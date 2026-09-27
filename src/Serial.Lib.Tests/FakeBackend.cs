using System.Collections.Concurrent;
using System.Threading.Channels;
using SharpAstro.Serial.Backends;

namespace SharpAstro.Serial.Tests;

/// <summary>
/// A scripted transport: bytes fed from the test arrive at the next read, and a write, an open or a close can be
/// made to hang (until released) or to throw, which is how the core's deadlines and guards are exercised without
/// hardware. It waits on a channel and on a gate, never by blocking a thread.
/// </summary>
internal sealed class FakeBackend : ISerialBackend
{
    private readonly Channel<byte> _rx = Channel.CreateUnbounded<byte>();
    private readonly ConcurrentQueue<byte[]> _written = new ConcurrentQueue<byte[]>();
    private volatile bool _open;

    /// <summary>Released to let a hung write, open or close finish.</summary>
    public Gate Release { get; } = new Gate();

    public bool BlockWrites { get; set; }
    public bool BlockOpen { get; set; }
    public bool BlockClose { get; set; }
    public Exception? WriteFault { get; set; }
    public Exception? ReadFault { get; set; }
    public Exception? OpenFault { get; set; }
    public int CloseCalls;

    public IReadOnlyList<byte[]> Written => [.. _written];

    public int BytesToRead => _rx.Reader.Count;

    public void Feed(string ascii)
    {
        foreach (var c in ascii)
        {
            _rx.Writer.TryWrite((byte)c);
        }
    }

    public bool IsOpen => _open;

    public async ValueTask OpenAsync(CancellationToken cancellationToken)
    {
        if (OpenFault is { } fault)
        {
            throw fault;
        }
        if (BlockOpen)
        {
            await Release.Task;
        }
        _open = true;
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (ReadFault is { } fault)
            {
                throw fault;
            }
            var got = ReadAvailable(buffer.Span);
            if (got > 0)
            {
                return got;
            }
            await _rx.Reader.WaitToReadAsync(cancellationToken);
        }
    }

    public int ReadAvailable(Span<byte> buffer)
    {
        var n = 0;
        while (n < buffer.Length && _rx.Reader.TryRead(out var b))
        {
            buffer[n++] = b;
        }
        return n;
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (WriteFault is { } fault)
        {
            throw fault;
        }
        var copy = data.ToArray();
        if (BlockWrites)
        {
            // A driver that never completes the write and ignores the token, as bthmodem.sys does.
            await Release.Task;
        }
        _written.Enqueue(copy);
    }

    public void DiscardInBuffer()
    {
        while (_rx.Reader.TryRead(out _))
        {
        }
    }

    public bool Dtr { get; set; }

    public bool Rts { get; set; }

    public async ValueTask CloseAsync()
    {
        Interlocked.Increment(ref CloseCalls);
        if (BlockClose)
        {
            await Release.Task;
        }
        _open = false;
    }

    /// <summary>A one-shot gate a hung operation awaits.</summary>
    internal sealed class Gate
    {
        private readonly TaskCompletionSource _tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Task => _tcs.Task;

        public void Set() => _tcs.TrySetResult();
    }
}
