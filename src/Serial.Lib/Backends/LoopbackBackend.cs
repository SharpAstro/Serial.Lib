using System.Collections.Concurrent;

namespace SharpAstro.Serial.Backends;

/// <summary>
/// One end of an in-memory null-modem cable: what this end writes, the other end reads. Writes never block (the
/// queue is unbounded), and a read waits for the first byte then takes whatever else is already there, which is
/// what a UART's receive buffer does.
/// </summary>
internal sealed class LoopbackBackend(BlockingCollection<byte> incoming, BlockingCollection<byte> outgoing) : ISerialBackend
{
    private volatile bool _open;

    public bool IsOpen => _open;

    public void Open() => _open = true;

    public int Read(Span<byte> buffer, TimeSpan timeout)
    {
        if (buffer.IsEmpty || !incoming.TryTake(out var first, timeout))
        {
            return 0;
        }
        buffer[0] = first;
        var n = 1;
        while (n < buffer.Length && incoming.TryTake(out var next))
        {
            buffer[n++] = next;
        }
        return n;
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            outgoing.Add(b);
        }
    }

    public int BytesToRead => incoming.Count;

    public void DiscardInBuffer()
    {
        while (incoming.TryTake(out _))
        {
        }
    }

    public bool Dtr { get; set; }

    public bool Rts { get; set; }

    public void Close() => _open = false;
}
