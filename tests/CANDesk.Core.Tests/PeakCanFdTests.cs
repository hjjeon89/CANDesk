using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using CANDesk.Hal;
using CANDesk.Hal.Peak;
using Xunit;

namespace CANDesk.Core.Tests;

[SupportedOSPlatform("windows")]
public sealed class PeakCanFdTests
{
    [Fact]
    public void NativeLayout_MatchesPcanHeaders()
    {
        Assert.Equal(72, Marshal.SizeOf<PCanBasicNative.TPCANMsgFD>());
        Assert.Equal(4, Marshal.OffsetOf<PCanBasicNative.TPCANMsgFD>(nameof(PCanBasicNative.TPCANMsgFD.MsgType)).ToInt32());
        Assert.Equal(5, Marshal.OffsetOf<PCanBasicNative.TPCANMsgFD>(nameof(PCanBasicNative.TPCANMsgFD.Dlc)).ToInt32());
        Assert.Equal(6, Marshal.OffsetOf<PCanBasicNative.TPCANMsgFD>(nameof(PCanBasicNative.TPCANMsgFD.Data)).ToInt32());
        Assert.Equal(16, Marshal.SizeOf<PCanBasicNative.TPCANMsg>());
        Assert.Equal(8, Marshal.SizeOf<PCanBasicNative.TPCANTimestamp>());
        Assert.Equal(52, Marshal.SizeOf<PCanBasicNative.TPCANChannelInformation>());
        Assert.Equal(48, Marshal.OffsetOf<PCanBasicNative.TPCANChannelInformation>(nameof(PCanBasicNative.TPCANChannelInformation.ChannelCondition)).ToInt32());
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(8, 8)]
    [InlineData(12, 9)]
    [InlineData(16, 10)]
    [InlineData(20, 11)]
    [InlineData(24, 12)]
    [InlineData(32, 13)]
    [InlineData(48, 14)]
    [InlineData(64, 15)]
    public void FdFrame_RoundTripsPayloadDlcFlagsAndTimestamp(int length, byte dlc)
    {
        var payload = Enumerable.Range(0, length).Select(index => (byte)(index + 1)).ToArray();
        var flags = CanFrameFlags.Fd | CanFrameFlags.BitRateSwitch | CanFrameFlags.Extended;
        var frame = CanFrame.Create(0x18DAF110, payload, flags);
        var timeBase = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
        const ulong timestampUs = 4_294_967_321;

        var native = PeakCanDevice.ToFdMessage(frame);
        var received = PeakCanDevice.ToCanFrame(native, timeBase, timestampUs);

        Assert.Equal(dlc, native.Dlc);
        Assert.Equal((byte)0x0E, native.MsgType);
        Assert.Equal(frame.Id, received.Id);
        Assert.Equal(payload, received.PayloadSpan.ToArray());
        Assert.Equal(flags, received.Flags);
        Assert.Equal(timestampUs, received.TimestampUs);
        Assert.Equal(timeBase.AddTicks((long)timestampUs * 10), received.SystemTime);
    }

