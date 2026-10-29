using System.Runtime.Versioning;
using CANDesk.Hal;

namespace CANDesk.Hal.Peak;

/// <summary>
/// Discovers PEAK PCAN-USB channels via PCAN-Basic. If <c>PCANBasic.dll</c> is not installed,
/// <see cref="EnumerateAsync"/> returns an empty list instead of throwing, so the vendor is simply
/// absent from the UI rather than crashing the app (Project_Architecture.md §11: "미설치 시 해당
/// 벤더만 비활성화, 앱 크래시 방지").
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PeakCanDeviceFactory : ICanDeviceFactory, ICanDeviceFactoryDiagnostics
{
    public string Vendor => "PEAK";
    public string LastEnumerationDiagnostics { get; private set; } = string.Empty;

    public unsafe Task<IReadOnlyList<CanDeviceDescriptor>> EnumerateAsync(CancellationToken cancellationToken = default)
    {
        var descriptors = new List<CanDeviceDescriptor>();
        LastEnumerationDiagnostics = string.Empty;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Discovery must not activate a Classic controller on a live CAN FD bus.
            var result = PCanBasicNative.GetValue(0, PCanBasicNative.AttachedChannelsCount, out var count, sizeof(uint));
            if (result != PCanBasicNative.StatusOk)
                throw new InvalidOperationException($"PCAN attached-channel count query failed: 0x{result:X}. PCAN-Basic 4.4 or later is required.");
            if (count > 1024) throw new InvalidOperationException($"PCAN returned an invalid channel count: {count}.");
            if (count == 0) return Task.FromResult<IReadOnlyList<CanDeviceDescriptor>>(descriptors);

            var channels = new PCanBasicNative.TPCANChannelInformation[count];
            fixed (PCanBasicNative.TPCANChannelInformation* buffer = channels)
                result = PCanBasicNative.GetValueBytes(0, PCanBasicNative.AttachedChannels, (byte*)buffer,
                    checked((uint)(channels.Length * sizeof(PCanBasicNative.TPCANChannelInformation))));
            if (result != PCanBasicNative.StatusOk)
                throw new InvalidOperationException($"PCAN attached-channel query failed: 0x{result:X}.");

            foreach (var (handle, name) in PeakChannels.UsbChannels)
            {
                var channel = channels.FirstOrDefault(channel => channel.ChannelHandle == handle);
                if (channel.ChannelCondition == 0) continue;
                var supportsFd = (channel.DeviceFeatures & PCanBasicNative.FeatureFdCapable) != 0;
                descriptors.Add(new CanDeviceDescriptor(Vendor, handle.ToString("X"), $"{name} (ID: {channel.DeviceId:X8})",
                    [$"Channel {handle - 0x50}", name], SupportsCanFd: supportsFd,
                    ClockFrequencyHz: supportsFd ? PeakBitTiming.FdClockHz : null));
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            LastEnumerationDiagnostics = exception.Message;
        }

        return Task.FromResult<IReadOnlyList<CanDeviceDescriptor>>(descriptors);
    }

    public Task<ICanDevice> CreateAsync(string deviceId, string channelName, CancellationToken cancellationToken = default)
    {
        if (!ushort.TryParse(deviceId, System.Globalization.NumberStyles.HexNumber, null, out var handle))
        {
            throw new ArgumentException($"'{deviceId}' is not a valid PEAK channel handle.", nameof(deviceId));
        }

        return Task.FromResult<ICanDevice>(new PeakCanDevice(handle, channelName));
    }
}
