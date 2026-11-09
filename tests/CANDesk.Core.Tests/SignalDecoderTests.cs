using CANDesk.Core.Dispatch;
using CANDesk.Core.MessageDb;
using CANDesk.Hal;
using Xunit;

namespace CANDesk.Core.Tests;

public sealed class SignalDecoderTests
{
    [Fact]
    public void Decode_IntelSignal_ReadsLeastSignificantBitFirst()
    {
        var signal = new DbcSignal("EngineSpeed", 0, 16, ByteOrder.Intel, false, 0.25, Unit: "rpm");
        var decoder = CreateDecoder(0x100, signal);

        var decoded = Assert.Single(decoder.Decode(CanFrame.Create(0x100, [0x34, 0x12])));

        Assert.Equal(0x1234, decoded.RawValue);
        Assert.Equal(0x1234 * 0.25, decoded.PhysicalValue);
        Assert.Equal("rpm", decoded.Unit);
    }

    [Fact]
    public void Decode_MotorolaSignal_FollowsDbcSawtoothBitNumbering()
    {
        // Start bit 7 consumes byte 0 from MSB to LSB, then byte 1 from MSB to LSB.
        var signal = new DbcSignal("MotorolaValue", 7, 16, ByteOrder.Motorola, false);
        var decoder = CreateDecoder(0x101, signal);

        var decoded = Assert.Single(decoder.Decode(CanFrame.Create(0x101, [0x12, 0x34])));

        Assert.Equal(0x1234, decoded.RawValue);
    }

    [Fact]
    public void Decode_MultiplexedSignal_EmitsOnlyTheActiveMuxValue()
    {
        var mux = new DbcSignal("Mode", 0, 4, ByteOrder.Intel, false, MultiplexorRole: MultiplexorRole.Switch);
        var active = new DbcSignal("ActiveValue", 4, 4, ByteOrder.Intel, false, MultiplexorRole: MultiplexorRole.Value, MultiplexValue: 2);
        var inactive = new DbcSignal("InactiveValue", 4, 4, ByteOrder.Intel, false, MultiplexorRole: MultiplexorRole.Value, MultiplexValue: 3);
        var decoder = CreateDecoder(0x102, mux, active, inactive);

        var decoded = decoder.Decode(CanFrame.Create(0x102, [0x22]));

        Assert.Collection(decoded,
            value => { Assert.Equal("Mode", value.SignalName); Assert.Equal(2, value.RawValue); },
            value => { Assert.Equal("ActiveValue", value.SignalName); Assert.Equal(2, value.RawValue); });
    }

    [Theory]
    [InlineData(3, 12, false, 0x0A, 0xBC, 0xABC)]
    [InlineData(7, 16, true, 0xFF, 0x9C, -100)]
    public void Decode_MotorolaUnalignedAndSignedSignals(int startBit, int length, bool signed, byte first, byte second, int raw)
    {
        var signal = new DbcSignal("Value", startBit, length, ByteOrder.Motorola, signed, 0.5, -10);
        var decoded = Assert.Single(CreateDecoder(0x101, signal).Decode(CanFrame.Create(0x101, [first, second])));

        Assert.Equal(raw, decoded.RawValue);
        Assert.Equal(raw * 0.5 - 10, decoded.PhysicalValue);
        var payload = new byte[2];
        SignalEncoder.Encode(payload, signal, decoded.PhysicalValue);
        Assert.Equal(new[] { first, second }, payload);
    }

    [Theory]
    [InlineData(ByteOrder.Intel, 8)]
    [InlineData(ByteOrder.Motorola, 15)]
    public void Decode_ShortFrameSkipsIncompleteSignalAndKeepsValidSignals(ByteOrder order, int startBit)
    {
        var valid = new DbcSignal("Available", 7, 8, ByteOrder.Motorola, false);
        var incomplete = new DbcSignal("Incomplete", startBit, 16, order, false);
        var decoder = CreateDecoder(0x101, incomplete, valid);

        var decoded = Assert.Single(decoder.Decode(CanFrame.Create(0x101, [0x12, 0x34])));
        Assert.Equal("Available", decoded.SignalName);
        Assert.Equal(0x12, decoded.RawValue);
        Assert.Equal(2, decoder.Decode(CanFrame.Create(0x101, [0x12, 0x34, 0x56])).Count);
        Assert.Throws<InvalidOperationException>(() => SignalDecoder.DecodeValue(new byte[] { 0x12, 0x34 }, incomplete));
    }

    [Fact]
    public void Decode_MotorolaStartBitZeroDoesNotFitSixteenBitsInTwoBytes()
    {
        var signal = new DbcSignal("Value", 0, 16, ByteOrder.Motorola, false);
        var decoder = CreateDecoder(0x101, signal);

        Assert.Empty(decoder.Decode(CanFrame.Create(0x101, [0x01, 0x23])));
        Assert.Equal(0x91A2, Assert.Single(decoder.Decode(CanFrame.Create(0x101, [0x01, 0x23, 0x44]))).RawValue);
    }

    [Fact]
    public void Decode_MissingMultiplexorSkipsDependentSignalsButKeepsCommonSignals()
    {
        var mux = new DbcSignal("Mode", 15, 8, ByteOrder.Motorola, false, MultiplexorRole: MultiplexorRole.Switch);
        var common = new DbcSignal("Common", 7, 4, ByteOrder.Motorola, false);
        var dependent = new DbcSignal("Dependent", 3, 4, ByteOrder.Motorola, false,
            MultiplexorRole: MultiplexorRole.Value, MultiplexValue: 0);
        var decoder = CreateDecoder(0x101, mux, common, dependent);

        var decoded = Assert.Single(decoder.Decode(CanFrame.Create(0x101, [0xAB])));
        Assert.Equal("Common", decoded.SignalName);
        Assert.Equal(10, decoded.RawValue);
    }

    [Theory]
    [InlineData(CanFrameFlags.RemoteTransmissionRequest)]
    [InlineData(CanFrameFlags.ErrorFrame)]
    public void Decode_DoesNotInterpretRemoteOrErrorFramesAsSignalValues(CanFrameFlags flags)
    {
        var signal = new DbcSignal("Value", 7, 8, ByteOrder.Motorola, false);
        Assert.Empty(CreateDecoder(0x101, signal).Decode(CanFrame.Create(0x101, [0x12], flags)));
    }

    [Theory]
    [InlineData(-1, 8)]
    [InlineData(0, 0)]
    [InlineData(0, 65)]
    [InlineData(int.MaxValue, 64)]
    public void Decode_InvalidSignalLayoutDoesNotAbortOtherSignals(int startBit, int length)
    {
        var invalid = new DbcSignal("Invalid", startBit, length, ByteOrder.Motorola, false);
        var valid = new DbcSignal("Valid", 7, 8, ByteOrder.Motorola, false);
        Assert.Equal("Valid", Assert.Single(CreateDecoder(0x101, invalid, valid)
            .Decode(CanFrame.Create(0x101, [0x12]))).SignalName);
    }

    private static SignalDecoder CreateDecoder(uint canId, params DbcSignal[] signals) =>
        new(new MessageDatabase([new DbcMessage(canId, "TestMessage", 8, signals)]));
}
