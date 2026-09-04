using CANDesk.Hal;
using CANDesk.Hal.Mock;
using Xunit;

namespace CANDesk.Core.Tests;

public sealed class MockCanDeviceTests
{
    [Fact]
    public async Task TracePlayback_EmitsConfiguredFramesInSequence()
    {
        await using var device = new MockCanDevice("Mock Channel 0", MockCanMode.TracePlayback);
        device.SetTracePlayback(
        [
            new MockTraceFrame(CanFrame.Create(0x120, [0x01]), TimeSpan.Zero),
            new MockTraceFrame(CanFrame.Create(0x121, [0x02]), TimeSpan.Zero)
        ]);

        await device.OpenAsync(new CanBusConfig(500_000));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await using var enumerator = device.ReadFramesAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal((uint)0x120, enumerator.Current.Id);
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal((uint)0x121, enumerator.Current.Id);
    }
}
