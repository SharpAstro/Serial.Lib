using System.IO.Ports;

namespace SharpAstro.Serial.Backends;

/// <summary>
/// The managed backend: <see cref="SerialPort"/> used only through its BLOCKING calls, because its async surface is
/// what this library exists to avoid. On a CH34x bridge the first <c>BaseStream.ReadAsync</c> succeeds and every later
/// one aborts with <c>ERROR_OPERATION_ABORTED</c> while the reply still arrives, so replies land one frame late; that
/// "async" is a blocking read on a pool thread anyway (dotnet/runtime#28968) and ignores <c>ReadTimeout</c>; and its
/// <c>WriteAsync</c> ignores its token (a Bluetooth port with nobody on the far end never completes one). So this is
/// the one place in the library that blocks a thread, and only when it must: a read with bytes already buffered is
/// answered at once, and a waiting read blocks in short slices, looking at its token between them.
/// </summary>
internal sealed class SystemIoPortsBackend : ISerialBackend
{
    /// <summary>
    /// The longest one blocking read waits before the loop looks at its token again: short enough that a cancel is
    /// seen promptly, long enough not to spin.
    /// </summary>
    internal static readonly TimeSpan ReadSlice = TimeSpan.FromMilliseconds(200);

    private readonly SerialPort _port;
    private readonly byte[] _scratch = new byte[4096];
    private int _readTimeoutMs = -2;

    public SystemIoPortsBackend(string portName, SerialSettings settings)
    {
        _port = new SerialPort(portName, settings.BaudRate, ToParity(settings.Parity), settings.DataBits, ToStopBits(settings.StopBits))
        {
            Handshake = Handshake.None,
            DtrEnable = settings.AssertDtr,
            RtsEnable = settings.AssertRts,
            // The port-level half of the write bound; SerialPortCore holds the task-level half for drivers that
            // ignore COMMTIMEOUTS (bthmodem.sys does).
            WriteTimeout = ToMilliseconds(settings.WriteTimeout),
        };
    }

    public bool IsOpen => _port.IsOpen;

    // SerialPort.Open is synchronous only; the core bounds how long it may take.
    public ValueTask OpenAsync(CancellationToken cancellationToken) => new ValueTask(Task.Run(_port.Open, CancellationToken.None));

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Bytes already buffered need no thread.
        var ready = ReadAvailable(buffer.Span);
        if (ready > 0)
        {
            return new ValueTask<int>(ready);
        }
        return new ValueTask<int>(Task.Run(() => ReadBlocking(buffer, cancellationToken), CancellationToken.None));
    }

    private int ReadBlocking(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        // On Windows a ReadTimeout of n ms sets COMMTIMEOUTS to return at once with whatever is buffered, or wait up to
        // n ms for the first byte, so a slice returns as soon as anything arrives.
        SetReadTimeout(ReadSlice);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var got = _port.Read(_scratch, 0, Math.Min(buffer.Length, _scratch.Length));
                if (got > 0)
                {
                    _scratch.AsSpan(0, got).CopyTo(buffer.Span);
                    return got;
                }
            }
            catch (TimeoutException)
            {
                // The slice passed with nothing; look at the token again.
            }
        }
    }

    public int ReadAvailable(Span<byte> buffer)
    {
        var available = _port.BytesToRead;
        if (available == 0 || buffer.IsEmpty)
        {
            return 0;
        }
        // Buffered bytes come back at once whatever the timeout; the smallest one keeps a race with a concurrent
        // discard from ever waiting.
        SetReadTimeout(TimeSpan.FromMilliseconds(1));
        try
        {
            var got = _port.Read(_scratch, 0, Math.Min(Math.Min(available, buffer.Length), _scratch.Length));
            _scratch.AsSpan(0, got).CopyTo(buffer);
            return got;
        }
        catch (TimeoutException)
        {
            return 0;
        }
    }

    // SerialStream.WriteAsync ignores its token, so the write is the blocking one, bounded by WriteTimeout here and by
    // the core's deadline for a driver that ignores COMMTIMEOUTS.
    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        => new ValueTask(Task.Run(() => _port.BaseStream.Write(data.Span), CancellationToken.None));

    public void DiscardInBuffer() => _port.DiscardInBuffer();

    public bool Dtr
    {
        get => _port.DtrEnable;
        set => _port.DtrEnable = value;
    }

    public bool Rts
    {
        get => _port.RtsEnable;
        set => _port.RtsEnable = value;
    }

    // SerialPort.Close blocks for as long as the driver takes to finish or cancel pending I/O; the core bounds it.
    public ValueTask CloseAsync() => new ValueTask(Task.Run(_port.Close, CancellationToken.None));

    private void SetReadTimeout(TimeSpan timeout)
    {
        // Each set is a SetCommTimeouts call, so only when it changes.
        var ms = ToMilliseconds(timeout);
        if (ms != _readTimeoutMs)
        {
            _port.ReadTimeout = ms;
            _readTimeoutMs = ms;
        }
    }

    private static int ToMilliseconds(TimeSpan timeout)
        => (int)Math.Clamp(Math.Ceiling(timeout.TotalMilliseconds), 1, int.MaxValue);

    private static Parity ToParity(SerialParity parity) => parity switch
    {
        SerialParity.None => Parity.None,
        SerialParity.Odd => Parity.Odd,
        SerialParity.Even => Parity.Even,
        SerialParity.Mark => Parity.Mark,
        SerialParity.Space => Parity.Space,
        _ => throw new ArgumentOutOfRangeException(nameof(parity), parity, null),
    };

    private static StopBits ToStopBits(SerialStopBits stopBits) => stopBits switch
    {
        SerialStopBits.One => StopBits.One,
        SerialStopBits.OnePointFive => StopBits.OnePointFive,
        SerialStopBits.Two => StopBits.Two,
        _ => throw new ArgumentOutOfRangeException(nameof(stopBits), stopBits, null),
    };
}
