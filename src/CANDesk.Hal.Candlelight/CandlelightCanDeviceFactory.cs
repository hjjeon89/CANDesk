using System.Runtime.Versioning;
using CANDesk.Hal;

namespace CANDesk.Hal.Candlelight;

/// <summary>
/// Discovers CANable/candleLight devices running gs_usb compatible firmware through the Windows
/// USB device interface. The descriptor DeviceId is the Windows hardware instance id so the app can
/// reconnect to the exact physical adapter the user selected.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CandlelightCanDeviceFactory : ICanDeviceFactory, ICanDeviceFactoryDiagnostics
{
    public string Vendor => "CANable";
    public string LastEnumerationDiagnostics { get; private set; } = string.Empty;

    public Task<IReadOnlyList<CanDeviceDescriptor>> EnumerateAsync(CancellationToken cancellationToken = default)
    {
        var devices = WinUsbDeviceDiscovery.EnumerateGsUsbDevices().ToList();
        LastEnumerationDiagnostics = devices.Count == 0
            ? "No Candlelight interface 0 device was found for GUID c15b4308-04d3-11e6-b3ea-6057189e6443."
            : string.Join(" | ", devices.Select(device => $"{device.InstanceId} {device.Location}".TrimEnd()));

        IReadOnlyList<CanDeviceDescriptor> descriptors = devices
            .Select(device => new CanDeviceDescriptor(
                Vendor,
                device.InstanceId,
                CreateDisplayName(device),
                [$"Channel 0 - {CreateDisplayName(device)}"],
                SupportsCanFd: true))
            .ToList();
        return Task.FromResult(descriptors);
    }

    public Task<ICanDevice> CreateAsync(string deviceId, string channelName, CancellationToken cancellationToken = default)
    {
        var device = WinUsbDeviceDiscovery.EnumerateGsUsbDevices()
            .FirstOrDefault(candidate => string.Equals(candidate.InstanceId, deviceId, StringComparison.OrdinalIgnoreCase));
        if (device is null)
        {
            throw new InvalidOperationException($"CANable Candlelight device '{deviceId}' is not present.");
        }

        return Task.FromResult<ICanDevice>(new CandlelightCanDevice(device.DevicePath, channelName));
    }

    private static string CreateDisplayName(WinUsbDeviceInfo device)
    {
        var name = string.IsNullOrWhiteSpace(device.Name) ? "CANable Candlelight" : device.Name;
        var suffix = string.IsNullOrWhiteSpace(device.Location) ? ShortInstanceId(device.InstanceId) : device.Location;
        return $"{name} [{suffix}]";
    }

    private static string ShortInstanceId(string instanceId)
    {
        var slash = instanceId.LastIndexOf('\\');
        return slash >= 0 && slash < instanceId.Length - 1 ? instanceId[(slash + 1)..] : instanceId;
    }
}
