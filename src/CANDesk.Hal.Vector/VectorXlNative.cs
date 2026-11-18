using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

namespace CANDesk.Hal.Vector;

/// <summary>
/// Thin P/Invoke surface over Vector's XL Driver Library (<c>vxlapi.dll</c>/<c>vxlapi64.dll</c>),
/// for Classic CAN (V3) and ISO CAN FD (V4). Layouts follow Vector's XL Driver Library
/// Manual 26.30, sections 3.4 and 6.4–6.5.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class VectorXlNative
{
    private const string DllName = "vxlapi64.dll";

    // Port label only; discovery reads hardware directly without application-channel mappings.
    public const string ApplicationName = "CANDesk";
    public const uint BusTypeCan = 1;
    public const uint BusActiveCapCan = 0x0001_0000;
    public const uint InterfaceVersionV3 = 3;
    public const uint InterfaceVersionV4 = 4;
    public const short QueueIsEmpty = 10;
    public const uint ChannelFlagCanFdIsoSupport = 0x8000_0000;

    public const ushort CanFdEventTagReceiveOk = 0x0400;
    public const ushort CanFdEventTagTransmitMsg = 0x0440;
    public const uint CanFdMsgFlagEdl = 0x0001;
    public const uint CanFdMsgFlagBrs = 0x0002;
    public const uint CanFdMsgFlagRtr = 0x0010;
    public const uint CanFdRxMsgFlagErrorFrame = 0x0200;

    // Only fields needed for capability and active timing lookup are exposed; the remaining bytes belong to
    // the SDK. XLchannelConfig is packed to one byte and has a fixed 227-byte ABI.
    [StructLayout(LayoutKind.Explicit, Pack = 1, Size = 227)]
    public unsafe struct XLchannelConfig
    {
        [FieldOffset(0)] public fixed byte Name[32];
        [FieldOffset(34)] public byte HardwareChannel;
        [FieldOffset(42)] public ulong ChannelMask;
        [FieldOffset(50)] public uint ChannelCapabilities;
        [FieldOffset(54)] public uint ChannelBusCapabilities;
        [FieldOffset(63)] public uint BusType;
        [FieldOffset(67)] public uint ArbitrationBitRate;
        [FieldOffset(72)] public byte Tseg1Abr;
        [FieldOffset(73)] public byte Tseg2Abr;
        [FieldOffset(77)] public byte Tseg1Dbr;
        [FieldOffset(78)] public byte Tseg2Dbr;
        [FieldOffset(79)] public uint DataBitRate;
        [FieldOffset(83)] public byte CanOpMode;
        [FieldOffset(147)] public uint SerialNumber;
    }

    [InlineArray(64)]
    public struct XLchannelConfigs
    {
        private XLchannelConfig _element0;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct XLdriverConfig
    {
        public uint DllVersion;
        public uint ChannelCount;
        public fixed uint Reserved[10];
        public XLchannelConfigs Channels;
    }

    public static XLchannelConfig? FindChannel(in XLdriverConfig config, ulong channelMask)
    {
        for (var i = 0; i < Math.Min(config.ChannelCount, 64u); i++)
        {
            var channel = config.Channels[i];
            if (channel.ChannelMask == channelMask)
                return channel;
        }
        return null;
    }

    public static bool SupportsIsoCanFd(in XLdriverConfig config, ulong channelMask) =>
        FindChannel(config, channelMask) is { } channel && (channel.ChannelCapabilities & ChannelFlagCanFdIsoSupport) != 0;

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct XLcanFdConf
    {
        public uint ArbitrationBitRate;
        public uint SjwAbr;
        public uint Tseg1Abr;
        public uint Tseg2Abr;
        public uint DataBitRate;
        public uint SjwDbr;
        public uint Tseg1Dbr;
        public uint Tseg2Dbr;
        public byte Reserved;
        public byte Options;
        public fixed byte Reserved1[2];
        public uint Reserved2;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct XLcanTxEvent
    {
        public ushort Tag;
        public ushort TransId;
        public byte ChannelIndex;
        public fixed byte Reserved[3];
        public uint CanId;
        public uint MsgFlags;
        public byte Dlc;
        public fixed byte Reserved1[7];
        public fixed byte Data[64];
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct XLcanRxEvent
    {
        public uint Size;
        public ushort Tag;
        public byte ChannelIndex;
        public byte Reserved;
        public uint UserHandle;
        public ushort FlagsChip;
        public ushort Reserved0;
        public ulong Reserved1;
        public ulong TimeStamp;
        // XL_CAN_EV_RX_MSG is the largest member of the event union (96 bytes).
        public uint CanId;
        public uint MsgFlags;
        public uint Crc;
        public fixed byte Reserved2[12];
        public ushort TotalBitCount;
        public byte Dlc;
        public fixed byte Reserved3[5];
        public fixed byte Data[64];
    }

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

    [LibraryImport(DllName, EntryPoint = "xlOpenPort", StringMarshalling = StringMarshalling.Utf8)]
    public static partial short OpenPort(out int portHandle, string userName, ulong accessMask, ref ulong permissionMask, uint rxQueueSize, uint xlInterfaceVersion, uint busType);

    [LibraryImport(DllName, EntryPoint = "xlGetDriverConfig")]
    public static partial short GetDriverConfig(out XLdriverConfig config);

    [LibraryImport(DllName, EntryPoint = "xlCanFdSetConfiguration")]
    public static partial short CanFdSetConfiguration(int portHandle, ulong accessMask, in XLcanFdConf config);

    [LibraryImport(DllName, EntryPoint = "xlCanTransmitEx")]
    public static partial short CanTransmitEx(int portHandle, ulong accessMask, uint messageCount, out uint sentCount, in XLcanTxEvent message);

    [LibraryImport(DllName, EntryPoint = "xlCanReceive")]
    public static partial short CanReceive(int portHandle, out XLcanRxEvent xlEvent);

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
