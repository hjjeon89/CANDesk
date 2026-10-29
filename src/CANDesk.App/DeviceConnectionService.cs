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
    public CanBusConfig? CurrentConfiguration { get; private set; }

    private ICanDeviceFactory GetFactory(string vendor) => factories.FirstOrDefault(factory => factory.Vendor == vendor)
        ?? throw new InvalidOperationException($"No CAN driver is registered for vendor '{vendor}'.");

    public async Task<IReadOnlyList<CanDeviceDescriptor>> EnumerateByVendorAsync(string vendor, CancellationToken cancellationToken = default)
    {
        var factory = GetFactory(vendor);
        var descriptors = await factory.EnumerateAsync(cancellationToken).ConfigureAwait(false);
        if (descriptors.Count == 0)
        {
            var diagnostics = (factory as ICanDeviceFactoryDiagnostics)?.LastEnumerationDiagnostics;
            var detail = string.IsNullOrEmpty(diagnostics) ? string.Empty : $" ({diagnostics})";
            throw new InvalidOperationException(
                $"No {vendor} device was found. Make sure the vendor SDK/driver is installed and at least one channel is available.{detail}");
        }
        return descriptors;
    }

    public async Task ConnectByVendorAsync(string vendor, string deviceId, string channelName, CanBusConfig configuration, CancellationToken cancellationToken = default)
    {
        var descriptors = await EnumerateByVendorAsync(vendor, cancellationToken).ConfigureAwait(false);
        var descriptor = descriptors.FirstOrDefault(device => device.DeviceId == deviceId && device.Channels.Contains(channelName))
            ?? throw new InvalidOperationException($"The selected {vendor} device '{deviceId}' / {channelName} is no longer available. Refresh and select a device.");

        await ConnectAsync(GetFactory(vendor), descriptor.DeviceId, $"{descriptor.DisplayName} / {channelName}", configuration, cancellationToken).ConfigureAwait(false);
    }

    public async Task ConnectAsync(ICanDeviceFactory factory, string deviceId, string channelName, CanBusConfig configuration, CancellationToken cancellationToken = default)
    {
        configuration.Validate();
        await DisconnectAsync(cancellationToken).ConfigureAwait(false);
        var device = await factory.CreateAsync(deviceId, channelName, cancellationToken).ConfigureAwait(false);
        try { await device.OpenAsync(configuration, cancellationToken).ConfigureAwait(false); CurrentDevice = device; CurrentConfiguration = configuration; }
        catch { await device.DisposeAsync().ConfigureAwait(false); throw; }
    }

    public async Task ConnectDeviceAsync(ICanDevice device, CanBusConfig configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);

        configuration.Validate();
        await DisconnectAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await device.OpenAsync(configuration, cancellationToken).ConfigureAwait(false);
            CurrentDevice = device;
            CurrentConfiguration = configuration;
        }
        catch
        {
            await device.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (CurrentDevice is null) return;
        await CurrentDevice.CloseAsync(cancellationToken).ConfigureAwait(false);
        await CurrentDevice.DisposeAsync().ConfigureAwait(false);
        CurrentDevice = null;
        CurrentConfiguration = null;
    }
    public ValueTask DisposeAsync() => new(DisconnectAsync());
}
