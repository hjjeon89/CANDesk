using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace CANDesk.Hal.Peak;

/// <summary>
/// Thin P/Invoke surface over PEAK-System's PCAN-Basic API (<c>PCANBasic.dll</c>), scoped to
/// Classic CAN and CAN FD open/close/send/receive/reset/status. Layouts and constants follow
/// PEAK's public PCANBasic.h and PCANBasic.cs headers.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class PCanBasicNative
{
    private const string DllName = "PCANBasic.dll";

    // TPCANMsg.MSGTYPE bit flags.
    public const byte MessageStandard = 0x00;
    public const byte MessageRtr = 0x01;
    public const byte MessageExtended = 0x02;
    public const byte MessageFd = 0x04;
    public const byte MessageBitRateSwitch = 0x08;
    public const byte MessageErrorFrame = 0x40;
    public const byte MessageStatus = 0x80;

    // TPCANStatus bit flags (0 = OK; others can be OR'd together on CAN_GetStatus).
    public const uint StatusOk = 0x00000;
    public const uint StatusQueueReceiveEmpty = 0x00020;
    public const uint StatusBusLight = 0x00004;
    public const uint StatusBusHeavy = 0x00008;
    public const uint StatusBusOff = 0x00010;
    public const uint StatusBusPassive = 0x40000;

    public const byte BitrateInfoFd = 0x19;
    public const byte AttachedChannelsCount = 0x2A;
    public const byte AttachedChannels = 0x2B;
    public const uint FeatureFdCapable = 0x01;

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct TPCANMsg
    {
        public uint Id;
        public byte MsgType;
        public byte Length;
        public fixed byte Data[8];
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct TPCANMsgFD
    {
        public uint Id;
        public byte MsgType;
        public byte Dlc;
        public fixed byte Data[64];
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct TPCANChannelInformation
    {
        public ushort ChannelHandle;
        public byte DeviceType;
        public byte ControllerNumber;
        public uint DeviceFeatures;
        public fixed byte DeviceName[33];
        public uint DeviceId;
        public uint ChannelCondition;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TPCANTimestamp
    {
        public uint Millis;
        public ushort MillisOverflow;
        public ushort Micros;
    }

    [LibraryImport(DllName, EntryPoint = "CAN_Initialize")]
    public static partial uint Initialize(ushort channel, ushort btr0Btr1, byte hwType, uint ioPort, ushort interrupt);

    [LibraryImport(DllName, EntryPoint = "CAN_InitializeFD", StringMarshalling = StringMarshalling.Utf8)]
    public static partial uint InitializeFd(ushort channel, string bitrate);

    [LibraryImport(DllName, EntryPoint = "CAN_GetValue")]
    public static partial uint GetValue(ushort channel, byte parameter, out uint value, uint bufferLength);

    [LibraryImport(DllName, EntryPoint = "CAN_GetValue")]
    public static unsafe partial uint GetValueBytes(ushort channel, byte parameter, byte* value, uint bufferLength);

    [LibraryImport(DllName, EntryPoint = "CAN_Uninitialize")]
    public static partial uint Uninitialize(ushort channel);

    [LibraryImport(DllName, EntryPoint = "CAN_Reset")]
    public static partial uint Reset(ushort channel);

    [LibraryImport(DllName, EntryPoint = "CAN_GetStatus")]
    public static partial uint GetStatus(ushort channel);

    [LibraryImport(DllName, EntryPoint = "CAN_Read")]
    public static partial uint Read(ushort channel, out TPCANMsg message, out TPCANTimestamp timestamp);

    [LibraryImport(DllName, EntryPoint = "CAN_ReadFD")]
    public static partial uint ReadFd(ushort channel, out TPCANMsgFD message, out ulong timestampUs);

    [LibraryImport(DllName, EntryPoint = "CAN_Write")]
    public static partial uint Write(ushort channel, ref TPCANMsg message);

    [LibraryImport(DllName, EntryPoint = "CAN_WriteFD")]
    public static partial uint WriteFd(ushort channel, ref TPCANMsgFD message);
}