    [Theory]
    [InlineData(9, 12)]
    [InlineData(13, 16)]
    [InlineData(25, 32)]
    [InlineData(49, 64)]
    public void FdFrame_PadsToWireLengthWithZeros(int length, int wireLength)
    {
        var payload = Enumerable.Repeat((byte)0xA5, length).ToArray();
        var native = PeakCanDevice.ToFdMessage(CanFrame.Create(0x123, payload, CanFrameFlags.Fd));
        var received = PeakCanDevice.ToCanFrame(native, DateTime.UtcNow, 0);

        Assert.Equal((byte)PCanBasicNative.MessageFd, native.MsgType);
        Assert.Equal(wireLength, received.PayloadLength);
        Assert.Equal(payload, received.PayloadSpan[..length].ToArray());
        Assert.All(received.PayloadSpan[length..].ToArray(), value => Assert.Equal((byte)0, value));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClassicFrame_UsesFdApiWithoutSettingFdOrBrs(bool rtr)
    {
        var flags = rtr ? CanFrameFlags.RemoteTransmissionRequest : CanFrameFlags.None;
        var frame = CanFrame.Create(0x123, [1, 2, 3, 4], flags);
        var native = PeakCanDevice.ToFdMessage(frame);
        var received = PeakCanDevice.ToCanFrame(native, DateTime.UtcNow, 0);

        Assert.Equal(rtr ? (byte)1 : (byte)0, native.MsgType);
        Assert.Equal(frame.Flags, received.Flags);
        Assert.Equal(frame.PayloadSpan.ToArray(), received.PayloadSpan.ToArray());
    }

    [Theory]
    [InlineData(32, CanFrameFlags.None)]
    [InlineData(8, CanFrameFlags.BitRateSwitch)]
    [InlineData(8, CanFrameFlags.Fd | CanFrameFlags.RemoteTransmissionRequest)]
    public void ToFdMessage_RejectsInvalidFrameCombinations(int length, CanFrameFlags flags)
    {
        var frame = CanFrame.Create(0x123, new byte[length], flags);
        Assert.Throws<ArgumentException>(() => PeakCanDevice.ToFdMessage(frame));
    }

    [Fact]
    public void ToCanFrame_RejectsInvalidDlc()
    {
        var native = new PCanBasicNative.TPCANMsgFD { Dlc = 16, MsgType = PCanBasicNative.MessageFd };
        Assert.Throws<ArgumentOutOfRangeException>(() => PeakCanDevice.ToCanFrame(native, DateTime.UtcNow, 0));
    }

    [Fact]
    public void FdTiming_PresetsDescribeRequestedNominalAndDataRates()
    {
        var table = new BitrateTableProvider();
        foreach (var nominal in table.GetNominalPresets())
        foreach (var data in table.GetFdDataPhasePresets())
        {
            var configuration = new CanBusConfig((uint)nominal.BitrateKbps * 1000, (uint)data.BitrateKbps * 1000,
                nominal.SamplePointPercent, data.SamplePointPercent);
            var values = PeakBitTiming.ToFdString(configuration).Split(',')
                .Select(pair => pair.Split('=')).ToDictionary(pair => pair[0], pair => int.Parse(pair[1]));
            Assert.Equal(80_000_000, values["f_clock"]);
            foreach (var (prefix, setting) in new[] { ("nom", nominal), ("data", data) })
            {
                var bitrate = values["f_clock"] / (double)(values[$"{prefix}_brp"]
                    * (1 + values[$"{prefix}_tseg1"] + values[$"{prefix}_tseg2"]));
                Assert.InRange(Math.Abs(bitrate / (setting.BitrateKbps * 1000) - 1), 0, 0.005);
                var samplePoint = 100d * (1 + values[$"{prefix}_tseg1"])
                    / (1 + values[$"{prefix}_tseg1"] + values[$"{prefix}_tseg2"]);
                Assert.InRange(Math.Abs(samplePoint - setting.SamplePointPercent), 0, 1);
            }
        }
    }

    [Fact]
    public void FdTiming_PreservesExplicitRawSegments()
    {
        var configuration = new CanBusConfig(500000, 2000000)
        {
            NominalTiming = new BitTimingConfig { InputMode = BitTimingInputMode.RawSegments, RawSegments = new(2, 63, 16, 16) },
            DataTiming = new BitTimingConfig { InputMode = BitTimingInputMode.RawSegments, RawSegments = new(2, 15, 4, 4) }
        };

        Assert.Equal("f_clock=80000000,nom_brp=2,nom_tseg1=63,nom_tseg2=16,nom_sjw=16,data_brp=2,data_tseg1=15,data_tseg2=4,data_sjw=4",
            PeakBitTiming.ToFdString(configuration));
    }

    [Theory]
    [InlineData(20_000_000)]
    [InlineData(24_000_000)]
    [InlineData(30_000_000)]
    [InlineData(40_000_000)]
    [InlineData(60_000_000)]
    [InlineData(80_000_000)]
    public void FdTiming_UsesPcanViewClockAndPreservesRawRegisters(int clock)
    {
        var configuration = new CanBusConfig(500000, 1000000)
        {
            ClockFrequencyHz = clock,
            NominalTiming = new BitTimingConfig { InputMode = BitTimingInputMode.RawSegments, RawSegments = new(1, 63, 16, 16) },
            DataTiming = new BitTimingConfig { InputMode = BitTimingInputMode.RawSegments, RawSegments = new(1, 31, 8, 8) }
        };
        Assert.Equal($"f_clock={clock},nom_brp=1,nom_tseg1=63,nom_tseg2=16,nom_sjw=16,data_brp=1,data_tseg1=31,data_tseg2=8,data_sjw=8",
            PeakBitTiming.ToFdString(configuration));
    }

    [Fact]
    public void FdTiming_RejectsUnsupportedClock()
    {
        var configuration = new CanBusConfig(500000, 1000000) { ClockFrequencyHz = 16_000_000 };
        Assert.Throws<ArgumentOutOfRangeException>(() => PeakBitTiming.ToFdString(configuration));
    }

    [Theory]
    [InlineData(1025, 15, 4, 4)]
    [InlineData(1, 33, 4, 4)]
    [InlineData(1, 15, 17, 4)]
    [InlineData(1, 15, 4, 5)]
    public void FdTiming_RejectsInvalidDataSegments(int brp, int tseg1, int tseg2, int sjw)
    {
        var configuration = new CanBusConfig(500000, 2000000)
        {
            DataTiming = new BitTimingConfig
            {
                InputMode = BitTimingInputMode.RawSegments,
                RawSegments = new(brp, tseg1, tseg2, sjw)
            }
        };
        Assert.Throws<ArgumentOutOfRangeException>(() => PeakBitTiming.ToFdString(configuration));
    }

    [Fact]
    public void ClassicTiming_StillUsesExistingBtrPresets()
    {
        Assert.Equal((ushort)0x001C, PeakBitTiming.FromPreset(new(500, 87.5)));
        Assert.Equal((ushort)0x011C, PeakBitTiming.FromPreset(new(250, 87.5)));
        Assert.Equal((ushort)0x011C, PeakBitTiming.FromRawSegments(new(2, 13, 2, 1)));
    }
}
