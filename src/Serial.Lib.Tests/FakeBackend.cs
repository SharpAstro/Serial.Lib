using System.Collections.Concurrent;
using SharpAstro.Serial.Backends;

namespace SharpAstro.Serial.Tests;

/// <summary>
/// A scripted blocking transport: bytes fed from the test arrive at the next read, and a write, an open or a close
/// can be made to block (until released) or to throw, which is how the core's deadlines and guards are exercised
/// without hardware.
/// </summary>
internal sealed class FakeBackend : ISerialBackend
{
    private readonly BlockingCollection<byte> _rx = new BlockingCollection<byte>();
    private readonly ConcurrentQueue<byte[]> _written = new ConcurrentQueue<byte[]>();
    private volatile bool _open;

    /// <summary>Released to let a blocked write, open or close finish.</summary>
    public ManualResetEventSlim Release { get; } = new ManualResetEventSlim(false);

    public bool BlockWrites { get; set; }
    public bool BlockOpen { get; set; }
    public bool BlockClose { get; set; }
    public Exception? WriteFault { get; set; }
    public Exception? ReadFault { get; set; }
    public Exception? OpenFault { get; set; }
    public int CloseCalls;

    public IReadOnlyList<byte[]> Written => [.. _written];

    public void Feed(string ascii)
    {
        foreach (var c in ascii)
        {
            _rx.Add((byte)c);
        }
    }

    public bool IsOpen => _open;

    public void Open()
    {
        if (OpenFault is { } fault)
        {
            throw fault;
        }
        if (BlockOpen)
        {
            Release.Wait();
        }
        _open = true;
    }

    public int Read(Span<byte> buffer, TimeSpan timeout)
    {
        if (ReadFault is { } fault)
        {
            throw fault;
        }
        if (!_rx.TryTake(out var first, timeout))
        {
            return 0;
        }
        buffer[0] = first;
        var n = 1;
        while (n < buffer.Length && _rx.TryTake(out var next))
        {
            buffer[n++] = next;
        }
        return n;
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (WriteFault is { } fault)
        {
            throw fault;
        }
        var copy = data.ToArray();
        if (BlockWrites)
        {
            Release.Wait();
        }
        _written.Enqueue(copy);
    }

    public int BytesToRead => _rx.Count;

    public void DiscardInBuffer()
    {
        while (_rx.TryTake(out _))
        {
        }
    }

    public bool Dtr { get; set; }

    public bool Rts { get; set; }

    public void Close()
    {
        Interlocked.Increment(ref CloseCalls);
        if (BlockClose)
        {
            Release.Wait();
        }
        _open = false;
    }
}
