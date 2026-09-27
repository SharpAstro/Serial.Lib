using System.Text;
using Shouldly;
using Xunit;

namespace SharpAstro.Serial.Tests;

/// <summary>
/// The port a bench run drives, opened once for the class: every open resets a CH340 board, and the
/// myFocuserPro2 firmware takes about two seconds to come back.
/// </summary>
public sealed class BenchPortFixture : IAsyncLifetime
{
    /// <summary>
    /// Names the port of a myFocuserPro2-protocol focuser (a Gemini Focuser Pro, a myFP2): <c>COM3</c>. Unset, every
    /// bench test skips. The tests only READ (status, position, temperature); nothing moves.
    /// </summary>
    public const string PortVariable = "SERIAL_LIB_BENCH_PORT";

    public string? PortName { get; } = Environment.GetEnvironmentVariable(PortVariable) is { Length: > 0 } p ? p : null;

    public ISerialPort? Port { get; private set; }

    public async ValueTask InitializeAsync()
    {
        if (PortName is null)
        {
            return;
        }
        Port = await SerialPorts.OpenAsync(PortName, new SerialSettings(9600) { AssertDtr = true, AssertRts = true });
        // Out of the reset the open caused, and any boot chatter discarded.
        await Task.Delay(TimeSpan.FromSeconds(3));
        Port.DiscardInput(new byte[256]);
    }

    public async ValueTask DisposeAsync()
    {
        if (Port is not null)
        {
            (await Port.CloseAsync()).ShouldBeTrue();
        }
    }
}

public sealed class BenchTests(BenchPortFixture fixture) : IClassFixture<BenchPortFixture>
{
    private static readonly byte[] Hash = "#"u8.ToArray();

    private ISerialPort Port
    {
        get
        {
            Assert.SkipWhen(fixture.Port is null, $"set {BenchPortFixture.PortVariable} to a myFocuserPro2-protocol focuser's port to run the bench tests");
            return fixture.Port!;
        }
    }

    private async Task<string> ExchangeAsync(string command, CancellationToken cancellationToken)
    {
        var port = Port;
        await port.WriteAsync(Encoding.ASCII.GetBytes(command), cancellationToken);
        var reply = new byte[64];
        var n = await port.ReadTerminatedAsync(reply, Hash, cancellationToken);
        return Encoding.ASCII.GetString(reply, 0, n);
    }

    [Fact(Timeout = 20_000)]
    public async Task TheFocuserAnswersItsStatus() => (await ExchangeAsync(":02#", TestContext.Current.CancellationToken)).ShouldBe("EOK");

    [Fact(Timeout = 120_000)]
    public async Task ManyExchangesStayFrameAligned()
    {
        var ct = TestContext.Current.CancellationToken;
        // A reply landing one frame late (the CH34x async-read abort) shows as a position answered with a
        // temperature, or the other way about.
        for (var i = 0; i < 200; i++)
        {
            (await ExchangeAsync(":00#", ct)).ShouldMatch(@"^P\d+$");
            (await ExchangeAsync(":06#", ct)).ShouldMatch(@"^Z-?\d+(\.\d+)?$");
        }
    }

    [Fact(Timeout = 20_000)]
    public async Task ACancelledReadLeavesTheNextExchangeClean()
    {
        var port = Port;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(300);
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await port.ReadTerminatedAsync(new byte[64], Hash, TimeSpan.FromSeconds(10), cts.Token));

        (await ExchangeAsync(":02#", TestContext.Current.CancellationToken)).ShouldBe("EOK");
    }

    [Fact(Timeout = 20_000)]
    public async Task ANoReplyReadTimesOut()
    {
        var port = Port;

        await Should.ThrowAsync<SerialTimeoutException>(async () =>
            await port.ReadTerminatedAsync(new byte[64], Hash, TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken));
        (await ExchangeAsync(":02#", TestContext.Current.CancellationToken)).ShouldBe("EOK");
    }

    [Fact]
    public void EnumerationDescribesTheBenchPort()
    {
        var name = Port.PortName;
        var info = SerialPorts.Enumerate().Where(p => p.PortName.Equals(name, StringComparison.OrdinalIgnoreCase)).ShouldHaveSingleItem();

        info.VendorId.ShouldNotBeNull();
        info.ProductId.ShouldNotBeNull();
        info.Identity.ShouldNotBe(SerialIdentityKind.PortName);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{info.PortName}: {info.Description}; {info.VendorId:x4}:{info.ProductId:x4} serial '{info.SerialNumber}'; {info.DeviceInstanceId}; location {info.LocationPath}; identity {info.IdentityKey}");
    }

}
