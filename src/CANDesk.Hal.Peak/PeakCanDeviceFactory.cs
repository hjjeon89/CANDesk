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
public sealed class PeakCanDeviceFactory : ICanDeviceFactory
{
    public string Vendor => "PEAK";

    // Any standard bitrate works for the presence probe below; CAN_Initialize succeeding (and
    // getting cleanly undone by CAN_Uninitialize right after) just proves the channel exists and
    // isn't already claimed by another application — the real Connect later re-initializes with
    // whatever bitrate the user actually configured.
    private static readonly ushort ProbeBtr0Btr1 = PeakBitTiming.FromPreset(new BitTimingSetting(500, 87.5));

    public Task<IReadOnlyList<CanDeviceDescriptor>> EnumerateAsync(CancellationToken cancellationToken = default)
    {
        var descriptors = new List<CanDeviceDescriptor>();
        try
        {
            foreach (var (handle, name) in PeakChannels.UsbChannels)
            {
                var status = PCanBasicNative.Initialize(handle, ProbeBtr0Btr1, hwType: 0, ioPort: 0, interrupt: 0);
                if (status != PCanBasicNative.StatusOk)
                {
                    continue;
                }

                PCanBasicNative.Uninitialize(handle);
                descriptors.Add(new CanDeviceDescriptor(Vendor, handle.ToString("X"), name, [name], SupportsCanFd: false));
            }
        }
        catch (DllNotFoundException)
        {
            // PCAN-Basic driver is not installed on this machine; report no PEAK devices instead of crashing.
        }
        catch (EntryPointNotFoundException)
        {
            // An installed PCANBasic.dll that is older/newer than expected and is missing an entry point.
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
