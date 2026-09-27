using SharpAstro.Serial.Backends;

namespace SharpAstro.Serial;

/// <summary>
/// Every guarantee of <see cref="ISerialPort"/>, built once over an <see cref="ISerialBackend"/>: whole-read deadlines
/// (a timer-driven token linked with the caller's), framing with a carry-over for bytes past a terminator, the
/// abandoned-write guard, a bounded close, and the classification of a backend fault as a removed device or an
/// ordinary one. Nothing here blocks a thread; a backend that has to (the managed one) does it behind its own seam.
/// </summary>
internal sealed class SerialPortCore : ISerialPort
{
    private readonly ISerialBackend _backend;
    private readonly TimeProvider _time;
    private readonly Func<string, bool> _portExists;
    private readonly byte[] _rx = new byte[4096];
    private int _rxStart;
    private int _rxEnd;
    private int _reading;
    private int _writing;
    private int _abandoned;
    private int _closed;
    private Task<bool>? _closing;

    public SerialPortCore(ISerialBackend backend, string portName, SerialSettings settings, TimeProvider time, Func<string, bool> portExists, DateTimeOffset openedAt)
    {
        _backend = backend;
        _time = time;
        _portExists = portExists;
        PortName = portName;
        Settings = settings;
        OpenedAt = openedAt;
    }

    public string PortName { get; }

    public SerialSettings Settings { get; }

    public DateTimeOffset OpenedAt { get; }

    public bool IsOpen => Volatile.Read(ref _closed) == 0 && _backend.IsOpen;

    public bool HasAbandonedIo => Volatile.Read(ref _abandoned) != 0;

    public bool Dtr
    {
        get => Guard(() => _backend.Dtr);
        set => Guard(() => _backend.Dtr = value);
    }

    public bool Rts
    {
        get => Guard(() => _backend.Rts);
        set => Guard(() => _backend.Rts = value);
    }

    public async ValueTask<int> ReadTerminatedAsync(Memory<byte> buffer, ReadOnlyMemory<byte> terminators, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (terminators.IsEmpty)
        {
            throw new ArgumentException("At least one terminator is needed.", nameof(terminators));
        }
        ThrowIfInvalidReadTimeout(timeout, nameof(timeout));
        ThrowIfClosed();
        cancellationToken.ThrowIfCancellationRequested();
        EnterRead();
        try
        {
            var copied = 0;
            // A reply already in the carry-over completes without a timer or a wait.
            if (TakeTerminated(buffer.Span, terminators.Span, ref copied))
            {
                return copied;
            }
            using var deadline = Deadline(timeout);
            using var linked = deadline is null ? null : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
            var token = linked?.Token ?? cancellationToken;
            while (true)
            {
                await FillAsync(token, deadline, timeout, buffer, copied, cancellationToken).ConfigureAwait(false);
                if (TakeTerminated(buffer.Span, terminators.Span, ref copied))
                {
                    return copied;
                }
            }
        }
        finally
        {
            Volatile.Write(ref _reading, 0);
        }
    }

    public async ValueTask ReadExactlyAsync(Memory<byte> buffer, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ThrowIfInvalidReadTimeout(timeout, nameof(timeout));
        ThrowIfClosed();
        cancellationToken.ThrowIfCancellationRequested();
        EnterRead();
        try
        {
            var copied = 0;
            if (TakeExactly(buffer.Span, ref copied))
            {
                return;
            }
            using var deadline = Deadline(timeout);
            using var linked = deadline is null ? null : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
            var token = linked?.Token ?? cancellationToken;
            while (true)
            {
                await FillAsync(token, deadline, timeout, buffer, copied, cancellationToken).ConfigureAwait(false);
                if (TakeExactly(buffer.Span, ref copied))
                {
                    return;
                }
            }
        }
        finally
        {
            Volatile.Write(ref _reading, 0);
        }
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        ThrowIfClosed();
        if (HasAbandonedIo)
        {
            // A port that did not complete one write will not complete the next; every attempt would cost a full
            // deadline and strand another operation on the driver.
            throw new SerialIoAbandonedException(PortName, $"{PortName} did not complete an earlier write, so it is not written to again.", Settings.WriteTimeout);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _writing, 1, 0) != 0)
        {
            throw new InvalidOperationException($"A write is already in progress on {PortName}; a port has one writer.");
        }

