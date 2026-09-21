using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace CANDesk.Hal.Kvaser;

[SupportedOSPlatform("windows")]
internal static class KvaserCanlibNative
{
    private const string DllName = "canlib32.dll";

    public const int StatusOk = 0;
    public const int ErrorNoMessage = -2;

    public const int OpenAcceptVirtual = 0x20;

    public const int ChannelDataChannelName = 13;

    public const int DriverNormal = 4;

    public const uint MessageRtr = 0x0001;
    public const uint MessageExtended = 0x0004;
    public const uint MessageErrorFrame = 0x0020;

    public const int Bitrate1M = -1;
    public const int Bitrate500K = -2;
    public const int Bitrate250K = -3;
    public const int Bitrate125K = -4;
    public const int Bitrate100K = -5;
    public const int Bitrate62K = -6;
    public const int Bitrate50K = -7;
    public const int Bitrate83K = -8;
    public const int Bitrate10K = -9;

    [DllImport(DllName, EntryPoint = "canInitializeLibrary")]
    public static extern void InitializeLibrary();

    [DllImport(DllName, EntryPoint = "canGetNumberOfChannels")]
    public static extern int GetNumberOfChannels(out int channelCount);

    [DllImport(DllName, EntryPoint = "canGetChannelData", CharSet = CharSet.Ansi)]
    public static extern int GetChannelData(int channel, int item, StringBuilder buffer, nuint bufferSize);

    [DllImport(DllName, EntryPoint = "canOpenChannel")]
    public static extern int OpenChannel(int channel, int flags);

    [DllImport(DllName, EntryPoint = "canSetBusParams")]
    public static extern int SetBusParams(int handle, int freq, uint tseg1, uint tseg2, uint sjw, uint noSamp, uint syncMode);

    [DllImport(DllName, EntryPoint = "canTranslateBaud")]
    public static extern int TranslateBaud(ref int freq, out uint tseg1, out uint tseg2, out uint sjw, out uint noSamp, out uint syncMode);

    [DllImport(DllName, EntryPoint = "canSetBusOutputControl")]
    public static extern int SetBusOutputControl(int handle, uint driverType);

    [DllImport(DllName, EntryPoint = "canBusOn")]
    public static extern int BusOn(int handle);

    [DllImport(DllName, EntryPoint = "canBusOff")]
    public static extern int BusOff(int handle);

    [DllImport(DllName, EntryPoint = "canClose")]
    public static extern int Close(int handle);

    [DllImport(DllName, EntryPoint = "canReadWait")]
    public static extern int ReadWait(int handle, out int id, [Out] byte[] data, out uint dlc, out uint flags, out long timestampMs, uint timeoutMs);

    [DllImport(DllName, EntryPoint = "canWrite")]
    public static extern int Write(int handle, int id, byte[] data, uint dlc, uint flags);

    [DllImport(DllName, EntryPoint = "canWriteSync")]
    public static extern int WriteSync(int handle, ulong timeoutMs);
}
