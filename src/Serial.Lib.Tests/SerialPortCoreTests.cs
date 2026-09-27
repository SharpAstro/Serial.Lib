using System.Diagnostics;
using System.Text;
using Shouldly;
using Xunit;

namespace SharpAstro.Serial.Tests;

public sealed class SerialPortCoreTests
{
    private static readonly byte[] Hash = "#"u8.ToArray();

    private static SerialSettings Settings(int readMs = 2000, int writeMs = 2000, int closeMs = 2000)
        => new SerialSettings(9600)
        {
            ReadTimeout = TimeSpan.FromMilliseconds(readMs),
            WriteTimeout = TimeSpan.FromMilliseconds(writeMs),
            CloseTimeout = TimeSpan.FromMilliseconds(closeMs),
        };

    private static async Task<ISerialPort> OpenAsync(FakeBackend backend, CancellationToken cancellationToken, SerialSettings? settings = null, Func<string, bool>? exists = null)
        => await SerialPorts.OpenCoreAsync(backend, "COM99", settings ?? Settings(), TimeProvider.System, exists ?? (_ => true), cancellationToken);

    private static async Task<string> ReadReplyAsync(ISerialPort port, CancellationToken cancellationToken, int bufferSize = 32)
    {
        var buffer = new byte[bufferSize];
        var n = await port.ReadTerminatedAsync(buffer, Hash, cancellationToken);
        return Encoding.ASCII.GetString(buffer, 0, n);
    }

    [Fact(Timeout = 10_000)]
    public async Task ATerminatedReplyComesBackWithoutItsTerminatorAndTheRestWaitsForTheNextRead()
    {
        var ct = TestContext.Current.CancellationToken;
        var backend = new FakeBackend();
        await using var port = await OpenAsync(backend, ct);
        backend.Feed("P7820#EOK#");

        (await ReadReplyAsync(port, ct)).ShouldBe("P7820");
        (await ReadReplyAsync(port, ct)).ShouldBe("EOK");
    }

    [Fact(Timeout = 10_000)]
    public async Task AReplySplitAcrossArrivalsIsJoined()
    {
        var ct = TestContext.Current.CancellationToken;
        var backend = new FakeBackend();
        await using var port = await OpenAsync(backend, ct);
        backend.Feed("P78");
        var late = Task.Run(async () =>
        {
            await Task.Delay(150, ct);
            backend.Feed("20#");
        }, ct);

        (await ReadReplyAsync(port, ct)).ShouldBe("P7820");
        await late;
    }

