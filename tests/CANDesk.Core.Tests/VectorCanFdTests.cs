using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using CANDesk.Hal;
using CANDesk.Hal.Vector;
using Xunit;

namespace CANDesk.Core.Tests;

[SupportedOSPlatform("windows")]
public sealed class VectorCanFdTests
{
    [Fact]
    public void NativeLayouts_MatchVectorDriverLibrary()
    {
        Assert.Equal(40, Marshal.SizeOf<VectorXlNative.XLcanFdConf>());
        Assert.Equal(33, Marshal.OffsetOf<VectorXlNative.XLcanFdConf>(nameof(VectorXlNative.XLcanFdConf.Options)).ToInt32());
        Assert.Equal(88, Marshal.SizeOf<VectorXlNative.XLcanTxEvent>());
        Assert.Equal(8, Marshal.OffsetOf<VectorXlNative.XLcanTxEvent>(nameof(VectorXlNative.XLcanTxEvent.CanId)).ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<VectorXlNative.XLcanTxEvent>(nameof(VectorXlNative.XLcanTxEvent.Dlc)).ToInt32());
        Assert.Equal(24, Marshal.OffsetOf<VectorXlNative.XLcanTxEvent>(nameof(VectorXlNative.XLcanTxEvent.Data)).ToInt32());
        Assert.Equal(128, Marshal.SizeOf<VectorXlNative.XLcanRxEvent>());
        Assert.Equal(24, Marshal.OffsetOf<VectorXlNative.XLcanRxEvent>(nameof(VectorXlNative.XLcanRxEvent.TimeStamp)).ToInt32());
        Assert.Equal(32, Marshal.OffsetOf<VectorXlNative.XLcanRxEvent>(nameof(VectorXlNative.XLcanRxEvent.CanId)).ToInt32());
        Assert.Equal(58, Marshal.OffsetOf<VectorXlNative.XLcanRxEvent>(nameof(VectorXlNative.XLcanRxEvent.Dlc)).ToInt32());
        Assert.Equal(64, Marshal.OffsetOf<VectorXlNative.XLcanRxEvent>(nameof(VectorXlNative.XLcanRxEvent.Data)).ToInt32());
        Assert.Equal(227, Marshal.SizeOf<VectorXlNative.XLchannelConfig>());
        Assert.Equal(48 + 64 * 227, Marshal.SizeOf<VectorXlNative.XLdriverConfig>());
        Assert.Equal(48, Marshal.OffsetOf<VectorXlNative.XLdriverConfig>(nameof(VectorXlNative.XLdriverConfig.Channels)).ToInt32());
        Assert.Equal(48, Marshal.SizeOf<VectorXlNative.XLevent>());
    }

