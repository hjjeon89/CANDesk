using System.Runtime.Versioning;
using CANDesk.Hal;

namespace CANDesk.Hal.Vector;

/// <summary>
/// Discovers all available CAN channels directly from <c>XLdriverConfig</c>, including their
/// names, serial numbers and CAN FD capabilities. No application-channel mapping or port activation is required.
/// If <c>vxlapi64.dll</c> is not installed, <see cref="EnumerateAsync"/> returns an empty list
/// instead of throwing (Project_Architecture.md §11: "미설치 시 해당 벤더만 비활성화").
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class VectorCanDeviceFactory : ICanDeviceFactory, ICanDeviceFactoryDiagnostics
{
    public string Vendor => "Vector";

    /// <summary>Driver enumeration results from the last <see cref="EnumerateAsync"/> call.</summary>
    public string LastEnumerationDiagnostics { get; private set; } = string.Empty;

    public Task<IReadOnlyList<CanDeviceDescriptor>> EnumerateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<CanDeviceDescriptor> descriptors = [];
        var log = new List<string>();
        try
        {
            using var driverScope = VectorDriverScope.Acquire();
            var status = VectorXlNative.GetDriverConfig(out var driverConfig);
            if (status != 0)
            {
                log.Add($"xlGetDriverConfig returned XL status {status}.");
            }
            else
            {
                descriptors = DescribeChannels(driverConfig);
                log.Add($"xlGetDriverConfig reported {driverConfig.ChannelCount} channels; {descriptors.Count} support CAN.");
                log.AddRange(descriptors.Select(device => $"{device.DisplayName}, channel mask 0x{device.DeviceId}, CAN FD: {device.SupportsCanFd}."));
            }
        }
        catch (DllNotFoundException)
        {
            // XL Driver Library is not installed on this machine; report no Vector devices instead of crashing.
            log.Add("vxlapi64.dll was not found (XL Driver Library not installed).");
        }
        catch (EntryPointNotFoundException exception)
        {
            // An installed vxlapi64.dll that is older/newer than expected and is missing an entry point.
            log.Add($"vxlapi64.dll is missing an expected entry point: {exception.Message}");
        }

        LastEnumerationDiagnostics = string.Join(" | ", log);
        return Task.FromResult<IReadOnlyList<CanDeviceDescriptor>>(descriptors);
    }

    internal static unsafe IReadOnlyList<CanDeviceDescriptor> DescribeChannels(in VectorXlNative.XLdriverConfig config)
    {
        var descriptors = new List<CanDeviceDescriptor>();
        for (var index = 0; index < Math.Min(config.ChannelCount, 64u); index++)
        {
            var channel = config.Channels[index];
            if (channel.ChannelMask == 0 || (channel.ChannelBusCapabilities & VectorXlNative.BusActiveCapCan) == 0)
                continue;

            var nameBytes = new ReadOnlySpan<byte>(channel.Name, 32);
            var terminator = nameBytes.IndexOf((byte)0);
            var name = System.Text.Encoding.ASCII.GetString(terminator < 0 ? nameBytes : nameBytes[..terminator]);
            if (string.IsNullOrWhiteSpace(name)) name = $"Vector CAN Channel {channel.HardwareChannel + 1}";
            var displayName = $"{name} (S/N {channel.SerialNumber})";
            var supportsFd = (channel.ChannelCapabilities & VectorXlNative.ChannelFlagCanFdIsoSupport) != 0;
            descriptors.Add(new("Vector", channel.ChannelMask.ToString("X"), displayName, [name],
                SupportsCanFd: supportsFd, ClockFrequencyHz: supportsFd ? VectorBitTiming.ClockHz : null));
        }
        return descriptors;
    }

    public Task<ICanDevice> CreateAsync(string deviceId, string channelName, CancellationToken cancellationToken = default)
    {
        if (!ulong.TryParse(deviceId, System.Globalization.NumberStyles.HexNumber, null, out var channelMask))
        {
            throw new ArgumentException($"'{deviceId}' is not a valid Vector channel mask.", nameof(deviceId));
        }

        return Task.FromResult<ICanDevice>(new VectorCanDevice(channelMask, channelName));
    }
}
