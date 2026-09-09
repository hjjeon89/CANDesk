using CANDesk.Core.Scheduling;
using CANDesk.Hal;
using Xunit;

namespace CANDesk.Core.Tests;

public sealed class TxSchedulerTests
{
    [Fact]
    public async Task TriggeredJob_PublishesSendFailureInsteadOfSilentlyDroppingIt()
    {
        var device = new ThrowingCanDevice();
        await using var scheduler = new TxScheduler(device);
        var failure = new TaskCompletionSource<TxSchedulerErrorEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        scheduler.ErrorOccurred += (_, args) => failure.TrySetResult(args);
        var jobId = scheduler.ScheduleTriggered(CanFrame.Create(0x301, [0x00]), new AlwaysTrigger());

        scheduler.NotifyReceived(CanFrame.Create(0x100, [0x01]));

        var error = await failure.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(jobId, error.JobId);
        Assert.IsType<InvalidOperationException>(error.Exception);
    }

    [Fact]
    public async Task ScheduleCyclic_RaisesFrameSent_OnEveryTick()
    {
        var device = new RecordingCanDevice();
        await using var scheduler = new TxScheduler(device);
        var sentCount = 0;
        var sawThreeSends = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scheduler.FrameSent += (_, _) =>
        {
            if (Interlocked.Increment(ref sentCount) >= 3)
            {
                sawThreeSends.TrySetResult();
            }
        };

        var jobId = scheduler.ScheduleCyclic(CanFrame.Create(0x300, [0x00]), TimeSpan.FromMilliseconds(5));

        await sawThreeSends.Task.WaitAsync(TimeSpan.FromSeconds(2));
        scheduler.Cancel(jobId);
    }

    [Fact]
    public async Task ScheduleCyclic_StampsEachSentFrameWithTheActualSendTime()
    {
        // Regression test: SendJobAsync used to re-send the same stored template CanFrame on every
        // tick without touching its SystemTime, so consecutive FrameSent events all reported the
        // stale timestamp from whenever the job was scheduled. Monitor's CycleTimeMs, computed from
        // consecutive frames' SystemTime, then read as 0 (or negative once interleaved with a frame
        // that had a real, later timestamp) instead of the actual ~cyclic-period gap.
        var device = new RecordingCanDevice();
        await using var scheduler = new TxScheduler(device);
        var sentTimes = new List<DateTime>();
        var sawThreeSends = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scheduler.FrameSent += (_, frame) =>
        {
            lock (sentTimes)
            {
                sentTimes.Add(frame.SystemTime);
                if (sentTimes.Count >= 3)
                {
                    sawThreeSends.TrySetResult();
                }
            }
        };

        var jobId = scheduler.ScheduleCyclic(CanFrame.Create(0x300, [0x00]), TimeSpan.FromMilliseconds(20));

        await sawThreeSends.Task.WaitAsync(TimeSpan.FromSeconds(2));
        scheduler.Cancel(jobId);

        lock (sentTimes)
        {
            Assert.True(sentTimes[1] > sentTimes[0], "Second send's SystemTime should be later than the first's.");
            Assert.True(sentTimes[2] > sentTimes[1], "Third send's SystemTime should be later than the second's.");
        }
    }

    [Fact]
    public async Task UpdatePayload_ChangesSubsequentCyclicSends()
    {
        // Regression coverage for the gap ITxScheduler.UpdatePayload exists to close: editing a
        // running cyclic job's Raw Hex or a signal's physical value in the UI must actually reach
        // the wire, not just the grid. This tests the scheduler side of that path (App-layer wiring
        // from TxJobRow.PropertyChanged to this call isn't covered here, per the App layer's
        // existing "no automated tests" convention).
        var device = new RecordingCanDevice();
        await using var scheduler = new TxScheduler(device);
        var sentPayloads = new List<byte[]>();
        var sawUpdatedPayload = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scheduler.FrameSent += (_, frame) =>
        {
            lock (sentPayloads)
            {
                var payload = frame.PayloadSpan.ToArray();
                sentPayloads.Add(payload);
                if (payload is [0xAA, 0xBB])
                {
                    sawUpdatedPayload.TrySetResult();
                }
            }
        };

        var jobId = scheduler.ScheduleCyclic(CanFrame.Create(0x300, [0x00, 0x00]), TimeSpan.FromMilliseconds(5));
        await Task.Delay(20); // let a couple of ticks go out with the original payload first
        scheduler.UpdatePayload(jobId, [0xAA, 0xBB]);

        await sawUpdatedPayload.Task.WaitAsync(TimeSpan.FromSeconds(2));
        scheduler.Cancel(jobId);

        lock (sentPayloads)
        {
            Assert.Contains(sentPayloads, p => p is [0x00, 0x00]);
        }
    }

    [Fact]
    public async Task SendOnceAsync_RaisesFrameSent_ForRateAndTraceObservers()
    {
        var device = new RecordingCanDevice();
        await using var scheduler = new TxScheduler(device);
        var sent = new TaskCompletionSource<CanFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        scheduler.FrameSent += (_, frame) => sent.TrySetResult(frame);
        var frame = CanFrame.Create(0x200, [0x01, 0x02]);

        await scheduler.SendOnceAsync(frame);

        var observed = await sent.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(frame.Id, observed.Id);
    }

    private sealed class AlwaysTrigger : ITriggerCondition
    {
        public bool IsSatisfied(in CanFrame receivedFrame) => true;
    }

    private sealed class ThrowingCanDevice : ICanDevice
    {
        public string ChannelName => "Test";
        public CanDeviceStatus Status => CanDeviceStatus.Open;
        public event EventHandler<CanErrorEventArgs>? ErrorOccurred
        {
            add { }
            remove { }
        }

        public event EventHandler<CanDeviceStatusChangedEventArgs>? StatusChanged
        {
            add { }
            remove { }
        }
        public Task OpenAsync(CanBusConfig config, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CloseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask SendAsync(CanFrame frame, CancellationToken cancellationToken = default) => ValueTask.FromException(new InvalidOperationException("Injected send failure."));
        public async IAsyncEnumerable<CanFrame> ReadFramesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
        public Task ResetBusAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingCanDevice : ICanDevice
    {
        public string ChannelName => "Test";
        public CanDeviceStatus Status => CanDeviceStatus.Open;
        public event EventHandler<CanErrorEventArgs>? ErrorOccurred
        {
            add { }
            remove { }
        }

        public event EventHandler<CanDeviceStatusChangedEventArgs>? StatusChanged
        {
            add { }
            remove { }
        }
        public Task OpenAsync(CanBusConfig config, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CloseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask SendAsync(CanFrame frame, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public async IAsyncEnumerable<CanFrame> ReadFramesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
        public Task ResetBusAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
