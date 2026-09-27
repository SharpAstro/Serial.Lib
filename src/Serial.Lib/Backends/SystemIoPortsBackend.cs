using System.IO.Ports;

namespace SharpAstro.Serial.Backends;

/// <summary>
/// The managed backend: <see cref="SerialPort"/> used only through its BLOCKING calls. Its <c>BaseStream</c> async
/// reads are what this library exists to avoid: on a CH34x bridge the first succeeds and every later one aborts
/// with <c>ERROR_OPERATION_ABORTED</c> while the reply still arrives, so replies land one frame late; the "async"
/// is a blocking read on a pool thread anyway (dotnet/runtime#28968), and it ignores <c>ReadTimeout</c>. A blocking
/// <c>Read</c> honours <c>ReadTimeout</c> and is immune to the abort.
/// </summary>
internal sealed class SystemIoPortsBackend : ISerialBackend
{
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

    public void Open() => _port.Open();

    public int Read(Span<byte> buffer, TimeSpan timeout)
    {
        // On Windows a ReadTimeout of n ms sets COMMTIMEOUTS to return at once with whatever is buffered, or wait
        // up to n ms for the first byte, so this returns as soon as anything arrives. Set only when it changes:
        // each set is a SetCommTimeouts call.
        var ms = ToMilliseconds(timeout);
        if (ms != _readTimeoutMs)
        {
            _port.ReadTimeout = ms;
            _readTimeoutMs = ms;
        }

        try
        {
            var got = _port.Read(_scratch, 0, Math.Min(buffer.Length, _scratch.Length));
            _scratch.AsSpan(0, got).CopyTo(buffer);
            return got;
        }
        catch (TimeoutException)
        {
            return 0;
        }
    }

    public void Write(ReadOnlySpan<byte> data) => _port.BaseStream.Write(data);

    public int BytesToRead => _port.BytesToRead;

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

    public void Close() => _port.Close();

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
