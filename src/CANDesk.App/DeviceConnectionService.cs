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

    /// <summary>Connects using the vendor picked in <c>DeviceConnectionViewModel.SelectedVendor</c>
    /// (a display label like "PEAK"). Enumerates the vendor's real hardware and picks the descriptor
    /// matching the requested channel name. PEAK channel aliases select the physical USB channel.</summary>
    public async Task ConnectByVendorAsync(string vendorLabel, string channelName, CanBusConfig configuration, CancellationToken cancellationToken = default)
    {
        var vendor = vendorLabel.Split(' ')[0];
        var factory = factories.FirstOrDefault(factory => factory.Vendor == vendor)
            ?? throw new InvalidOperationException($"No CAN driver is registered for vendor '{vendor}'.");

        var descriptors = await factory.EnumerateAsync(cancellationToken).ConfigureAwait(false);
        var descriptor = descriptors.FirstOrDefault(d => d.Channels.Contains(channelName));
        // PEAK aliases identify physical USB channels; never silently substitute a different bus.
        if (descriptor is null && vendor != "PEAK") descriptor = descriptors.FirstOrDefault();
        if (descriptor is null)
        {
            var diagnostics = (factory as ICanDeviceFactoryDiagnostics)?.LastEnumerationDiagnostics;
            var detail = string.IsNullOrEmpty(diagnostics) ? string.Empty : $" ({diagnostics})";
            throw new InvalidOperationException(
                $"No {vendor} {channelName} was found. Make sure the vendor SDK/driver is installed and at least one channel is available.{detail}");
        }

        await ConnectAsync(factory, descriptor.DeviceId, descriptor.DisplayName, configuration, cancellationToken).ConfigureAwait(false);
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
