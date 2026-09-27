using System.Text;
using Shouldly;
using Xunit;

namespace SharpAstro.Serial.Tests;

public sealed class SerialLoopbackTests
{
    [Fact(Timeout = 10_000)]
    public async Task WhatOneEndWritesTheOtherReads()
    {
        var ct = TestContext.Current.CancellationToken;
        var (first, second) = SerialLoopback.CreatePair(new SerialSettings(9600));
        await using var a = first;
        await using var b = second;

        await a.WriteAsync(":02#"u8.ToArray(), ct);
        var request = new byte[8];
        var n = await b.ReadTerminatedAsync(request, "#"u8.ToArray(), ct);
        Encoding.ASCII.GetString(request, 0, n).ShouldBe(":02");

        await b.WriteAsync("EOK#"u8.ToArray(), ct);
        var reply = new byte[8];
        n = await a.ReadTerminatedAsync(reply, "#"u8.ToArray(), ct);
        Encoding.ASCII.GetString(reply, 0, n).ShouldBe("EOK");
    }

    [Fact(Timeout = 10_000)]
    public async Task AnEndWithNothingSentTimesOutLikeAPort()
    {
        var ct = TestContext.Current.CancellationToken;
        var (first, second) = SerialLoopback.CreatePair(new SerialSettings(9600) { ReadTimeout = TimeSpan.FromMilliseconds(200) });
        await using var a = first;
        await using var b = second;

        await Should.ThrowAsync<SerialTimeoutException>(async () => await a.ReadTerminatedAsync(new byte[8], "#"u8.ToArray(), ct));
        a.PortName.ShouldBe("loopback-1");
        b.PortName.ShouldBe("loopback-2");
    }
}
