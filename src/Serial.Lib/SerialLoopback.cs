using System.Collections.Concurrent;
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
        // Each queue is shared by the two ends for the pair's whole life, so neither end may dispose it; a
        // BlockingCollection's SemaphoreSlim creates no kernel handle unless one is asked for, so there is nothing
        // to leak.
#pragma warning disable CA2000
        var toSecond = new BlockingCollection<byte>();
        var toFirst = new BlockingCollection<byte>();
#pragma warning restore CA2000
        return (Open("loopback-1", new LoopbackBackend(toFirst, toSecond), settings, time),
                Open("loopback-2", new LoopbackBackend(toSecond, toFirst), settings, time));
    }

    private static SerialPortCore Open(string name, LoopbackBackend backend, SerialSettings settings, TimeProvider time)
    {
        backend.Open();
        return new SerialPortCore(backend, name, settings, time, static _ => true, time.GetUtcNow());
    }
}