    [Fact]
    public unsafe void Discovery_UsesChannelIsoCapabilityIncludingLastSlot()
    {
        // Populate native bytes to check the packed channel stride and field offsets as well.
        var config = new VectorXlNative.XLdriverConfig { ChannelCount = 100 };
        var bytes = new Span<byte>(&config, sizeof(VectorXlNative.XLdriverConfig));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes[(48 + 63 * 227 + 42)..], 1UL << 63);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes[(48 + 63 * 227 + 50)..], 0x8000_0000);
        config.Channels[0] = new() { ChannelMask = 1, ChannelCapabilities = 0x2000_0000 }; // Bosch only
        config.Channels[1] = new() { ChannelMask = 2, ChannelCapabilities = 0 };

        Assert.True(VectorXlNative.SupportsIsoCanFd(config, 1UL << 63));
        Assert.False(VectorXlNative.SupportsIsoCanFd(config, 1));
        Assert.False(VectorXlNative.SupportsIsoCanFd(config, 2));
        Assert.False(VectorXlNative.SupportsIsoCanFd(config, 4));
    }

    [Theory]
    [InlineData("VN1630A", 123456u)]
    [InlineData("VN1640A", 564636u)]
    public unsafe void Discovery_ListsAllCanChannelsWithoutApplicationMappings(string model, uint serial)
    {
        var config = new VectorXlNative.XLdriverConfig { ChannelCount = 6 };
        for (var index = 0; index < 4; index++)
        {
            var channel = new VectorXlNative.XLchannelConfig
            {
                ChannelMask = 1UL << index, ChannelCapabilities = 0x8000_0000,
                ChannelBusCapabilities = 0x0001_0001, HardwareChannel = (byte)index, SerialNumber = serial
            };
            System.Text.Encoding.ASCII.GetBytes($"{model} Channel {index + 1}").CopyTo(new Span<byte>(channel.Name, 32));
            config.Channels[index] = channel;
        }
        config.Channels[4] = new() { ChannelMask = 16, ChannelBusCapabilities = 0x0040_0040 }; // Sync, not CAN
        config.Channels[5] = new() { ChannelMask = 32, ChannelBusCapabilities = 1 }; // CAN-compatible, unavailable transceiver

        var descriptors = VectorCanDeviceFactory.DescribeChannels(config);
        Assert.Equal(4, descriptors.Count);
        for (var index = 0; index < 4; index++)
        {
            var descriptor = descriptors[index];
            Assert.Equal((1UL << index).ToString("X"), descriptor.DeviceId);
            Assert.Equal($"{model} Channel {index + 1} (S/N {serial})", descriptor.DisplayName);
            Assert.Equal($"{model} Channel {index + 1}", Assert.Single(descriptor.Channels));
            Assert.True(descriptor.SupportsCanFd);
            Assert.Equal(80_000_000, descriptor.ClockFrequencyHz);
        }
    }

    [Fact]
    public void Discovery_IncludesLastDriverSlotAndClassicOnlyChannels()
    {
        var config = new VectorXlNative.XLdriverConfig { ChannelCount = 100 };
        config.Channels[0] = new() { ChannelMask = 0, ChannelBusCapabilities = 0x0001_0000 };
        config.Channels[63] = new()
        {
            ChannelMask = 1UL << 63, HardwareChannel = 3, ChannelBusCapabilities = 0x0001_0000,
            ChannelCapabilities = 0x2000_0000, SerialNumber = 987654
        };
        var descriptor = Assert.Single(VectorCanDeviceFactory.DescribeChannels(config));
        Assert.Equal("8000000000000000", descriptor.DeviceId);
        Assert.Equal("Vector CAN Channel 4 (S/N 987654)", descriptor.DisplayName);
        Assert.False(descriptor.SupportsCanFd);
        Assert.Null(descriptor.ClockFrequencyHz);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(8, 8)]
    [InlineData(9, 9)]
    [InlineData(12, 9)]
    [InlineData(16, 10)]
    [InlineData(20, 11)]
    [InlineData(24, 12)]
    [InlineData(25, 13)]
    [InlineData(32, 13)]
    [InlineData(48, 14)]
    [InlineData(49, 15)]
    [InlineData(64, 15)]
    public unsafe void FdFrame_MapsPayloadDlcFlagsPaddingAndNanosecondTimestamp(int length, byte dlc)
    {
        var flags = CanFrameFlags.Fd | CanFrameFlags.BitRateSwitch | CanFrameFlags.Extended;
        var payload = Enumerable.Range(0, length).Select(i => (byte)(i + 1)).ToArray();
        var tx = VectorCanDevice.ToFdMessage(CanFrame.Create(0x18DAF110, payload, flags));
        var rx = new VectorXlNative.XLcanRxEvent
        {
            Tag = 0x0400, CanId = tx.CanId, MsgFlags = tx.MsgFlags, Dlc = tx.Dlc,
            TimeStamp = 4_294_967_321_789
        };
        new ReadOnlySpan<byte>(tx.Data, 64).CopyTo(new Span<byte>(rx.Data, 64));
        var timeBase = new DateTime(2026, 11, 18, 0, 0, 0, DateTimeKind.Utc);
        var received = VectorCanDevice.ToCanFrame(rx, timeBase);
        var wireLength = dlc switch { <= 8 => dlc, 9 => 12, 10 => 16, 11 => 20, 12 => 24, 13 => 32, 14 => 48, _ => 64 };

        Assert.Equal((ushort)0x0440, tx.Tag);
        Assert.Equal(0x98DAF110u, tx.CanId);
        Assert.Equal(3u, tx.MsgFlags);
        Assert.Equal(dlc, tx.Dlc);
        Assert.Equal(0x18DAF110u, received.Id);
        Assert.Equal(flags, received.Flags);
        Assert.Equal(wireLength, received.PayloadLength);
        Assert.Equal(payload, received.PayloadSpan[..length].ToArray());
        Assert.All(received.PayloadSpan[length..].ToArray(), value => Assert.Equal((byte)0, value));
        Assert.Equal(rx.TimeStamp / 1000, received.TimestampUs);
        Assert.Equal(timeBase.AddTicks((long)(rx.TimeStamp / 100)), received.SystemTime);
    }

    [Theory]
    [InlineData(CanFrameFlags.None, 0)]
    [InlineData(CanFrameFlags.RemoteTransmissionRequest, 16)]
    [InlineData(CanFrameFlags.Fd, 1)]
    public unsafe void FdPort_CanSendClassicRtrAndFdWithoutBrs(CanFrameFlags flags, uint nativeFlags)
    {
        var frame = CanFrame.Create(0x123, [1, 2, 3, 4], flags);
        var tx = VectorCanDevice.ToFdMessage(frame);
        var rx = new VectorXlNative.XLcanRxEvent { CanId = tx.CanId, MsgFlags = tx.MsgFlags, Dlc = tx.Dlc };
        new ReadOnlySpan<byte>(tx.Data, 64).CopyTo(new Span<byte>(rx.Data, 64));
        Assert.Equal(nativeFlags, tx.MsgFlags);
        Assert.Equal(frame, VectorCanDevice.ToCanFrame(rx, DateTime.UtcNow));
    }

    [Theory]
    [InlineData(9, CanFrameFlags.None, 0x123)]
    [InlineData(8, CanFrameFlags.BitRateSwitch, 0x123)]
    [InlineData(8, CanFrameFlags.Fd | CanFrameFlags.RemoteTransmissionRequest, 0x123)]
    [InlineData(8, CanFrameFlags.ErrorFrame, 0x123)]
    [InlineData(8, CanFrameFlags.Fd, 0x800)]
    [InlineData(8, CanFrameFlags.Fd | CanFrameFlags.Extended, 0x20000000)]
    public void Transmit_RejectsInvalidFrame(int length, CanFrameFlags flags, uint id)
    {
        var frame = CanFrame.Create(id, new byte[length], flags);
        Assert.Throws<ArgumentException>(() => VectorCanDevice.ToFdMessage(frame));
        Assert.Throws<ArgumentException>(() => VectorCanDevice.ToClassicMessage(frame));
    }

    [Fact]
    public void ClassicTransmit_RejectsFdAndPreservesClassicLayout()
    {
        Assert.Throws<ArgumentException>(() => VectorCanDevice.ToClassicMessage(CanFrame.Create(0x123, [1], CanFrameFlags.Fd)));
        var message = VectorCanDevice.ToClassicMessage(CanFrame.Create(0x18DAF110, [1, 2, 3],
            CanFrameFlags.Extended | CanFrameFlags.RemoteTransmissionRequest));
        var (id, flags, dlc, data) = VectorXlNative.ReadCanMsg(message);
        Assert.Equal((byte)10, message.Tag);
        Assert.Equal(0x98DAF110u, id);
        Assert.Equal((ushort)0x10, flags);
        Assert.Equal((ushort)3, dlc);
        Assert.Equal(new byte[] { 1, 2, 3, 0, 0, 0, 0, 0 }, data);
    }

    [Fact]
    public void Receive_RejectsInvalidDlcAndMapsErrorFlag()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => VectorCanDevice.ToCanFrame(new() { Dlc = 16 }, DateTime.UtcNow));
        Assert.Equal(CanFrameFlags.ErrorFrame,
            VectorCanDevice.ToCanFrame(new() { MsgFlags = 0x200 }, DateTime.UtcNow).Flags);
    }

    [Fact]
    public void FdTiming_RepresentsSupportedPresetsWithinDriverTolerance()
    {
        var table = new BitrateTableProvider();
        foreach (var nominal in table.GetNominalPresets())
        foreach (var data in table.GetFdDataPhasePresets().Where(data => data.BitrateKbps >= nominal.BitrateKbps))
        {
            var config = new CanBusConfig((uint)nominal.BitrateKbps * 1000, (uint)data.BitrateKbps * 1000,
                nominal.SamplePointPercent, data.SamplePointPercent);
            var native = VectorBitTiming.ToFdConfiguration(config);
            Assert.Equal((uint)nominal.BitrateKbps * 1000, native.ArbitrationBitRate);
            Assert.Equal((uint)data.BitrateKbps * 1000, native.DataBitRate);
            Assert.Equal((byte)0, native.Options); // ISO CAN FD
            foreach (var (rate, tseg1, tseg2, sjw, setting) in new[]
            {
                (native.ArbitrationBitRate, native.Tseg1Abr, native.Tseg2Abr, native.SjwAbr, nominal),
                (native.DataBitRate, native.Tseg1Dbr, native.Tseg2Dbr, native.SjwDbr, data)
            })
            {
                Assert.InRange(tseg1, 2u, 254u);
                Assert.InRange(tseg2, 2u, 254u);
                Assert.InRange(sjw, 1u, tseg2);
                var totalTq = 1 + tseg1 + tseg2;
                var prescaler = Math.Round(80_000_000d / (rate * totalTq));
                Assert.True(prescaler >= 1);
                var actualRate = 80_000_000d / (prescaler * totalTq);
                Assert.InRange(Math.Abs(actualRate / rate - 1), 0, 1d / 256);
                Assert.InRange(Math.Abs(100d * (1 + tseg1) / totalTq - setting.SamplePointPercent), 0, 1);
            }
        }
    }

    [Fact]
    public void ConnectionSettings_RawPreviewUsesVectorCompatibleSegments()
    {
        var viewModel = new CANDesk.App.DeviceConnectionViewModel(new BitrateTableProvider(), new BitTimingCalculator(),
            new CANDesk.App.DeviceConnectionService([])) { SelectedVendor = "Vector", IsFdMode = true };
        Assert.True(int.Parse(viewModel.NominalTseg2) >= 2);
        viewModel.UseRawNominalTiming = true;
        viewModel.UseRawDataTiming = true;
        var configuration = viewModel.BuildConfiguration();
        var native = VectorBitTiming.ToFdConfiguration(configuration);
        Assert.Equal(500_000u, native.ArbitrationBitRate);
        Assert.Equal(2_000_000u, native.DataBitRate);
        Assert.Equal((uint)int.Parse(viewModel.NominalTseg1), native.Tseg1Abr);
        Assert.Equal((uint)int.Parse(viewModel.NominalTseg2), native.Tseg2Abr);
    }

    [Fact]
    public void FdTiming_UsesRawSegmentsAndDerivesRatesFromPrescalers()
    {
        var config = new CanBusConfig(250000, 1000000)
        {
            NominalTiming = new() { InputMode = BitTimingInputMode.RawSegments, RawSegments = new(2, 63, 16, 16) },
            DataTiming = new() { InputMode = BitTimingInputMode.RawSegments, RawSegments = new(2, 15, 4, 4) }
        };
        var native = VectorBitTiming.ToFdConfiguration(config);
        Assert.Equal(500_000u, native.ArbitrationBitRate);
        Assert.Equal(63u, native.Tseg1Abr);
        Assert.Equal(16u, native.Tseg2Abr);
        Assert.Equal(16u, native.SjwAbr);
        Assert.Equal(2_000_000u, native.DataBitRate);
        Assert.Equal(15u, native.Tseg1Dbr);
        Assert.Equal(4u, native.Tseg2Dbr);
        Assert.Equal(4u, native.SjwDbr);
    }

    [Theory]
    [InlineData(1, 1, 4, 4)]
    [InlineData(1, 127, 4, 4)]
    [InlineData(1, 15, 1, 1)]
    [InlineData(1, 15, 127, 4)]
    [InlineData(1, 15, 65, 65)]
    public void FdTiming_RejectsInvalidDataSegments(int brp, int tseg1, int tseg2, int sjw)
    {
        var config = new CanBusConfig(500000, 2000000)
        {
            DataTiming = new() { InputMode = BitTimingInputMode.RawSegments, RawSegments = new(brp, tseg1, tseg2, sjw) }
        };
        Assert.Throws<ArgumentOutOfRangeException>(() => VectorBitTiming.ToFdConfiguration(config));
    }

    [Fact]
    public void FdTiming_RejectsUnsupportedClockAndRateCombinations()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => VectorBitTiming.ToFdConfiguration(new(500000, 2000000) { ClockFrequencyHz = 40_000_000 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => VectorBitTiming.ToFdConfiguration(new(1000000, 500000)));
        Assert.Throws<ArgumentOutOfRangeException>(() => VectorBitTiming.ToFdConfiguration(new(10000, 20000)));
        Assert.Throws<ArgumentOutOfRangeException>(() => VectorBitTiming.ToFdConfiguration(new(500000, 20000000)));
    }

    [Theory]
    [InlineData(2, 500000, 2000000, 13, true)]
    [InlineData(1, 500000, 2000000, 13, false)]
    [InlineData(6, 500000, 2000000, 13, false)]
    [InlineData(2, 250000, 2000000, 13, false)]
    [InlineData(2, 500000, 1000000, 13, false)]
    [InlineData(2, 500000, 2000000, 10, false)]
    public void SharedFdChannel_RequiresMatchingIsoModeRatesAndSamplePoints(byte mode, uint nominal, uint data, byte tseg1, bool matches)
    {
        var requested = new VectorXlNative.XLcanFdConf
        {
            ArbitrationBitRate = 500000, DataBitRate = 2000000, Tseg1Abr = 13, Tseg2Abr = 2, Tseg1Dbr = 15, Tseg2Dbr = 4
        };
        var active = new VectorXlNative.XLchannelConfig
        {
            BusType = 1, CanOpMode = mode, ArbitrationBitRate = nominal, DataBitRate = data,
            Tseg1Abr = tseg1, Tseg2Abr = 2, Tseg1Dbr = 15, Tseg2Dbr = 4
        };
        Assert.Equal(matches, VectorBitTiming.MatchesActiveConfiguration(active, requested));
    }
}
