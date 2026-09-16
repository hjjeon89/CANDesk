using System.Runtime.CompilerServices;
using CANDesk.Core.Dispatch;
using CANDesk.Hal;
using Xunit;

namespace CANDesk.Core.Tests;

public sealed class RxDispatcherTests
{
    [Fact]
    public async Task DroppedFrameCount_IncrementsWhenBoundedQueueDropsOldest()
    {
        await using var dispatcher = new RxDispatcher(TimeSpan.FromSeconds(10), capacity: 1);

        await dispatcher.StartAsync(GenerateFramesAsync(100));

        for (var attempt = 0; attempt < 50 && dispatcher.DroppedFrameCount == 0; attempt++)
        {
            await Task.Delay(10);
        }

        Assert.True(dispatcher.DroppedFrameCount > 0);
    }

    private static async IAsyncEnumerable<CanFrame> GenerateFramesAsync(
        int count,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return CanFrame.Create((uint)i, [0x01]);
        }
    }
}
