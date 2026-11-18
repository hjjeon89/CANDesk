using CANDesk.Hal;
using System.Runtime.Versioning;

namespace CANDesk.Hal.Vector;

public static class VectorBitTiming
{
    // XLcanFdConf requires an assumed 80 MHz clock; the driver derives the prescaler.
    public const int ClockHz = 80_000_000;

    [SupportedOSPlatform("windows")]
    internal static bool MatchesActiveConfiguration(in VectorXlNative.XLchannelConfig channel, in VectorXlNative.XLcanFdConf requested) =>
        channel.BusType == VectorXlNative.BusTypeCan && (channel.CanOpMode & 0x06) == 0x02
        && Math.Abs((double)channel.ArbitrationBitRate / requested.ArbitrationBitRate - 1) < 1d / 256
        && Math.Abs((double)channel.DataBitRate / requested.DataBitRate - 1) < 1d / 256
        && Math.Abs(SamplePoint(channel.Tseg1Abr, channel.Tseg2Abr) - SamplePoint(requested.Tseg1Abr, requested.Tseg2Abr)) < 2
        && Math.Abs(SamplePoint(channel.Tseg1Dbr, channel.Tseg2Dbr) - SamplePoint(requested.Tseg1Dbr, requested.Tseg2Dbr)) < 2;

    private static double SamplePoint(uint tseg1, uint tseg2) => 100d * (1 + tseg1) / (1 + tseg1 + tseg2);

    [SupportedOSPlatform("windows")]
    internal static VectorXlNative.XLcanFdConf ToFdConfiguration(CanBusConfig config)
    {
        config.Validate();
        if (!config.IsCanFd) throw new ArgumentException("CAN FD configuration is required.", nameof(config));
        if (config.ClockFrequencyHz != ClockHz)
            throw new ArgumentOutOfRangeException(nameof(config), "Vector CAN FD timing requires an 80 MHz clock.");

        var (nominalRate, nominal) = GetTiming(config.NominalTiming, config.Nominal, dataPhase: false);
        var (dataRate, data) = GetTiming(config.DataTiming, config.Data!, dataPhase: true);
        if (dataRate < Math.Max(nominalRate, 25_000u))
            throw new ArgumentOutOfRangeException(nameof(config), "Vector data bitrate must be at least the nominal bitrate and 25 kbps.");

        return new()
        {
            ArbitrationBitRate = nominalRate, SjwAbr = (uint)nominal.Sjw,
            Tseg1Abr = (uint)nominal.Tseg1, Tseg2Abr = (uint)nominal.Tseg2,
            DataBitRate = dataRate, SjwDbr = (uint)data.Sjw,
            Tseg1Dbr = (uint)data.Tseg1, Tseg2Dbr = (uint)data.Tseg2
        };
    }

    private static (uint Bitrate, RawBitTimingSegments Segments) GetTiming(
        BitTimingConfig? timing, BitTimingSetting setting, bool dataPhase)
    {
        var maxSegment = dataPhase ? 126 : 254;
        if (timing?.InputMode == BitTimingInputMode.RawSegments)
        {
            var raw = timing.RawSegments!;
            raw.Validate();
            if (raw.Tseg1 < 2 || raw.Tseg1 > maxSegment || raw.Tseg2 < 2 || raw.Tseg2 > maxSegment
                || raw.Sjw > (dataPhase ? 64 : 128))
                throw new ArgumentOutOfRangeException(nameof(timing), "Timing exceeds the Vector CAN FD segment limits.");
            var rate = ClockHz / (raw.Prescaler * (1d + raw.Tseg1 + raw.Tseg2));
            if (rate < 1) throw new ArgumentOutOfRangeException(nameof(timing), "Vector bitrate must be positive.");
            return (checked((uint)Math.Round(rate)), raw);
        }

        setting = timing?.Preset ?? setting;
        return (checked((uint)setting.BitrateKbps * 1_000u), Calculate(setting, dataPhase));
    }

    /// <summary>Calculates Vector-compatible segments for a preset and the required 80 MHz clock.</summary>
    public static RawBitTimingSegments Calculate(BitTimingSetting setting, bool dataPhase)
    {
        if (setting.BitrateKbps <= 0 || !double.IsFinite(setting.SamplePointPercent) || setting.SamplePointPercent is <= 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(setting));
        var maxSegment = dataPhase ? 126 : 254;
        var requestedRate = checked((uint)setting.BitrateKbps * 1_000u);
        RawBitTimingSegments? best = null;
        var bestError = double.MaxValue;
        for (var totalTq = 5; totalTq <= 1 + 2 * maxSegment; totalTq++)
        {
            var prescaler = Math.Round(ClockHz / ((double)requestedRate * totalTq));
            if (prescaler < 1 || prescaler > int.MaxValue) continue;
            var rate = ClockHz / (prescaler * totalTq);
            if (Math.Abs(rate / requestedRate - 1) >= 1d / 256) continue;
            var tseg1 = (int)Math.Round(totalTq * setting.SamplePointPercent / 100d) - 1;
            var tseg2 = totalTq - 1 - tseg1;
            if (tseg1 < 2 || tseg1 > maxSegment || tseg2 < 2 || tseg2 > maxSegment) continue;
            var error = Math.Abs(rate / requestedRate - 1)
                + Math.Abs(100d * (1 + tseg1) / totalTq - setting.SamplePointPercent) / 10_000;
            if (error >= bestError) continue;
            bestError = error;
            best = new((int)prescaler, tseg1, tseg2, Math.Min(4, tseg2));
        }

        if (best is null) throw new ArgumentOutOfRangeException(nameof(setting), "Timing cannot be represented by the Vector 80 MHz CAN FD clock.");
        return best;
    }
}
