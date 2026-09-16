using CANDesk.Core.Dispatch;
using CANDesk.Hal;
using CANDesk.Hal.Mock;
using Xunit;

namespace CANDesk.IntegrationTests;

public sealed class MockStressPipelineTests
{
    [Fact]
    public async Task MockStressMode_FlowsThroughRxDispatcherAtHighRate()
    {
        await using var device = new MockCanDevice("Stress", MockCanMode.Stress)
        {
            StressFramesPerSecond = 20_000
        };
        await using var dispatcher = new RxDispatcher(capacity: 32_768);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var received = 0;
        var enoughFrames = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.FramesBatched += (_, frames) =>
        {
            if (Interlocked.Add(ref received, frames.Count) >= 5_000)
            {
                enoughFrames.TrySetResult();
            }
        };

        await device.OpenAsync(new CanBusConfig(500_000), timeout.Token);
        await dispatcher.StartAsync(device.ReadFramesAsync(timeout.Token), timeout.Token);

        await enoughFrames.Task.WaitAsync(timeout.Token);

        Assert.True(Volatile.Read(ref received) >= 5_000);
        Assert.Equal(CanDeviceStatus.Open, device.Status);
    }

    [Fact]
    public async Task MockTracePlayback_ReplaysImportedTraceTimingThroughDispatcher()
    {
        var start = DateTime.UtcNow;
        var trace = MockTracePlayback.FromTimestampedFrames(
        [
            (CanFrame.Create(0x120, [0x01]), start),
            (CanFrame.Create(0x121, [0x02]), start.AddMilliseconds(20)),
            (CanFrame.Create(0x122, [0x03]), start.AddMilliseconds(40)),
        ]);
        await using var device = new MockCanDevice("Trace", MockCanMode.TracePlayback);
        device.SetTracePlayback(trace);
        await using var dispatcher = new RxDispatcher(TimeSpan.FromMilliseconds(10));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var ids = new List<uint>();
        var sawAll = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.FramesBatched += (_, frames) =>
        {
            lock (ids)
            {
                ids.AddRange(frames.Select(frame => frame.Id));
                if (ids.Count >= 3)
                {
                    sawAll.TrySetResult();
                }
            }
        };

        await device.OpenAsync(new CanBusConfig(500_000), timeout.Token);
        await dispatcher.StartAsync(device.ReadFramesAsync(timeout.Token), timeout.Token);

        await sawAll.Task.WaitAsync(timeout.Token);

        lock (ids)
        {
            Assert.Equal([(uint)0x120, 0x121, 0x122], ids);
        }
    }
}
