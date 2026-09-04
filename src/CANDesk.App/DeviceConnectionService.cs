using CANDesk.Hal;

namespace CANDesk.App;

public interface IDeviceConnectionService
{
    ICanDevice? CurrentDevice { get; }
    Task ConnectAsync(ICanDeviceFactory factory, string deviceId, string channelName, CanBusConfig configuration, CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
}

public sealed class DeviceConnectionService(IEnumerable<ICanDeviceFactory> factories) : IDeviceConnectionService, IAsyncDisposable
{
    public ICanDevice? CurrentDevice { get; private set; }
    public async Task ConnectMockAsync(CanBusConfig? configuration = null, CancellationToken cancellationToken = default)
    {
        var factory = factories.Single(factory => factory.Vendor == "Mock");
        await ConnectAsync(factory, "mock-0", "Mock Channel 0", configuration ?? new(500_000), cancellationToken);
    }
    public async Task ConnectAsync(ICanDeviceFactory factory, string deviceId, string channelName, CanBusConfig configuration, CancellationToken cancellationToken = default)
    {
        await DisconnectAsync(cancellationToken);
        var device = await factory.CreateAsync(deviceId, channelName, cancellationToken);
        try { await device.OpenAsync(configuration, cancellationToken); CurrentDevice = device; }
        catch { await device.DisposeAsync(); throw; }
    }
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (CurrentDevice is null) return;
        await CurrentDevice.CloseAsync(cancellationToken); await CurrentDevice.DisposeAsync(); CurrentDevice = null;
    }
    public ValueTask DisposeAsync() => new(DisconnectAsync());
}
