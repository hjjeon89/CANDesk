using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CANDesk.Hal.Candlelight;

internal static partial class NativeMethods
{
    public static readonly IntPtr InvalidHandleValue = new(-1);
    public const uint DigcfPresent = 0x00000002;
    public const uint DigcfDeviceInterface = 0x00000010;
    public const int ErrorNoMoreItems = 259;
    public const int ErrorSemTimeout = 121;
    public const int ErrorIoPending = 997;
    public const int ErrorOperationAborted = 995;
    public const uint GenericRead = 0x80000000;
    public const uint GenericWrite = 0x40000000;
    public const uint FileShareRead = 0x00000001;
    public const uint FileShareWrite = 0x00000002;
    public const uint OpenExisting = 3;
    public const uint FileAttributeNormal = 0x00000080;
    public const uint FileFlagOverlapped = 0x40000000;
    public const uint Infinite = 0xFFFF_FFFF;
    public const uint WaitObject0 = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct SpDeviceInterfaceData
    {
        public int CbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SpDevinfoData
    {
        public int CbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct UsbInterfaceDescriptor
    {
        public byte Length;
        public byte DescriptorType;
        public byte InterfaceNumber;
        public byte AlternateSetting;
        public byte NumEndpoints;
        public byte InterfaceClass;
        public byte InterfaceSubClass;
        public byte InterfaceProtocol;
        public byte Interface;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    public struct WinUsbPipeInformation
    {
        public int PipeType;
        public byte PipeId;
        public ushort MaximumPacketSize;
        public byte Interval;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WinUsbSetupPacket
    {
        public byte RequestType;
        public byte Request;
        public ushort Value;
        public ushort Index;
        public ushort Length;
    }

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData,
        ref Guid interfaceClassGuid, uint memberIndex, ref SpDeviceInterfaceData deviceInterfaceData);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet, ref SpDeviceInterfaceData deviceInterfaceData,
        IntPtr deviceInterfaceDetailData, uint deviceInterfaceDetailDataSize, out uint requiredSize, ref SpDevinfoData deviceInfoData);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceRegistryPropertyW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetupDiGetDeviceRegistryProperty(IntPtr deviceInfoSet, ref SpDevinfoData deviceInfoData,
        uint property, out uint propertyRegDataType, [Out] char[] propertyBuffer, int propertyBufferSize, out int requiredSize);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInstanceIdW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetupDiGetDeviceInstanceId(IntPtr deviceInfoSet, ref SpDevinfoData deviceInfoData,
        [Out] char[] deviceInstanceId, int deviceInstanceIdSize, out int requiredSize);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("winusb.dll", EntryPoint = "WinUsb_Initialize", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinUsbInitialize(SafeFileHandle deviceHandle, out SafeWinUsbHandle interfaceHandle);

    [DllImport("winusb.dll", EntryPoint = "WinUsb_QueryInterfaceSettings", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinUsbQueryInterfaceSettings(SafeWinUsbHandle interfaceHandle, byte alternateInterfaceNumber,
        out UsbInterfaceDescriptor usbAltInterfaceDescriptor);

    [DllImport("winusb.dll", EntryPoint = "WinUsb_QueryPipe", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinUsbQueryPipe(SafeWinUsbHandle interfaceHandle, byte alternateInterfaceNumber, byte pipeIndex,
        out WinUsbPipeInformation pipeInformation);

    [DllImport("winusb.dll", EntryPoint = "WinUsb_ControlTransfer", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinUsbControlTransfer(SafeWinUsbHandle interfaceHandle, WinUsbSetupPacket setupPacket,
        byte[] buffer, int bufferLength, out int lengthTransferred, IntPtr overlapped);

    [DllImport("winusb.dll", EntryPoint = "WinUsb_ReadPipe", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinUsbReadPipe(SafeWinUsbHandle interfaceHandle, byte pipeId, byte[] buffer, int bufferLength,
        out int lengthTransferred, IntPtr overlapped);

    [DllImport("winusb.dll", EntryPoint = "WinUsb_ReadPipe", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinUsbReadPipeOverlapped(SafeWinUsbHandle interfaceHandle, byte pipeId, IntPtr buffer,
        int bufferLength, IntPtr lengthTransferred, ref NativeOverlapped overlapped);

    [DllImport("winusb.dll", EntryPoint = "WinUsb_GetOverlappedResult", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinUsbGetOverlappedResult(SafeWinUsbHandle interfaceHandle, ref NativeOverlapped overlapped,
        out int lengthTransferred, [MarshalAs(UnmanagedType.Bool)] bool wait);

    [DllImport("winusb.dll", EntryPoint = "WinUsb_WritePipe", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinUsbWritePipe(SafeWinUsbHandle interfaceHandle, byte pipeId, byte[] buffer, int bufferLength,
        out int lengthTransferred, IntPtr overlapped);

    [DllImport("winusb.dll", EntryPoint = "WinUsb_AbortPipe", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinUsbAbortPipe(SafeWinUsbHandle interfaceHandle, byte pipeId);

    [DllImport("winusb.dll", EntryPoint = "WinUsb_SetPipePolicy", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinUsbSetPipePolicy(SafeWinUsbHandle interfaceHandle, byte pipeId, uint policyType, int valueLength,
        ref uint value);

    [DllImport("winusb.dll", EntryPoint = "WinUsb_Free")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinUsbFree(IntPtr interfaceHandle);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateEventW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr CreateEvent(IntPtr eventAttributes, [MarshalAs(UnmanagedType.Bool)] bool manualReset,
        [MarshalAs(UnmanagedType.Bool)] bool initialState, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint WaitForSingleObject(IntPtr handle, uint milliseconds);
}

internal sealed class SafeWinUsbHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private SafeWinUsbHandle() : base(true)
    {
    }

    protected override bool ReleaseHandle() => NativeMethods.WinUsbFree(handle);
}
