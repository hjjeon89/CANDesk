using System.Threading.Channels;
using CANDesk.Hal;

namespace CANDesk.Hal.Mock;

public enum MockCanMode { Loopback, Stress, TracePlayback, FaultInjection }
public sealed class MockCanDevice(string channelName, MockCanMode mode = MockCanMode.Loopback) : ICanDevice
{
    private readonly Channel<CanFrame> _frames = Channel.CreateBounded<CanFrame>(new BoundedChannelOptions(32_768) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _stressTask;
    public string ChannelName { get; } = channelName;
    public MockCanMode Mode { get; set; } = mode;
    public CanDeviceStatus Status { get; private set; } = CanDeviceStatus.Closed;
    public event EventHandler<CanErrorEventArgs>? ErrorOccurred;
    public event EventHandler<CanDeviceStatusChangedEventArgs>? StatusChanged;
    public Task OpenAsync(CanBusConfig config, CancellationToken cancellationToken = default)
    {
        if (Status == CanDeviceStatus.Open) return Task.CompletedTask;
        SetStatus(CanDeviceStatus.Open);
        if (Mode == MockCanMode.Stress) _stressTask = Task.Run(GenerateStressAsync);
        return Task.CompletedTask;
    }
    public Task CloseAsync(CancellationToken cancellationToken = default) { SetStatus(CanDeviceStatus.Closed); return Task.CompletedTask; }
    public ValueTask SendAsync(CanFrame frame, CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        if (Mode == MockCanMode.FaultInjection) { ErrorOccurred?.Invoke(this, new(CanErrorKind.BusOff, "Mock bus-off injected.")); SetStatus(CanDeviceStatus.BusOff); return ValueTask.CompletedTask; }
        if (Mode == MockCanMode.Loopback) _frames.Writer.TryWrite(frame);
        return ValueTask.CompletedTask;
    }
    public async IAsyncEnumerable<CanFrame> ReadFramesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    { await foreach (var frame in _frames.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false)) yield return frame; }
    public Task ResetBusAsync(CancellationToken cancellationToken = default) { SetStatus(CanDeviceStatus.Open); return Task.CompletedTask; }
    private async Task GenerateStressAsync()
    {
        var counter = 0u;
        try { while (!_lifetime.IsCancellationRequested && Status == CanDeviceStatus.Open) { _frames.Writer.TryWrite(CanFrame.Create(0x700 + counter % 0x100, BitConverter.GetBytes(counter++))); await Task.Delay(1, _lifetime.Token).ConfigureAwait(false); } }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }
    private void EnsureOpen() { if (Status != CanDeviceStatus.Open) throw new InvalidOperationException("The CAN device is not open."); }
    private void SetStatus(CanDeviceStatus status) { Status = status; StatusChanged?.Invoke(this, new(status)); }
    public async ValueTask DisposeAsync() { await _lifetime.CancelAsync().ConfigureAwait(false); if (_stressTask is not null) await _stressTask.ConfigureAwait(false); _frames.Writer.TryComplete(); _lifetime.Dispose(); }
}

public sealed class MockCanDeviceFactory : ICanDeviceFactory
{
    public string Vendor => "Mock";
    public Task<IReadOnlyList<CanDeviceDescriptor>> EnumerateAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CanDeviceDescriptor>>([new(Vendor, "mock-0", "Mock CAN device", ["Mock Channel 0"], true)]);
    public Task<ICanDevice> CreateAsync(string deviceId, string channelName, CancellationToken cancellationToken = default) => Task.FromResult<ICanDevice>(new MockCanDevice(channelName));
}
