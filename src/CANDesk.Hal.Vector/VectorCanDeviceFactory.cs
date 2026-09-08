using System.Runtime.Versioning;
using CANDesk.Hal;

namespace CANDesk.Hal.Vector;

/// <summary>
/// Discovers Vector channels the user has mapped to the <see cref="VectorXlNative.ApplicationName"/>
/// application via the Vector Hardware Config utility (Start Menu → Vector Hardware → Vector
/// Hardware Config; add an application with that exact name and assign its channel slots to
/// physical hardware). This is the standard way 3rd-party tools integrate with the XL
/// Driver Library without parsing the large, version-sensitive <c>XLdriverConfig</c> struct.
/// If <c>vxlapi64.dll</c> is not installed, <see cref="EnumerateAsync"/> returns an empty list
/// instead of throwing (Project_Architecture.md §11: "미설치 시 해당 벤더만 비활성화").
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class VectorCanDeviceFactory : ICanDeviceFactory, ICanDeviceFactoryDiagnostics
{
    private const uint MaxAppChannels = 8;

    public string Vendor => "Vector";

    /// <summary>Per-appChannel XL status codes from the last <see cref="EnumerateAsync"/> call, so a
    /// "no channel found" failure can say exactly which call rejected which app channel instead of
    /// just "not found" — see <see cref="ICanDeviceFactoryDiagnostics"/>.</summary>
    public string LastEnumerationDiagnostics { get; private set; } = string.Empty;

    public Task<IReadOnlyList<CanDeviceDescriptor>> EnumerateAsync(CancellationToken cancellationToken = default)
    {
        var descriptors = new List<CanDeviceDescriptor>();
        var log = new List<string>();
        try
        {
            // xlGetApplConfig/xlGetChannelMask are config queries, but Vector's own samples always
            // call xlOpenDriver before any other xl* call, so acquire the (ref-counted) driver scope
            // here too rather than assuming query-only calls are safe before it.
            using var driverScope = VectorDriverScope.Acquire();
            for (var appChannel = 0u; appChannel < MaxAppChannels; appChannel++)
            {
                var status = VectorXlNative.GetApplConfig(VectorXlNative.ApplicationName, appChannel,
                    out var hwType, out var hwIndex, out var hwChannel, VectorXlNative.BusTypeCan);
                if (status != 0)
                {
                    log.Add($"appChannel {appChannel}: xlGetApplConfig(\"{VectorXlNative.ApplicationName}\") returned XL status {status} (no channel assigned to this app slot in Vector Hardware Config).");
                    continue;
                }

                var mask = VectorXlNative.GetChannelMask((int)hwType, (int)hwIndex, (int)hwChannel);
                if (mask == 0)
                {
                    log.Add($"appChannel {appChannel}: xlGetApplConfig succeeded (hw {hwType}/{hwIndex}/{hwChannel}) but xlGetChannelMask returned 0 (that hardware isn't present/connected).");
                    continue;
                }

                log.Add($"appChannel {appChannel}: found hw {hwType}/{hwIndex}/{hwChannel}, channel mask 0x{mask:X}.");
                var name = $"App Channel {appChannel} (hw {hwType}/{hwIndex}/{hwChannel})";
                descriptors.Add(new CanDeviceDescriptor(Vendor, mask.ToString("X"), name, [name], SupportsCanFd: false));
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

    public Task<ICanDevice> CreateAsync(string deviceId, string channelName, CancellationToken cancellationToken = default)
    {
        if (!ulong.TryParse(deviceId, System.Globalization.NumberStyles.HexNumber, null, out var channelMask))
        {
            throw new ArgumentException($"'{deviceId}' is not a valid Vector channel mask.", nameof(deviceId));
        }

        return Task.FromResult<ICanDevice>(new VectorCanDevice(channelMask, channelName));
    }
}
