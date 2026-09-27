using System.Threading.Channels;

namespace SharpAstro.Serial.Backends;

/// <summary>
/// One end of an in-memory null-modem cable: what this end writes, the other end reads. Nothing here blocks a
/// thread: a read with nothing buffered awaits the channel, a write never waits (the channel is unbounded), and a
/// read takes everything already there, which is what a UART's receive buffer does.
/// </summary>
internal sealed class LoopbackBackend(ChannelReader<byte> incoming, ChannelWriter<byte> outgoing) : ISerialBackend
{
    private volatile bool _open;

    public bool IsOpen => _open;

    public ValueTask OpenAsync(CancellationToken cancellationToken)
    {
        _open = true;
        return ValueTask.CompletedTask;
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        while (true)
        {
            var got = ReadAvailable(buffer.Span);
            if (got > 0)
            {
                return got;
            }
            if (!await incoming.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new IOException("The other end of the loopback is closed.");
            }
        }
    }

    public int ReadAvailable(Span<byte> buffer)
    {
        var n = 0;
        while (n < buffer.Length && incoming.TryRead(out var b))
        {
            buffer[n++] = b;
        }
        return n;
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        foreach (var b in data.Span)
        {
            // An unbounded channel always takes the byte while it is open.
            if (!outgoing.TryWrite(b))
            {
                throw new IOException("The other end of the loopback is closed.");
            }
        }
        return ValueTask.CompletedTask;
    }

    public void DiscardInBuffer()
    {
        while (incoming.TryRead(out _))
        {
        }
    }

    public bool Dtr { get; set; }

    public bool Rts { get; set; }

    public ValueTask CloseAsync()
    {
        _open = false;
        return ValueTask.CompletedTask;
    }
}
