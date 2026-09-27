namespace SharpAstro.Serial;

/// <summary>
/// One open serial port. Opened by <see cref="SerialPorts.OpenAsync(string, SerialSettings, CancellationToken)"/>.
/// </summary>
/// <remarks>
/// <para>The contract, which is the reason this library exists:</para>
/// <list type="bullet">
/// <item>A read ends one of four ways: it completes, it throws <see cref="SerialTimeoutException"/> once its deadline
/// passes, it throws <see cref="OperationCanceledException"/> once the caller's token fires, or it throws another
/// <see cref="SerialException"/> for a fault. It never returns a default value for a reply that did not come, and
/// cancelling it never leaves a read pending that could eat the next reply.</item>
/// <item>Bytes that arrive after a reply's terminator are kept for the next read, never dropped.</item>
/// <item>Bytes consumed by a read that then failed are gone from the stream (the exception carries them), so a
/// partial reply cannot contaminate the next exchange.</item>
/// <item>A port has one reader and one writer at a time; a second concurrent read (or write) throws
/// <see cref="InvalidOperationException"/> rather than interleaving.</item>
/// </list>
/// </remarks>
public interface ISerialPort : IAsyncDisposable
{
    /// <summary>The OS name of the port (<c>COM3</c>, <c>/dev/ttyUSB0</c>).</summary>
    string PortName { get; }

    SerialSettings Settings { get; }

    /// <summary>True from a successful open until the close.</summary>
    bool IsOpen { get; }

    /// <summary>
    /// When the open completed. Opening resets many boards (every CH340 one here), and some firmware saves state
    /// on a delay, so a driver may need to know how long ago the reset was.
    /// </summary>
    DateTimeOffset OpenedAt { get; }

    bool Dtr { get; set; }

    bool Rts { get; set; }

    /// <summary>
    /// True once a write was still pending when its deadline or the caller's token fired: the DRIVER never
    /// completed it. A device that does not answer is a failed read; a port that does not complete I/O is this,
    /// and every further write on it throws <see cref="SerialIoAbandonedException"/> at once. The Bluetooth
    /// listener port Windows creates for a paired Serial Port Profile device is the case that found it.
    /// </summary>
    bool HasAbandonedIo { get; }

    /// <summary>
    /// Reads until any byte of <paramref name="terminators"/>, which is consumed and not stored.
    /// </summary>
    /// <returns>The number of bytes stored in <paramref name="buffer"/>.</returns>
    /// <exception cref="SerialTimeoutException">No terminator within <paramref name="timeout"/>.</exception>
    /// <exception cref="SerialFramingException">No terminator within <paramref name="buffer"/>'s length: the reply is refused, never truncated.</exception>
    ValueTask<int> ReadTerminatedAsync(Memory<byte> buffer, ReadOnlyMemory<byte> terminators, TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <inheritdoc cref="ReadTerminatedAsync(Memory{byte}, ReadOnlyMemory{byte}, TimeSpan, CancellationToken)"/>
    ValueTask<int> ReadTerminatedAsync(Memory<byte> buffer, ReadOnlyMemory<byte> terminators, CancellationToken cancellationToken = default)
        => ReadTerminatedAsync(buffer, terminators, Settings.ReadTimeout, cancellationToken);

    /// <summary>Reads exactly <paramref name="buffer"/>'s length.</summary>
    /// <exception cref="SerialTimeoutException">Fewer bytes arrived within <paramref name="timeout"/>.</exception>
    ValueTask ReadExactlyAsync(Memory<byte> buffer, TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <inheritdoc cref="ReadExactlyAsync(Memory{byte}, TimeSpan, CancellationToken)"/>
    ValueTask ReadExactlyAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => ReadExactlyAsync(buffer, Settings.ReadTimeout, cancellationToken);

    /// <summary>Hands every byte to the driver within <see cref="SerialSettings.WriteTimeout"/>.</summary>
    /// <exception cref="SerialTimeoutException">The driver gave up the write at its own timeout.</exception>
    /// <exception cref="SerialIoAbandonedException">The write was still pending at the deadline (see <see cref="HasAbandonedIo"/>).</exception>
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    /// <summary>
    /// Empties the receive side: the bytes kept from earlier reads and what the driver holds now. Copies up to
    /// <paramref name="drained"/>'s length of them first, so a caller can log what the device actually sent
    /// (a stray byte from a timed-out probe is the usual culprit), then discards the rest.
    /// </summary>
    /// <returns>The number of bytes copied into <paramref name="drained"/>.</returns>
    int DiscardInput(Span<byte> drained);

    /// <summary>
    /// Closes the port within <see cref="SerialSettings.CloseTimeout"/>. Idempotent.
    /// </summary>
    /// <returns>False when the close did not finish in time: the handle is abandoned (the driver still holds I/O
    /// it never completed) and the next open of this port may find it busy.</returns>
    ValueTask<bool> CloseAsync();
}
