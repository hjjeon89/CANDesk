using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace CANDesk.Hal.Candlelight;

[SupportedOSPlatform("windows")]
internal static class WinUsbDeviceDiscovery
{
    private static readonly Guid CandlelightInterfaceGuid = new("c15b4308-04d3-11e6-b3ea-6057189e6443");
    private const uint SpdrpDeviceDesc = 0;
    private const uint SpdrpLocationInformation = 13;

    public static IEnumerable<WinUsbDeviceInfo> EnumerateGsUsbDevices()
    {
        var candlelightInterfaceGuid = CandlelightInterfaceGuid;
        var deviceInfoSet = NativeMethods.SetupDiGetClassDevs(ref candlelightInterfaceGuid, null, IntPtr.Zero,
            NativeMethods.DigcfPresent | NativeMethods.DigcfDeviceInterface);
        if (deviceInfoSet == NativeMethods.InvalidHandleValue)
        {
            yield break;
        }

        try
        {
            for (uint index = 0; ; index++)
            {
                var interfaceData = new NativeMethods.SpDeviceInterfaceData { CbSize = Marshal.SizeOf<NativeMethods.SpDeviceInterfaceData>() };
                if (!NativeMethods.SetupDiEnumDeviceInterfaces(deviceInfoSet, IntPtr.Zero, ref candlelightInterfaceGuid, index, ref interfaceData))
                {
                    if (Marshal.GetLastWin32Error() == NativeMethods.ErrorNoMoreItems)
                    {
                        yield break;
                    }

                    continue;
                }

                var deviceInfoData = new NativeMethods.SpDevinfoData { CbSize = Marshal.SizeOf<NativeMethods.SpDevinfoData>() };
                var devicePath = GetDevicePath(deviceInfoSet, ref interfaceData, ref deviceInfoData);
                if (devicePath is null)
                {
                    continue;
                }

                var instanceId = GetInstanceId(deviceInfoSet, ref deviceInfoData);
                if (string.IsNullOrWhiteSpace(instanceId))
                {
                    continue;
                }

                var name = GetRegistryProperty(deviceInfoSet, ref deviceInfoData, SpdrpDeviceDesc);
                var location = GetRegistryProperty(deviceInfoSet, ref deviceInfoData, SpdrpLocationInformation);
                yield return new WinUsbDeviceInfo(instanceId, devicePath, name, location);
            }
        }
        finally
        {
            NativeMethods.SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }
    }

    private static string? GetDevicePath(IntPtr deviceInfoSet, ref NativeMethods.SpDeviceInterfaceData interfaceData,
        ref NativeMethods.SpDevinfoData deviceInfoData)
    {
        NativeMethods.SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData, IntPtr.Zero, 0, out var requiredSize,
            ref deviceInfoData);
        if (requiredSize == 0)
        {
            return null;
        }

        var detailData = Marshal.AllocHGlobal((int)requiredSize);
        try
        {
            Marshal.WriteInt32(detailData, IntPtr.Size == 8 ? 8 : 6);
            if (!NativeMethods.SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData, detailData, requiredSize,
                out _, ref deviceInfoData))
            {
                return null;
            }

            return Marshal.PtrToStringUni(IntPtr.Add(detailData, 4));
        }
        finally
        {
            Marshal.FreeHGlobal(detailData);
        }
    }

    private static string? GetInstanceId(IntPtr deviceInfoSet, ref NativeMethods.SpDevinfoData deviceInfoData)
    {
        var buffer = new char[512];
        return NativeMethods.SetupDiGetDeviceInstanceId(deviceInfoSet, ref deviceInfoData, buffer, buffer.Length, out _)
            ? TrimNull(buffer)
            : null;
    }

    private static string GetRegistryProperty(IntPtr deviceInfoSet, ref NativeMethods.SpDevinfoData deviceInfoData, uint property)
    {
        var buffer = new char[512];
        return NativeMethods.SetupDiGetDeviceRegistryProperty(deviceInfoSet, ref deviceInfoData, property, out _, buffer,
            buffer.Length * sizeof(char), out _)
            ? TrimNull(buffer)
            : string.Empty;
    }

    private static string TrimNull(char[] buffer)
    {
        var end = Array.IndexOf(buffer, '\0');
        return new string(buffer, 0, end >= 0 ? end : buffer.Length);
    }
}

internal sealed record WinUsbDeviceInfo(string InstanceId, string DevicePath, string Name, string Location);
