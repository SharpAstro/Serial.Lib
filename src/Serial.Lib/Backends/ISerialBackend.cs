namespace SharpAstro.Serial.Backends;

/// <summary>
/// The OS seam under <see cref="SerialPortCore"/>: plain BLOCKING calls, each bounded by what the call says. Every
/// guarantee of <see cref="ISerialPort"/> (deadlines, cancellation, framing, the carry-over, abandoned I/O, removal)
/// is built above this, once, so a backend only has to be a faithful blocking transport. The managed one wraps
/// <c>System.IO.Ports</c>; a native Win32 one (P2) will drive the handle itself.
/// </summary>
internal interface ISerialBackend
{
    void Open();

    bool IsOpen { get; }

    /// <summary>Blocks for at most <paramref name="timeout"/> for at least one byte.</summary>
    /// <returns>The bytes read, or 0 when the timeout passed with none.</returns>
    int Read(Span<byte> buffer, TimeSpan timeout);

    /// <summary>Blocks until the driver has every byte.</summary>
    /// <exception cref="TimeoutException">The driver gave the write up at its own write timeout.</exception>
    void Write(ReadOnlySpan<byte> data);

    /// <summary>Bytes the driver holds that a read would return at once.</summary>
    int BytesToRead { get; }

    void DiscardInBuffer();

    bool Dtr { get; set; }

    bool Rts { get; set; }

    void Close();
}
