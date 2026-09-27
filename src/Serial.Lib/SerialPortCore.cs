using SharpAstro.Serial.Backends;

namespace SharpAstro.Serial;

/// <summary>
/// Every guarantee of <see cref="ISerialPort"/>, built once over a blocking <see cref="ISerialBackend"/>: whole-read
/// deadlines, cancellation observed between short read slices (so no blocked thread is ever abandoned by a read),
/// framing with a carry-over for bytes past a terminator, the abandoned-write guard, a bounded close, and the
/// classification of a backend fault as a removed device or an ordinary one.
/// </summary>
internal sealed class SerialPortCore : ISerialPort
{
    /// <summary>
    /// The longest one blocking read waits before the loop looks at the token and the deadline again: short enough
    /// that a cancel is seen promptly, long enough not to spin.
    /// </summary>
    internal static readonly TimeSpan ReadSlice = TimeSpan.FromMilliseconds(200);

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
            // A reply already sitting in the carry-over completes without a thread hop.
            if (_rx.AsSpan(_rxStart, _rxEnd - _rxStart).IndexOfAny(terminators.Span) >= 0)
            {
                return ReadTerminatedBlocking(buffer, terminators, timeout, cancellationToken);
            }
            return await Task.Run(() => ReadTerminatedBlocking(buffer, terminators, timeout, cancellationToken), CancellationToken.None).ConfigureAwait(false);
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
            if (_rxEnd - _rxStart >= buffer.Length)
            {
                ReadExactlyBlocking(buffer, timeout, cancellationToken);
                return;
            }
            await Task.Run(() => ReadExactlyBlocking(buffer, timeout, cancellationToken), CancellationToken.None).ConfigureAwait(false);
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
            // deadline and strand another thread.
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
            var write = Task.Run(() => Classify(() => _backend.Write(data.Span), isWrite: true), CancellationToken.None);
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

            // What the driver holds now: taken at once (a one-millisecond slice returns whatever is buffered), up to
            // the caller's room, then the rest goes to the native discard.
            while (count < drained.Length && Guard(() => _backend.BytesToRead) > 0)
            {
                var room = count;
                var got = ReadBackend(drained[room..], TimeSpan.FromMilliseconds(1));
                if (got <= 0)
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
        var close = Task.Run(_backend.Close, CancellationToken.None);
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

    private int ReadTerminatedBlocking(Memory<byte> buffer, ReadOnlyMemory<byte> terminators, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var start = _time.GetTimestamp();
        var span = buffer.Span;
        var term = terminators.Span;
        var copied = 0;
        while (true)
        {
            var available = _rxEnd - _rxStart;
            if (available > 0)
            {
                var room = span.Length - copied;
                // Up to `room` bytes of reply, plus one slot for its terminator.
                var window = _rx.AsSpan(_rxStart, Math.Min(available, room + 1));
                var at = window.IndexOfAny(term);
                if (at >= 0)
                {
                    window[..at].CopyTo(span[copied..]);
                    copied += at;
                    _rxStart += at + 1;
                    return copied;
                }
                if (window.Length == room + 1)
                {
                    window[..room].CopyTo(span[copied..]);
                    copied += room;
                    _rxStart += room;
                    throw new SerialFramingException(PortName,
                        $"{PortName}: no terminator within {span.Length} bytes; the reply is refused.", span[..copied].ToArray());
                }
                window.CopyTo(span[copied..]);
                copied += window.Length;
                _rxStart += window.Length;
            }

            _rxStart = _rxEnd = 0;
            FillOrFail(start, timeout, span[..copied], cancellationToken);
        }
    }

    private void ReadExactlyBlocking(Memory<byte> buffer, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var start = _time.GetTimestamp();
        var span = buffer.Span;
        var copied = 0;
        while (true)
        {
            var take = Math.Min(_rxEnd - _rxStart, span.Length - copied);
            _rx.AsSpan(_rxStart, take).CopyTo(span[copied..]);
            copied += take;
            _rxStart += take;
            if (copied == span.Length)
            {
                return;
            }

            _rxStart = _rxEnd = 0;
            FillOrFail(start, timeout, span[..copied], cancellationToken);
        }
    }

    /// <summary>
    /// One slice of waiting: throws for the token or the deadline first, then reads at most one slice into the
    /// (empty) carry-over.
    /// </summary>
    private void FillOrFail(long start, TimeSpan timeout, ReadOnlySpan<byte> received, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (timeout == Timeout.InfiniteTimeSpan)
        {
            _rxEnd = ReadBackend(_rx, ReadSlice);
            return;
        }
        var remaining = timeout - _time.GetElapsedTime(start);
        if (remaining <= TimeSpan.Zero)
        {
            throw new SerialTimeoutException(PortName,
                $"{PortName}: no complete reply within {timeout.TotalMilliseconds:0} ms ({received.Length} byte(s) received).",
                timeout, received.ToArray());
        }
        _rxEnd = ReadBackend(_rx, remaining < ReadSlice ? remaining : ReadSlice);
    }

    /// <summary>A read deadline is positive, or <see cref="Timeout.InfiniteTimeSpan"/> for none (the token alone ends the read).</summary>
    internal static void ThrowIfInvalidReadTimeout(TimeSpan timeout, string paramName)
    {
        if (timeout != Timeout.InfiniteTimeSpan && timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(paramName, timeout, "A read timeout must be positive, or Timeout.InfiniteTimeSpan for none.");
        }
    }

    private int ReadBackend(Span<byte> into, TimeSpan timeout)
    {
        try
        {
            return _backend.Read(into, timeout);
        }
        catch (Exception ex) when (IsBackendFault(ex))
        {
            throw Translate(ex, isWrite: false);
        }
    }

    private void Classify(Action action, bool isWrite)
    {
        try
        {
            action();
        }
        catch (TimeoutException ex) when (isWrite)
        {
            throw new SerialTimeoutException(PortName, $"{PortName}: the driver gave up the write at its timeout.", Settings.WriteTimeout, innerException: ex);
        }
        catch (Exception ex) when (IsBackendFault(ex))
        {
            throw Translate(ex, isWrite);
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
        Classify(action, isWrite: false);
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
