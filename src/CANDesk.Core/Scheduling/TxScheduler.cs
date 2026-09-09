using System.Collections.Concurrent;
using CANDesk.Hal;

namespace CANDesk.Core.Scheduling;

public delegate void PreSendModifier(Span<byte> payload, ulong sendCount);

public interface ITriggerCondition
{
    bool IsSatisfied(in CanFrame receivedFrame);
}

public interface ITxScheduler : IAsyncDisposable
{
    event EventHandler<TxSchedulerErrorEventArgs>? ErrorOccurred;

    /// <summary>Raised after a frame is successfully handed to the device — for single sends,
    /// cyclic jobs, and triggered jobs alike — so callers can observe actual bus TX traffic
    /// (trace/monitor mirroring, TX rate counting) from one place regardless of send path.</summary>
    event EventHandler<CanFrame>? FrameSent;
    Guid ScheduleCyclic(CanFrame templateFrame, TimeSpan period, PreSendModifier? preSendModifier = null);
    Guid ScheduleTriggered(CanFrame templateFrame, ITriggerCondition trigger, PreSendModifier? preSendModifier = null);
    ValueTask SendOnceAsync(CanFrame frame, CancellationToken cancellationToken = default);
    void NotifyReceived(in CanFrame frame);
    void Cancel(Guid jobId);
    void UpdatePayload(Guid jobId, ReadOnlySpan<byte> newData);
}

public sealed class TxSchedulerErrorEventArgs(Guid jobId, CanFrame frame, Exception exception) : EventArgs
{
    public Guid JobId { get; } = jobId;
    public CanFrame Frame { get; } = frame;
    public Exception Exception { get; } = exception;
}

public sealed class TxScheduler(ICanDevice device) : ITxScheduler
{
    private sealed class Job(CanFrame frame, PreSendModifier? modifier, TimeSpan? period = null, ITriggerCondition? trigger = null)
    {
        public readonly object Gate = new();
        public CanFrame Frame = frame;
        public ulong SendCount;
        public readonly PreSendModifier? Modifier = modifier;
        public readonly TimeSpan? Period = period;
        public readonly ITriggerCondition? Trigger = trigger;
        public CancellationTokenSource? Cancellation;
    }
    private readonly ConcurrentDictionary<Guid, Job> _jobs = new();
    private readonly CancellationTokenSource _shutdown = new();
    private int _disposeState;

    public event EventHandler<TxSchedulerErrorEventArgs>? ErrorOccurred;
    public event EventHandler<CanFrame>? FrameSent;

    public Guid ScheduleCyclic(CanFrame templateFrame, TimeSpan period, PreSendModifier? preSendModifier = null)
    {
        if (period <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(period));
        }

        var id = Guid.NewGuid();
        var job = new Job(templateFrame, preSendModifier, period);
        _jobs[id] = job;
        job.Cancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        _ = RunCyclicAsync(id, job, job.Cancellation.Token);
        return id;
    }

    public Guid ScheduleTriggered(CanFrame templateFrame, ITriggerCondition trigger, PreSendModifier? preSendModifier = null)
    {
        ArgumentNullException.ThrowIfNull(trigger);

        var id = Guid.NewGuid();
        _jobs[id] = new Job(templateFrame, preSendModifier, trigger: trigger);
        return id;
    }

    public async ValueTask SendOnceAsync(CanFrame frame, CancellationToken cancellationToken = default)
    {
        await device.SendAsync(frame, cancellationToken).ConfigureAwait(false);
        FrameSent?.Invoke(this, frame);
    }

    public void NotifyReceived(in CanFrame frame)
    {
        var received = frame;
        foreach (var pair in _jobs.Where(pair => pair.Value.Trigger?.IsSatisfied(received) == true))
        {
            _ = SendJobSafelyAsync(pair.Key, pair.Value, _shutdown.Token);
        }
    }

    public void Cancel(Guid jobId)
    {
        if (_jobs.TryRemove(jobId, out var job))
        {
            job.Cancellation?.Cancel();
        }
    }

    public void UpdatePayload(Guid jobId, ReadOnlySpan<byte> newData)
    {
        if (!_jobs.TryGetValue(jobId, out var job))
        {
            throw new KeyNotFoundException($"TX job '{jobId}' was not found.");
        }

        lock (job.Gate)
        {
            job.Frame = job.Frame.WithPayload(newData);
        }
    }

    private async Task RunCyclicAsync(Guid id, Job job, CancellationToken ct)
    {
        using var highResolutionScope = HighResolutionTimerScope.Acquire();
        using var timer = new PeriodicTimer(job.Period!.Value);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                await SendJobSafelyAsync(id, job, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            _jobs.TryRemove(id, out _);
        }
    }

    private async Task SendJobAsync(Job job, CancellationToken ct)
    {
        CanFrame frame;
        ulong count;
        lock (job.Gate)
        {
            frame = job.Frame;
            count = job.SendCount++;
            job.Modifier?.Invoke(frame.PayloadSpanWritable, count);
        }

        // job.Frame is the same stored template mutated in place on every cyclic/triggered tick, so
        // its SystemTime stays frozen at whenever the job was scheduled (or last had UpdatePayload
        // called) unless re-stamped here. Without this, Monitor's CycleTimeMs — computed from
        // consecutive frames' SystemTime — saw the same stale value every tick, showing 0 or even
        // negative gaps once interleaved with a frame that had a real, later timestamp.
        frame = frame.WithSystemTime(DateTime.UtcNow);
        await device.SendAsync(frame, ct).ConfigureAwait(false);
        FrameSent?.Invoke(this, frame);
    }

    private async Task SendJobSafelyAsync(Guid jobId, Job job, CancellationToken ct)
    {
        try { await SendJobAsync(job, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception exception)
        {
            CanFrame frame;
            lock (job.Gate) frame = job.Frame;
            ErrorOccurred?.Invoke(this, new(jobId, frame, exception));
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        _shutdown.Cancel();
        foreach (var id in _jobs.Keys)
        {
            Cancel(id);
        }

        _shutdown.Dispose();

        return ValueTask.CompletedTask;
    }
}
