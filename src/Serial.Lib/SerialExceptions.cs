namespace SharpAstro.Serial;

/// <summary>
/// A serial port fault. Every exception this library throws for a port derives from it, and it derives from
/// <see cref="IOException"/>, so a caller's I/O-fault handling (a retry, a reconnect) covers all of them.
/// </summary>
public class SerialException : IOException
{
    public SerialException(string portName, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        PortName = portName;
    }

    /// <summary>The port the fault happened on.</summary>
    public string PortName { get; }
}

/// <summary>
/// An operation did not finish within its deadline. For a read, <see cref="Received"/> holds what did arrive
/// (consumed from the stream, so it cannot contaminate the next read).
/// </summary>
public class SerialTimeoutException : SerialException
{
    public SerialTimeoutException(string portName, string message, TimeSpan timeout, ReadOnlyMemory<byte> received = default, Exception? innerException = null)
        : base(portName, message, innerException)
    {
        Timeout = timeout;
        Received = received;
    }

    /// <summary>The deadline that passed.</summary>
    public TimeSpan Timeout { get; }

    /// <summary>The bytes a read consumed before its deadline passed; empty for a write or an open.</summary>
    public ReadOnlyMemory<byte> Received { get; }
}

/// <summary>
/// A write was still pending when its deadline or the caller's token fired: the driver never completed it, and
/// the port is given up (<see cref="ISerialPort.HasAbandonedIo"/>). Distinct from a device that does not answer.
/// </summary>
public sealed class SerialIoAbandonedException : SerialTimeoutException
{
    public SerialIoAbandonedException(string portName, string message, TimeSpan timeout)
        : base(portName, message, timeout)
    {
    }
}

/// <summary>
/// A terminated read found no terminator within the buffer it was given: the reply is refused, never truncated.
/// The rest of that reply may still be in the stream; <see cref="ISerialPort.DiscardInput"/> before the next
/// exchange.
/// </summary>
public sealed class SerialFramingException : SerialException
{
    public SerialFramingException(string portName, string message, ReadOnlyMemory<byte> received)
        : base(portName, message)
    {
        Received = received;
    }

    /// <summary>The bytes consumed, a full buffer of them.</summary>
    public ReadOnlyMemory<byte> Received { get; }
}

/// <summary>
/// The device went away: the port faulted and is no longer enumerated (a USB cable pulled). Distinct from a
/// timeout, so a driver can mark its state uncertain rather than retry into a port that is not there.
/// </summary>
public sealed class SerialPortRemovedException : SerialException
{
    public SerialPortRemovedException(string portName, Exception? innerException = null)
        : base(portName, $"{portName} is no longer present (the device was removed).", innerException)
    {
    }
}

/// <summary>The port exists but another handle holds it.</summary>
public sealed class SerialPortBusyException : SerialException
{
    public SerialPortBusyException(string portName, Exception? innerException = null)
        : base(portName, $"{portName} is in use by another program or handle.", innerException)
    {
    }
}

/// <summary>No such port.</summary>
public sealed class SerialPortNotFoundException : SerialException
{
    public SerialPortNotFoundException(string portName, Exception? innerException = null)
        : base(portName, $"There is no serial port {portName}.", innerException)
    {
    }
}
