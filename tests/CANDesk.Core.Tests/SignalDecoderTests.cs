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

    private static SignalDecoder CreateDecoder(uint canId, params DbcSignal[] signals) =>
        new(new MessageDatabase([new DbcMessage(canId, "TestMessage", 8, signals)]));
}
