using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace CANDesk.Hal.Vector;

/// <summary>
/// Thin P/Invoke surface over Vector's XL Driver Library (<c>vxlapi.dll</c>/<c>vxlapi64.dll</c>),
/// scoped to Classic CAN (legacy <c>XLevent</c> API, interface version 3) open/close/send/receive/
/// reset. Struct field order and sizes follow the publicly documented XL Driver Library API and
/// widely-referenced open-source bindings (e.g. python-can's vector interface); they were written
/// without the vendor header in hand, so verify <see cref="XLevent"/>'s layout against the
/// installed vxlapi.h before the first real hardware run. CAN-FD (interface version 4,
/// <c>XLcanFdConf</c>/<c>XLcanTxEvent</c>/<c>XLcanRxEvent</c>) is intentionally out of scope for v1,
/// matching Project_Architecture.md §11 ("1차 릴리스는 Classic CAN 우선").
///
/// Enumeration relies on <see cref="GetApplConfig"/> + <see cref="GetChannelMask"/> rather than
/// parsing <c>XLdriverConfig</c> (a large, version-sensitive struct) — this is the same pattern
/// Vector's own sample code uses for "application channel" integration, and requires the user to
/// map a "CANDesk" application to physical channels once via the Vector Hardware Config utility.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class VectorXlNative
{
    private const string DllName = "vxlapi64.dll";

    // Must exactly match the application name registered in Vector Hardware Config (Start Menu →
    // Vector Hardware → Vector Hardware Config → Applications), since xlGetApplConfig looks it up
    // by this exact string. "xlCanGP" is what this project's own Vector Hardware Config is set up
    // with; rename here (and re-register in Vector Hardware Config) together if it ever changes.
    public const string ApplicationName = "xlCanGP";
    public const uint BusTypeCan = 1;
    public const uint InterfaceVersionV3 = 3;

    public const byte EventTagReceiveMsg = 1;
    public const byte EventTagChipState = 4;
    public const byte EventTagTransmitMsg = 10;

    public const ushort CanMsgFlagErrorFrame = 0x0001;
    public const ushort CanMsgFlagOverrun = 0x0002;
    public const ushort CanMsgFlagRemoteFrame = 0x0010;
    public const ushort CanMsgFlagTxRequest = 0x0080;

    public const uint ExtendedMessageIdFlag = 0x8000_0000;

    /// <summary>32-byte <c>XLcanMsg</c> field offsets within <see cref="XLevent.TagData"/>: id
    /// (uint,0) / flags (ushort,4) / dlc (ushort,6) / res1 (ulong,8) / data (byte[8],16) / res2
    /// (ulong,24).</summary>
    private const int CanMsgDataOffset = 16;

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct XLevent
    {
        public byte Tag;
        public byte ChanIndex;
        public ushort TransId;
        public ushort PortHandle;
        public fixed byte Reserved[2];
        public ulong TimeStamp;
        public fixed byte TagData[32];
    }

    public static unsafe void WriteCanMsg(ref XLevent xlEvent, uint id, ushort flags, ushort dlc, ReadOnlySpan<byte> data)
    {
        fixed (byte* tagData = xlEvent.TagData)
        {
            *(uint*)tagData = id;
            *(ushort*)(tagData + 4) = flags;
            *(ushort*)(tagData + 6) = dlc;
            var dataPtr = tagData + CanMsgDataOffset;
            for (var i = 0; i < 8; i++)
            {
                dataPtr[i] = i < data.Length ? data[i] : (byte)0;
            }
        }
    }

    public static unsafe (uint Id, ushort Flags, ushort Dlc, byte[] Data) ReadCanMsg(in XLevent xlEvent)
    {
        fixed (byte* tagData = xlEvent.TagData)
        {
            var id = *(uint*)tagData;
            var flags = *(ushort*)(tagData + 4);
            var dlc = *(ushort*)(tagData + 6);
            var data = new byte[8];
            var dataPtr = tagData + CanMsgDataOffset;
            for (var i = 0; i < 8; i++)
            {
                data[i] = dataPtr[i];
            }

            return (id, flags, dlc, data);
        }
    }

    [LibraryImport(DllName, EntryPoint = "xlOpenDriver")]
    public static partial short OpenDriver();

    [LibraryImport(DllName, EntryPoint = "xlCloseDriver")]
    public static partial short CloseDriver();

    [LibraryImport(DllName, EntryPoint = "xlGetApplConfig", StringMarshalling = StringMarshalling.Utf8)]
    public static partial short GetApplConfig(string appName, uint appChannel, out uint hwType, out uint hwIndex, out uint hwChannel, uint busType);

    [LibraryImport(DllName, EntryPoint = "xlGetChannelMask")]
    public static partial ulong GetChannelMask(int hwType, int hwIndex, int hwChannel);

    [LibraryImport(DllName, EntryPoint = "xlOpenPort", StringMarshalling = StringMarshalling.Utf8)]
    public static partial short OpenPort(out int portHandle, string userName, ulong accessMask, out ulong permissionMask, uint rxQueueSize, uint xlInterfaceVersion, uint busType);

    [LibraryImport(DllName, EntryPoint = "xlActivateChannel")]
    public static partial short ActivateChannel(int portHandle, ulong accessMask, uint busType, uint flags);

    [LibraryImport(DllName, EntryPoint = "xlDeactivateChannel")]
    public static partial short DeactivateChannel(int portHandle, ulong accessMask);

    [LibraryImport(DllName, EntryPoint = "xlClosePort")]
    public static partial short ClosePort(int portHandle);

    [LibraryImport(DllName, EntryPoint = "xlCanSetChannelBitrate")]
    public static partial short CanSetChannelBitrate(int portHandle, ulong accessMask, uint bitrate);

    [LibraryImport(DllName, EntryPoint = "xlCanTransmit")]
    public static partial short CanTransmit(int portHandle, ulong accessMask, ref uint messageCount, ref XLevent message);

    [LibraryImport(DllName, EntryPoint = "xlReceive")]
    public static partial short Receive(int portHandle, ref uint eventCount, out XLevent xlEvent);

    [LibraryImport(DllName, EntryPoint = "xlSetNotification")]
    public static partial short SetNotification(int portHandle, out nint xlHandle, int queueLevel);

    [LibraryImport(DllName, EntryPoint = "xlResetClock")]
    public static partial short ResetClock(int portHandle);
}