        var abandoned = false;
        try
        {
            // Started outside the wait so a write that FAILED can be told from one still PENDING at the deadline.
            var write = WriteBackendAsync(data, cancellationToken);
            try
            {
                await write.WaitAsync(Settings.WriteTimeout, _time, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException || (ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
            {
                if (write.IsCompleted)
                {
                    // It finished in the race with the deadline: its own outcome stands.
                    await write.ConfigureAwait(false);
                    return;
                }

                abandoned = true;
                Volatile.Write(ref _abandoned, 1);
                // Observe whatever the stranded write eventually does (an abort once the handle closes).
                _ = write.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                if (ex is OperationCanceledException)
                {
                    throw;
                }
                throw new SerialIoAbandonedException(PortName,
                    $"{PortName} did not complete a {data.Length}-byte write within {Settings.WriteTimeout.TotalMilliseconds:0} ms; the port is given up.",
                    Settings.WriteTimeout);
            }
        }
        finally
        {
            // An abandoned write still owns the writer slot; HasAbandonedIo refuses every later write anyway.
            if (!abandoned)
            {
                Volatile.Write(ref _writing, 0);
            }
        }
    }

    public int DiscardInput(Span<byte> drained)
    {
        ThrowIfClosed();
        EnterRead();
        try
        {
            var count = Math.Min(drained.Length, _rxEnd - _rxStart);
            _rx.AsSpan(_rxStart, count).CopyTo(drained);
            _rxStart = _rxEnd = 0;

            // What the backend holds now, up to the caller's room, then the rest goes to the native discard.
            while (count < drained.Length)
            {
                int got;
                try
                {
                    got = _backend.ReadAvailable(drained[count..]);
                }
                catch (Exception ex) when (IsBackendFault(ex))
                {
                    throw Translate(ex, isWrite: false);
                }
                if (got == 0)
                {
                    break;
                }
                count += got;
            }
            Guard(_backend.DiscardInBuffer);
            return count;
        }
        finally
        {
            Volatile.Write(ref _reading, 0);
        }
    }

    public async ValueTask<bool> CloseAsync()
    {
        // Published before the work, so a racing second close joins this one rather than starting its own.
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.CompareExchange(ref _closing, tcs.Task, null) is { } prior)
        {
            return await prior.ConfigureAwait(false);
        }

        Volatile.Write(ref _closed, 1);
        var close = _backend.CloseAsync().AsTask();
        bool closed;
        try
        {
            await close.WaitAsync(Settings.CloseTimeout, _time).ConfigureAwait(false);
            closed = true;
        }
        catch (TimeoutException)
        {
            // The driver still holds I/O it never completed; abandon the handle, not the caller.
            _ = close.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            closed = false;
        }
        catch (Exception)
        {
            // A close that threw (a removed device) has still released the handle.
            closed = true;
        }
        tcs.SetResult(closed);
        return closed;
    }

    public async ValueTask DisposeAsync() => await CloseAsync().ConfigureAwait(false);

    /// <summary>A read deadline is positive, or <see cref="Timeout.InfiniteTimeSpan"/> for none (the token alone ends the read).</summary>
    internal static void ThrowIfInvalidReadTimeout(TimeSpan timeout, string paramName)
    {
        if (timeout != Timeout.InfiniteTimeSpan && timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(paramName, timeout, "A read timeout must be positive, or Timeout.InfiniteTimeSpan for none.");
        }
    }

    private CancellationTokenSource? Deadline(TimeSpan timeout)
        => timeout == Timeout.InfiniteTimeSpan ? null : new CancellationTokenSource(timeout, _time);

    /// <summary>
    /// Moves the carry-over into <paramref name="buffer"/> up to a terminator.
    /// </summary>
    /// <returns>True with the reply complete in <c>buffer[..copied]</c>; false when more bytes are needed.</returns>
    private bool TakeTerminated(Span<byte> buffer, ReadOnlySpan<byte> terminators, ref int copied)
    {
        var available = _rxEnd - _rxStart;
        if (available == 0)
        {
            return false;
        }
        var room = buffer.Length - copied;
        // Up to `room` bytes of reply, plus one slot for its terminator.
        var window = _rx.AsSpan(_rxStart, Math.Min(available, room + 1));
        var at = window.IndexOfAny(terminators);
        if (at >= 0)
        {
            window[..at].CopyTo(buffer[copied..]);
            copied += at;
            _rxStart += at + 1;
            return true;
        }
        if (window.Length == room + 1)
        {
            window[..room].CopyTo(buffer[copied..]);
            copied += room;
            _rxStart += room;
            throw new SerialFramingException(PortName,
                $"{PortName}: no terminator within {buffer.Length} bytes; the reply is refused.", buffer[..copied].ToArray());
        }
        window.CopyTo(buffer[copied..]);
        copied += window.Length;
        _rxStart += window.Length;
        return false;
    }

    private bool TakeExactly(Span<byte> buffer, ref int copied)
    {
        var take = Math.Min(_rxEnd - _rxStart, buffer.Length - copied);
        _rx.AsSpan(_rxStart, take).CopyTo(buffer[copied..]);
        copied += take;
        _rxStart += take;
        return copied == buffer.Length;
    }

    /// <summary>
    /// Waits for the next bytes into the (empty) carry-over. The deadline's token firing, and not the caller's, is a
    /// <see cref="SerialTimeoutException"/> carrying what the read had consumed.
    /// </summary>
    private async ValueTask FillAsync(CancellationToken token, CancellationTokenSource? deadline, TimeSpan timeout,
        Memory<byte> buffer, int copied, CancellationToken cancellationToken)
    {
        _rxStart = _rxEnd = 0;
        try
        {
            _rxEnd = await _backend.ReadAsync(_rx, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline is { IsCancellationRequested: true })
        {
            throw new SerialTimeoutException(PortName,
                $"{PortName}: no complete reply within {timeout.TotalMilliseconds:0} ms ({copied} byte(s) received).",
                timeout, buffer[..copied].ToArray());
        }
        catch (Exception ex) when (IsBackendFault(ex))
        {
            throw Translate(ex, isWrite: false);
        }
    }

    private async Task WriteBackendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        try
        {
            await _backend.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new SerialTimeoutException(PortName, $"{PortName}: the driver gave up the write at its timeout.", Settings.WriteTimeout, innerException: ex);
        }
        catch (Exception ex) when (IsBackendFault(ex))
        {
            throw Translate(ex, isWrite: true);
        }
    }

    private T Guard<T>(Func<T> read)
    {
        ThrowIfClosed();
        try
        {
            return read();
        }
        catch (Exception ex) when (IsBackendFault(ex))
        {
            throw Translate(ex, isWrite: false);
        }
    }

    private void Guard(Action action)
    {
        ThrowIfClosed();
        try
        {
            action();
        }
        catch (Exception ex) when (IsBackendFault(ex))
        {
            throw Translate(ex, isWrite: false);
        }
    }

    private static bool IsBackendFault(Exception ex)
        => ex is IOException or UnauthorizedAccessException or InvalidOperationException or ObjectDisposedException && ex is not SerialException;

    /// <summary>
    /// A backend fault on a port we closed is the close's doing; on a port that is no longer enumerated it is a
    /// removed device; otherwise it is an ordinary fault, carried as a <see cref="SerialException"/>.
    /// </summary>
    private Exception Translate(Exception ex, bool isWrite)
    {
        if (Volatile.Read(ref _closed) != 0)
        {
            return new ObjectDisposedException(PortName, $"{PortName} was closed.");
        }
        if (!_portExists(PortName))
        {
            return new SerialPortRemovedException(PortName, ex);
        }
        return new SerialException(PortName, $"{PortName}: the {(isWrite ? "write" : "read")} failed: {ex.Message}", ex);
    }

    private void EnterRead()
    {
        if (Interlocked.CompareExchange(ref _reading, 1, 0) != 0)
        {
            throw new InvalidOperationException($"A read is already in progress on {PortName}; a port has one reader.");
        }
    }

    private void ThrowIfClosed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
}
