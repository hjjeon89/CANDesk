using System.Threading.Channels;
using CANDesk.Hal;

namespace CANDesk.Hal.Mock;

public enum MockCanMode
{
    Loopback,
    Stress,
    TracePlayback,
    FaultInjection
}

/// <summary>One frame in a mock trace; delay is measured from the previous frame.</summary>
public sealed record MockTraceFrame(CanFrame Frame, TimeSpan Delay);

public sealed class MockCanDevice(string channelName, MockCanMode mode = MockCanMode.Loopback) : ICanDevice
{
    private readonly Channel<CanFrame> _frames = Channel.CreateBounded<CanFrame>(new BoundedChannelOptions(32_768) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _traceGate = new();
    private IReadOnlyList<MockTraceFrame> _tracePlayback = [];
    private CancellationTokenSource? _sessionCancellation;
    private Task? _modeTask;
    private int _disposeState;

    public string ChannelName { get; } = channelName;
    public MockCanMode Mode { get; set; } = mode;
    public CanDeviceStatus Status { get; private set; } = CanDeviceStatus.Closed;

    public event EventHandler<CanErrorEventArgs>? ErrorOccurred;
    public event EventHandler<CanDeviceStatusChangedEventArgs>? StatusChanged;

    /// <summary>Replaces the trace that is emitted when the device is opened in TracePlayback mode.</summary>
    public void SetTracePlayback(IEnumerable<MockTraceFrame> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);

        var trace = frames.ToArray();
        if (trace.Any(entry => entry.Delay < TimeSpan.Zero))
        {
            throw new ArgumentOutOfRangeException(nameof(frames), "Trace frame delays cannot be negative.");
        }

        lock (_traceGate)
        {
            _tracePlayback = trace;
        }
    }

    public Task OpenAsync(CanBusConfig config, CancellationToken cancellationToken = default)
    {
        if (Status == CanDeviceStatus.Open)
        {
            return Task.CompletedTask;
        }

        config.Validate();
        var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        _sessionCancellation = sessionCancellation;
        SetStatus(CanDeviceStatus.Open);

        _modeTask = Mode switch
        {
            MockCanMode.Stress => Task.Run(() => GenerateStressAsync(sessionCancellation.Token)),
            MockCanMode.TracePlayback => Task.Run(() => PlayTraceAsync(sessionCancellation.Token)),
            _ => null
        };

        return Task.CompletedTask;
    }

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        if (Status == CanDeviceStatus.Closed)
        {
            return;
        }

        SetStatus(CanDeviceStatus.Closed);
        _sessionCancellation?.Cancel();
        if (_modeTask is not null)
        {
            await _modeTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        _modeTask = null;
        _sessionCancellation?.Dispose();
        _sessionCancellation = null;
    }

    public ValueTask SendAsync(CanFrame frame, CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        if (Mode == MockCanMode.FaultInjection)
        {
            ErrorOccurred?.Invoke(this, new(CanErrorKind.BusOff, "Mock bus-off injected."));
            SetStatus(CanDeviceStatus.BusOff);
            return ValueTask.CompletedTask;
        }

        if (Mode == MockCanMode.Loopback)
        {
            _frames.Writer.TryWrite(frame);
        }

        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<CanFrame> ReadFramesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var frame in _frames.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return frame;
        }
    }

    public Task ResetBusAsync(CancellationToken cancellationToken = default)
    {
        SetStatus(CanDeviceStatus.Open);
        return Task.CompletedTask;
    }

    private async Task GenerateStressAsync(CancellationToken cancellationToken)
    {
        var counter = 0u;
        try
        {
            while (!cancellationToken.IsCancellationRequested && Status == CanDeviceStatus.Open)
            {
                _frames.Writer.TryWrite(CanFrame.Create(0x700 + counter % 0x100, BitConverter.GetBytes(counter++)));
                await Task.Delay(1, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task PlayTraceAsync(CancellationToken cancellationToken)
    {
        MockTraceFrame[] trace;
        lock (_traceGate)
        {
            trace = [.. _tracePlayback];
        }

        try
        {
            foreach (var entry in trace)
            {
                if (entry.Delay > TimeSpan.Zero)
                {
                    await Task.Delay(entry.Delay, cancellationToken).ConfigureAwait(false);
                }

                if (Status != CanDeviceStatus.Open || cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                await _frames.Writer.WriteAsync(entry.Frame, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void EnsureOpen()
    {
        if (Status != CanDeviceStatus.Open)
        {
            throw new InvalidOperationException("The CAN device is not open.");
        }
    }

    private void SetStatus(CanDeviceStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, new(status));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        await CloseAsync().ConfigureAwait(false);
        await _lifetime.CancelAsync().ConfigureAwait(false);
        _frames.Writer.TryComplete();
        _lifetime.Dispose();
    }
}

public sealed class MockCanDeviceFactory : ICanDeviceFactory
{
    public string Vendor => "Mock";
    public Task<IReadOnlyList<CanDeviceDescriptor>> EnumerateAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<CanDeviceDescriptor>>([new(Vendor, "mock-0", "Mock CAN device", ["Mock Channel 0"], true)]);

    public Task<ICanDevice> CreateAsync(string deviceId, string channelName, CancellationToken cancellationToken = default) =>
        Task.FromResult<ICanDevice>(new MockCanDevice(channelName));
}
