namespace SharpAstro.Serial;

/// <summary>The parity bit of a serial frame.</summary>
public enum SerialParity
{
    None,
    Odd,
    Even,
    Mark,
    Space,
}

/// <summary>The stop bits of a serial frame.</summary>
public enum SerialStopBits
{
    One,
    OnePointFive,
    Two,
}

/// <summary>
/// How one port is opened and how long each operation on it may take. Handshaking is never enabled: a
/// command/response device answers inside the driver's buffer, and flow control is what lets a write block.
/// </summary>
/// <param name="BaudRate">The line rate, e.g. 9600.</param>
public sealed record SerialSettings(int BaudRate)
{
    /// <summary>Data bits per frame, 5 to 8.</summary>
    public int DataBits { get; init; } = 8;

    public SerialParity Parity { get; init; } = SerialParity.None;

    public SerialStopBits StopBits { get; init; } = SerialStopBits.One;

    /// <summary>
    /// Assert DTR from the open on. Some USB bridges hold the device's MCU in reset until DTR is asserted
    /// (the Gemini FlatPanel's CH341). Note that opening a CH340-based board resets it whatever this says:
    /// the reset is the board's, and <see cref="ISerialPort.OpenedAt"/> is what lets a driver wait it out.
    /// </summary>
    public bool AssertDtr { get; init; }

    /// <summary>Assert RTS from the open on.</summary>
    public bool AssertRts { get; init; }

    /// <summary>
    /// How long a whole read may take, from the call to its last byte, unless the call names its own.
    /// A read that runs out of it throws <see cref="SerialTimeoutException"/>, never a default value.
    /// </summary>
    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long a write may take. A healthy port absorbs a command in milliseconds; one that is still
    /// holding the write past this is given up (<see cref="ISerialPort.HasAbandonedIo"/>).
    /// </summary>
    public TimeSpan WriteTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long the open may take. Opening an outgoing Bluetooth serial port connects to the remote device
    /// first, which can take many seconds or never finish.
    /// </summary>
    public TimeSpan OpenTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long the close may take before the handle is abandoned rather than the caller.</summary>
    public TimeSpan CloseTimeout { get; init; } = TimeSpan.FromSeconds(2);

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(BaudRate, nameof(BaudRate));
        if (DataBits is < 5 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(DataBits), DataBits, "Data bits must be 5 to 8.");
        }
        foreach (var (name, value) in new[] { (nameof(ReadTimeout), ReadTimeout), (nameof(WriteTimeout), WriteTimeout),
                                              (nameof(OpenTimeout), OpenTimeout), (nameof(CloseTimeout), CloseTimeout) })
        {
            if (value <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(name, value, "A timeout must be positive.");
            }
        }
    }
}
