using CANDesk.Hal;

namespace CANDesk.Core.MessageDb;

internal static class MessageDbBusConfiguration
{
    // A missing FD data rate stays unspecified; loading a DB must not invent a bus speed.
    public static CanBusConfig? Create(int nominalBitrate, string? busType, int dataBitrate = 0,
        double nominalSamplePoint = 87.5, double dataSamplePoint = 80)
    {
        if (nominalBitrate == 0 && dataBitrate == 0) return null;
        if (nominalBitrate <= 0 || nominalBitrate % 1000 != 0 || dataBitrate < 0 || dataBitrate % 1000 != 0)
            throw new FormatException("Database bitrates must be positive whole kbps values.");
        if (!double.IsFinite(nominalSamplePoint) || nominalSamplePoint is <= 0 or >= 100
            || (dataBitrate > 0 && (!double.IsFinite(dataSamplePoint) || dataSamplePoint is <= 0 or >= 100)))
            throw new FormatException("Database sample points must be between 0 and 100 percent.");

        return new CanBusConfig((uint)nominalBitrate, dataBitrate > 0 ? (uint)dataBitrate : null,
            nominalSamplePoint, dataSamplePoint,
            enableCanFd: busType?.Contains("FD", StringComparison.OrdinalIgnoreCase) == true);
    }
}