    [Fact(Timeout = 10_000)]
    public async Task ANoReplyReadThrowsATimeoutCarryingWhatArrived()
    {
        var ct = TestContext.Current.CancellationToken;
        var backend = new FakeBackend();
        await using var port = await OpenAsync(backend, ct, Settings(readMs: 400));
        backend.Feed("P78");

        var clock = Stopwatch.StartNew();
        var ex = await Should.ThrowAsync<SerialTimeoutException>(async () => await ReadReplyAsync(port, ct));

        clock.Elapsed.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(350));
        Encoding.ASCII.GetString(ex.Received.Span).ShouldBe("P78");
        ex.PortName.ShouldBe("COM99");
        ex.ShouldBeAssignableTo<IOException>();
    }

    [Fact(Timeout = 10_000)]
    public async Task ATimedOutPartialReplyDoesNotContaminateTheNextRead()
    {
        var ct = TestContext.Current.CancellationToken;
        var backend = new FakeBackend();
        await using var port = await OpenAsync(backend, ct, Settings(readMs: 300));
        backend.Feed("0");
        await Should.ThrowAsync<SerialTimeoutException>(async () => await ReadReplyAsync(port, ct));

        backend.Feed("EOK#");
        (await ReadReplyAsync(port, ct)).ShouldBe("EOK");
    }

    [Fact(Timeout = 10_000)]
    public async Task AReplyLongerThanTheBufferIsRefusedNotTruncated()
    {
        var ct = TestContext.Current.CancellationToken;
        var backend = new FakeBackend();
        await using var port = await OpenAsync(backend, ct);
        backend.Feed("ABCDE#");

        var ex = await Should.ThrowAsync<SerialFramingException>(async () => await ReadReplyAsync(port, ct, bufferSize: 4));
        Encoding.ASCII.GetString(ex.Received.Span).ShouldBe("ABCD");
    }

    [Fact(Timeout = 10_000)]
    public async Task AReplyExactlyAsLongAsTheBufferReads()
    {
        var ct = TestContext.Current.CancellationToken;
        var backend = new FakeBackend();
        await using var port = await OpenAsync(backend, ct);
        backend.Feed("ABCD#");

        (await ReadReplyAsync(port, ct, bufferSize: 4)).ShouldBe("ABCD");
    }

    [Fact(Timeout = 10_000)]
    public async Task ACancelledReadEndsPromptlyAndTheNextReadGetsItsReply()
    {
        var ct = TestContext.Current.CancellationToken;
        // The CH34x trap in one test: after a cancelled read, nothing may still be waiting to eat the next reply.
        var backend = new FakeBackend();
        await using var port = await OpenAsync(backend, ct, Settings(readMs: 8000));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(150);

        var clock = Stopwatch.StartNew();
        await Should.ThrowAsync<OperationCanceledException>(async () => await ReadReplyAsync(port, cts.Token));
        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2));

        backend.Feed("EOK#");
        (await ReadReplyAsync(port, ct)).ShouldBe("EOK");
    }

    [Fact(Timeout = 10_000)]
    public async Task ASecondConcurrentReadIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var backend = new FakeBackend();
        await using var port = await OpenAsync(backend, ct, Settings(readMs: 8000));
        var first = ReadReplyAsync(port, ct);

        await Should.ThrowAsync<InvalidOperationException>(async () => await ReadReplyAsync(port, ct));

        backend.Feed("EOK#");
        (await first).ShouldBe("EOK");
    }

    [Fact(Timeout = 10_000)]
    public async Task AnExactReadTakesItsLengthAndLeavesTheRest()
    {
        var ct = TestContext.Current.CancellationToken;
        var backend = new FakeBackend();
        await using var port = await OpenAsync(backend, ct, Settings(readMs: 300));
        backend.Feed("12345678");
        var buffer = new byte[5];

        await port.ReadExactlyAsync(buffer, ct);

        Encoding.ASCII.GetString(buffer).ShouldBe("12345");
        var rest = await Should.ThrowAsync<SerialTimeoutException>(async () => await ReadReplyAsync(port, ct, bufferSize: 8));
        Encoding.ASCII.GetString(rest.Received.Span).ShouldBe("678");
    }

    [Fact(Timeout = 10_000)]
    public async Task AShortExactReadTimesOutWithWhatArrived()
    {
        var ct = TestContext.Current.CancellationToken;
        var backend = new FakeBackend();
        await using var port = await OpenAsync(backend, ct, Settings(readMs: 300));
        backend.Feed("12");

        var ex = await Should.ThrowAsync<SerialTimeoutException>(async () => await port.ReadExactlyAsync(new byte[5], ct));
        Encoding.ASCII.GetString(ex.Received.Span).ShouldBe("12");
    }

    [Fact(Timeout = 10_000)]
    public async Task AWriteReachesTheDriver()
    {
        var ct = TestContext.Current.CancellationToken;
        var backend = new FakeBackend();
        await using var port = await OpenAsync(backend, ct);

        await port.WriteAsync(":00#"u8.ToArray(), ct);

        backend.Written.Select(w => Encoding.ASCII.GetString(w)).ShouldBe([":00#"]);
        port.HasAbandonedIo.ShouldBeFalse();
    }

    [Fact(Timeout = 10_000)]
    public async Task AWriteTheDriverNeverCompletesGivesThePortUp()
    {
        var ct = TestContext.Current.CancellationToken;
        var backend = new FakeBackend { BlockWrites = true };
        await using var port = await OpenAsync(backend, ct, Settings(writeMs: 300));

        await Should.ThrowAsync<SerialIoAbandonedException>(async () => await port.WriteAsync(":00#"u8.ToArray(), ct));
        port.HasAbandonedIo.ShouldBeTrue();

        // Refused at once rather than stranding another thread for another deadline.
        var clock = Stopwatch.StartNew();
        await Should.ThrowAsync<SerialIoAbandonedException>(async () => await port.WriteAsync(":02#"u8.ToArray(), ct));
        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromMilliseconds(250));
        backend.Release.Set();
    }

    [Fact(Timeout = 10_000)]
    public async Task AWriteTheDriverTimedOutIsATimeoutNotAnAbandonment()
    {
        var ct = TestContext.Current.CancellationToken;
        var backend = new FakeBackend { WriteFault = new TimeoutException("driver write timeout") };
        await using var port = await OpenAsync(backend, ct);

        var ex = await Should.ThrowAsync<SerialTimeoutException>(async () => await port.WriteAsync(":00#"u8.ToArray(), ct));
        ex.ShouldNotBeOfType<SerialIoAbandonedException>();
        port.HasAbandonedIo.ShouldBeFalse();
    }

    [Fact(Timeout = 10_000)]
    public async Task AReadFaultOnAPortNoLongerListedIsARemoval()
    {
        var ct = TestContext.Current.CancellationToken;
        var backend = new FakeBackend();
        var present = true;
        await using var port = await OpenAsync(backend, ct, exists: _ => present);
        backend.ReadFault = new IOException("A device attached to the system is not functioning.");
        present = false;

        var ex = await Should.ThrowAsync<SerialPortRemovedException>(async () => await ReadReplyAsync(port, ct));
        ex.InnerException.ShouldBeOfType<IOException>();
    }

    [Fact(Timeout = 10_000)]
    public async Task AReadFaultOnAPortStillListedIsAnOrdinaryFault()
    {
        var ct = TestContext.Current.CancellationToken;
        var backend = new FakeBackend();
        await using var port = await OpenAsync(backend, ct, exists: _ => true);
        backend.ReadFault = new IOException("The parameter is incorrect.");

        var ex = await Should.ThrowAsync<SerialException>(async () => await ReadReplyAsync(port, ct));
        ex.ShouldNotBeOfType<SerialPortRemovedException>();
    }

    [Fact(Timeout = 10_000)]
    public async Task DiscardHandsBackWhatWasPendingThenEmptiesBothSides()
    {
        var ct = TestContext.Current.CancellationToken;
        var backend = new FakeBackend();
        await using var port = await OpenAsync(backend, ct, Settings(readMs: 300));
        backend.Feed("P7820#junk");
        (await ReadReplyAsync(port, ct)).ShouldBe("P7820");
        backend.Feed("more");

        var drained = new byte[64];
        var n = port.DiscardInput(drained);

        Encoding.ASCII.GetString(drained, 0, n).ShouldBe("junkmore");
        backend.BytesToRead.ShouldBe(0);
        await Should.ThrowAsync<SerialTimeoutException>(async () => await ReadReplyAsync(port, ct));
    }

    [Fact(Timeout = 10_000)]
    public async Task ACloseThatNeverFinishesAbandonsTheHandleNotTheCaller()
    {
        var ct = TestContext.Current.CancellationToken;
        var backend = new FakeBackend { BlockClose = true };
        var port = await OpenAsync(backend, ct, Settings(closeMs: 300));

        var clock = Stopwatch.StartNew();
        (await port.CloseAsync()).ShouldBeFalse();
        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(3));
        (await port.CloseAsync()).ShouldBeFalse();
        backend.CloseCalls.ShouldBe(1);
        port.IsOpen.ShouldBeFalse();
        await Should.ThrowAsync<ObjectDisposedException>(async () => await ReadReplyAsync(port, ct));
        backend.Release.Set();
    }

    [Fact(Timeout = 10_000)]
    public async Task AClosedPortClosesOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var backend = new FakeBackend();
        var port = await OpenAsync(backend, ct);

        (await port.CloseAsync()).ShouldBeTrue();
        await port.DisposeAsync();
        backend.CloseCalls.ShouldBe(1);
    }

    [Fact(Timeout = 10_000)]
    public async Task APortHeldElsewhereIsBusy()
    {
        var ct = TestContext.Current.CancellationToken;
        var backend = new FakeBackend { OpenFault = new UnauthorizedAccessException("Access to the path 'COM99' is denied.") };

        await Should.ThrowAsync<SerialPortBusyException>(async () => await OpenAsync(backend, ct));
    }

    [Fact(Timeout = 10_000)]
    public async Task APortThatIsNotThereIsNotFound()
    {
        var ct = TestContext.Current.CancellationToken;
        var backend = new FakeBackend { OpenFault = new IOException("The port 'COM99' does not exist.") };

        await Should.ThrowAsync<SerialPortNotFoundException>(async () => await OpenAsync(backend, ct, exists: _ => false));
    }

    [Fact(Timeout = 10_000)]
    public async Task AnOpenThatHangsTimesOutAndItsLateHandleIsClosed()
    {
        var ct = TestContext.Current.CancellationToken;
        var backend = new FakeBackend { BlockOpen = true };
        var settings = Settings() with { OpenTimeout = TimeSpan.FromMilliseconds(300) };

        await Should.ThrowAsync<SerialTimeoutException>(async () => await OpenAsync(backend, ct, settings));

        backend.Release.Set();
        var deadline = Stopwatch.StartNew();
        while (backend.CloseCalls == 0 && deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(20, ct);
        }
        backend.CloseCalls.ShouldBe(1);
    }

    [Fact(Timeout = 10_000)]
    public async Task AReadWithNoDeadlineWaitsForItsReply()
    {
        var ct = TestContext.Current.CancellationToken;
        var backend = new FakeBackend();
        await using var port = await OpenAsync(backend, ct, Settings(readMs: 100));
        var late = Task.Run(async () =>
        {
            // Well past the port's own 100 ms deadline, which this read overrides.
            await Task.Delay(700, ct);
            backend.Feed("EOK#");
        }, ct);

        var buffer = new byte[16];
        var n = await port.ReadTerminatedAsync(buffer, Hash, Timeout.InfiniteTimeSpan, ct);

        Encoding.ASCII.GetString(buffer, 0, n).ShouldBe("EOK");
        await late;
    }

    [Fact(Timeout = 10_000)]
    public async Task AReadWithNoDeadlineStillEndsOnItsToken()
    {
        var ct = TestContext.Current.CancellationToken;
        var backend = new FakeBackend();
        await using var port = await OpenAsync(backend, ct, Settings() with { ReadTimeout = Timeout.InfiniteTimeSpan });
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(300);

        await Should.ThrowAsync<OperationCanceledException>(async () => await ReadReplyAsync(port, cts.Token));
    }

    [Fact]
    public void SettingsRefuseANonPositiveTimeout()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => (new SerialSettings(9600) { ReadTimeout = TimeSpan.Zero }).Validate());
        Should.Throw<ArgumentOutOfRangeException>(() => new SerialSettings(0).Validate());
        Should.Throw<ArgumentOutOfRangeException>(() => (new SerialSettings(9600) { DataBits = 9 }).Validate());
    }
}
