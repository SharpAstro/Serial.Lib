namespace SharpAstro.Serial.Backends;

/// <summary>
/// The transport seam under <see cref="SerialPortCore"/>. Every guarantee of <see cref="ISerialPort"/> (deadlines,
/// framing, the carry-over, abandoned I/O, removal, the bounded open and close) is built above this, once, so a
/// backend only has to be a faithful transport. It is asynchronous so a backend that can wait without a thread does:
/// the loopback waits on a channel, and a native Win32 backend (P2) will drive overlapped I/O. The managed backend
/// is the one that cannot, because the only reliable <c>System.IO.Ports</c> read is the blocking one, so its
/// blocking stays inside it.
/// </summary>
internal interface ISerialBackend
{
    ValueTask OpenAsync(CancellationToken cancellationToken);

    bool IsOpen { get; }

    /// <summary>Waits for at least one byte.</summary>
    /// <returns>The bytes read, never 0.</returns>
    /// <exception cref="OperationCanceledException">The token fired first. No read is left pending, and no byte is lost:
    /// a byte that arrived as the token fired is either returned or still in the backend.</exception>
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);

    /// <summary>What the backend holds now, without waiting.</summary>
    /// <returns>The bytes read, 0 when none are buffered.</returns>
    int ReadAvailable(Span<byte> buffer);

    /// <summary>Hands every byte to the transport.</summary>
    /// <exception cref="TimeoutException">The driver gave the write up at its own write timeout.</exception>
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    void DiscardInBuffer();

    bool Dtr { get; set; }

    bool Rts { get; set; }

    ValueTask CloseAsync();
}
