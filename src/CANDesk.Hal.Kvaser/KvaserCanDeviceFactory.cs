using System.Runtime.Versioning;
using System.Text;
using CANDesk.Hal;

namespace CANDesk.Hal.Kvaser;

[SupportedOSPlatform("windows")]
public sealed class KvaserCanDeviceFactory : ICanDeviceFactory, ICanDeviceFactoryDiagnostics
{
    public string Vendor => "Kvaser";
    public string LastEnumerationDiagnostics { get; private set; } = string.Empty;

    public Task<IReadOnlyList<CanDeviceDescriptor>> EnumerateAsync(CancellationToken cancellationToken = default)
    {
        var descriptors = new List<CanDeviceDescriptor>();
        var log = new List<string>();
        try
        {
            KvaserCanlibNative.InitializeLibrary();
            var status = KvaserCanlibNative.GetNumberOfChannels(out var channelCount);
            if (status != KvaserCanlibNative.StatusOk)
            {
                log.Add($"canGetNumberOfChannels returned CANlib status {status}.");
                LastEnumerationDiagnostics = string.Join(" | ", log);
                return Task.FromResult<IReadOnlyList<CanDeviceDescriptor>>(descriptors);
            }

            for (var channel = 0; channel < channelCount; channel++)
            {
                var displayName = GetChannelName(channel, log);
                descriptors.Add(new CanDeviceDescriptor(Vendor, channel.ToString(), displayName, [displayName], SupportsCanFd: false));
            }

            log.Add($"CANlib reported {channelCount} channel(s).");
        }
        catch (DllNotFoundException)
        {
            log.Add("canlib32.dll was not found (Kvaser CANlib is not installed or not on the DLL search path).");
        }
        catch (EntryPointNotFoundException exception)
        {
            log.Add($"canlib32.dll is missing an expected entry point: {exception.Message}");
        }

        LastEnumerationDiagnostics = string.Join(" | ", log);
        return Task.FromResult<IReadOnlyList<CanDeviceDescriptor>>(descriptors);
    }

    public Task<ICanDevice> CreateAsync(string deviceId, string channelName, CancellationToken cancellationToken = default)
    {
        if (!int.TryParse(deviceId, out var channelIndex) || channelIndex < 0)
        {
            throw new ArgumentException($"'{deviceId}' is not a valid Kvaser channel index.", nameof(deviceId));
        }

        return Task.FromResult<ICanDevice>(new KvaserCanDevice(channelIndex, channelName));
    }

    private static string GetChannelName(int channel, List<string> log)
    {
        var buffer = new StringBuilder(128);
        var status = KvaserCanlibNative.GetChannelData(channel, KvaserCanlibNative.ChannelDataChannelName, buffer, (nuint)buffer.Capacity);
        if (status == KvaserCanlibNative.StatusOk && buffer.Length > 0)
        {
            return buffer.ToString();
        }

        log.Add($"channel {channel}: canGetChannelData(CHANNEL_NAME) returned CANlib status {status}; using a generic display name.");
        return $"Channel {channel + 1}";
    }
}
