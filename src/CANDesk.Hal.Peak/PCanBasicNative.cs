using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace CANDesk.Hal.Peak;

/// <summary>
/// Thin P/Invoke surface over PEAK-System's PCAN-Basic API (<c>PCANBasic.dll</c>), scoped to
/// what Classic CAN open/close/send/receive/reset/status needs. Field layouts follow PCAN-Basic's
/// public API documentation; verify against the PCANBasic.h shipped with the installed driver
/// version before first hardware run, since this was written without that header in hand.
/// CAN-FD (<c>CAN_InitializeFD</c>/<c>TPCANMsgFD</c>) is intentionally out of scope for v1, matching
/// Project_Architecture.md §11 ("1차 릴리스는 Classic CAN 우선").
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class PCanBasicNative
{
    private const string DllName = "PCANBasic.dll";

    // TPCANMsg.MSGTYPE bit flags.
    public const byte MessageStandard = 0x00;
    public const byte MessageRtr = 0x01;
    public const byte MessageExtended = 0x02;
    public const byte MessageErrorFrame = 0x40;
    public const byte MessageStatus = 0x80;

    // TPCANStatus bit flags (0 = OK; others can be OR'd together on CAN_GetStatus).
    public const uint StatusOk = 0x00000;
    public const uint StatusQueueReceiveEmpty = 0x00020;
    public const uint StatusBusLight = 0x00004;
    public const uint StatusBusHeavy = 0x00008;
    public const uint StatusBusOff = 0x00010;
    public const uint StatusBusPassive = 0x40000;

    // TPCANParameter/TPCANChannelCondition values are deliberately not declared here: enumeration
    // (PeakCanDeviceFactory) probes with CAN_Initialize/CAN_Uninitialize instead of CAN_GetValue,
    // because the exact PCAN_CHANNEL_CONDITION parameter id is the kind of detail this file's
    // remarks warn needs verifying against the real header — a wrong id silently reports every
    // channel as unavailable (confirmed against real PEAK hardware: this was the actual bug behind
    // "no PEAK channel was found" until the probe-based rewrite). Initialize/Uninitialize are the
    // same calls the real connect path uses, so there's nothing extra to get wrong here.

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct TPCANMsg
    {
        public uint Id;
        public byte MsgType;
        public byte Length;
        public fixed byte Data[8];
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

    [LibraryImport(DllName, EntryPoint = "CAN_Uninitialize")]
    public static partial uint Uninitialize(ushort channel);

    [LibraryImport(DllName, EntryPoint = "CAN_Reset")]
    public static partial uint Reset(ushort channel);

    [LibraryImport(DllName, EntryPoint = "CAN_GetStatus")]
    public static partial uint GetStatus(ushort channel);

    [LibraryImport(DllName, EntryPoint = "CAN_Read")]
    public static partial uint Read(ushort channel, out TPCANMsg message, out TPCANTimestamp timestamp);

    [LibraryImport(DllName, EntryPoint = "CAN_Write")]
    public static partial uint Write(ushort channel, ref TPCANMsg message);
}
