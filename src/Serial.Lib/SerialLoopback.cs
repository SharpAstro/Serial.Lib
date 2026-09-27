using System.Threading.Channels;
using SharpAstro.Serial.Backends;

namespace SharpAstro.Serial;

/// <summary>
/// Two ports wired to each other in memory, for testing code that talks serial without hardware: what one writes,
/// the other reads. They are real ports in every respect but the wire (deadlines, cancellation, framing, the
/// carry-over, the close), so a test through them exercises the same guarantees a COM port gets.
/// </summary>
public static class SerialLoopback
{
    /// <summary>Creates a connected pair, both open.</summary>
    /// <param name="settings">Both ends' settings (the baud rate and line format are not modelled).</param>
    /// <param name="timeProvider">The clock their deadlines run on; the system clock when null.</param>
    public static (ISerialPort First, ISerialPort Second) CreatePair(SerialSettings settings, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        var time = timeProvider ?? TimeProvider.System;
        var options = new UnboundedChannelOptions { SingleReader = true, SingleWriter = true };
        var toSecond = Channel.CreateUnbounded<byte>(options);
        var toFirst = Channel.CreateUnbounded<byte>(options);
        return (Open("loopback-1", new LoopbackBackend(toFirst.Reader, toSecond.Writer), settings, time),
                Open("loopback-2", new LoopbackBackend(toSecond.Reader, toFirst.Writer), settings, time));
    }

    private static SerialPortCore Open(string name, LoopbackBackend backend, SerialSettings settings, TimeProvider time)
    {
        // Completes synchronously: a loopback end has nothing to open.
        _ = backend.OpenAsync(CancellationToken.None);
        return new SerialPortCore(backend, name, settings, time, static _ => true, time.GetUtcNow());
    }
}
