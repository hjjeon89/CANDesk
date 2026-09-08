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

    /// <summary>Connects using the vendor picked in <c>DeviceConnectionViewModel.SelectedVendor</c>
    /// (a display label like "PEAK"). Enumerates the vendor's real hardware and picks the descriptor
    /// matching the requested channel name, falling back to the first one found.</summary>
    public async Task ConnectByVendorAsync(string vendorLabel, string channelName, CanBusConfig configuration, CancellationToken cancellationToken = default)
    {
        var vendor = vendorLabel.Split(' ')[0];
        var factory = factories.FirstOrDefault(factory => factory.Vendor == vendor)
            ?? throw new InvalidOperationException($"No CAN driver is registered for vendor '{vendor}'.");

        var descriptors = await factory.EnumerateAsync(cancellationToken).ConfigureAwait(false);
        var descriptor = descriptors.FirstOrDefault(d => d.Channels.Contains(channelName)) ?? descriptors.FirstOrDefault();
        if (descriptor is null)
        {
            var diagnostics = (factory as ICanDeviceFactoryDiagnostics)?.LastEnumerationDiagnostics;
            var detail = string.IsNullOrEmpty(diagnostics) ? string.Empty : $" ({diagnostics})";
            throw new InvalidOperationException(
                $"No {vendor} channel was found. Make sure the vendor SDK/driver is installed and at least one channel is available.{detail}");
        }

        await ConnectAsync(factory, descriptor.DeviceId, descriptor.DisplayName, configuration, cancellationToken).ConfigureAwait(false);
    }

    public async Task ConnectAsync(ICanDeviceFactory factory, string deviceId, string channelName, CanBusConfig configuration, CancellationToken cancellationToken = default)
    {
        configuration.Validate();
        await DisconnectAsync(cancellationToken).ConfigureAwait(false);
        var device = await factory.CreateAsync(deviceId, channelName, cancellationToken).ConfigureAwait(false);
        try { await device.OpenAsync(configuration, cancellationToken).ConfigureAwait(false); CurrentDevice = device; }
        catch { await device.DisposeAsync().ConfigureAwait(false); throw; }
    }
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (CurrentDevice is null) return;
        await CurrentDevice.CloseAsync(cancellationToken).ConfigureAwait(false);
        await CurrentDevice.DisposeAsync().ConfigureAwait(false);
        CurrentDevice = null;
    }
    public ValueTask DisposeAsync() => new(DisconnectAsync());
}
