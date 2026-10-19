using System.Text;
using CANDesk.Core.MessageDb;
using CANDesk.Hal;
using Xunit;

namespace CANDesk.Core.Tests;

public sealed class DbcParserTests
{
    [Fact]
    public async Task ParseAsync_ReadsMessageSignalsAndMultiplexingMarker()
    {
        const string dbc = """
            VERSION ""
            BO_ 256 EngineStatus: 8 Vector__XXX
             SG_ Mode M : 0|4@1+ (1,0) [0|15] "" Vector__XXX
             SG_ EngineSpeed m2 : 7|16@0+ (0.25,0) [0|16383.75] "rpm" Vector__XXX
            """;
        var parser = new DbcParser();
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes(dbc));

        var database = await parser.ParseAsync(input);

        Assert.Null(database.BusConfiguration);
        var message = Assert.Single(database.Messages);
        Assert.Equal(0x100u, message.CanId);
        Assert.Equal("EngineStatus", message.Name);
        Assert.Equal((byte)8, message.Dlc);
        Assert.Collection(message.Signals,
            signal => Assert.Equal(MultiplexorRole.Switch, signal.MultiplexorRole),
            signal =>
            {
                Assert.Equal(ByteOrder.Motorola, signal.ByteOrder);
                Assert.Equal(MultiplexorRole.Value, signal.MultiplexorRole);
                Assert.Equal(2, signal.MultiplexValue);
                Assert.Equal("rpm", signal.Unit);
            });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParseAsync_ReadsBusAttributesAndDefaults(bool defaultsOnly)
    {
        var dbc = """
            BA_DEF_DEF_ "Baudrate" 250000;
            BA_DEF_DEF_ "BusType" "CAN";
            BA_ "Baudrate" BO_ 291 125000;
            """;
        if (!defaultsOnly) dbc += "\n" + """
            BA_ "Baudrate" 500000;
            BA_ "BusType" "CAN FD";
            BA_ "DataBitrate" 1000000;
            BA_ "NominalSamplePoint" 80;
            BA_ "DataSamplePoint" 75;
            BA_DEF_DEF_ "Baudrate" 250000;
            """;
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes(dbc));
        var database = await new DbcParser().ParseAsync(input);
        var bus = Assert.IsType<CanBusConfig>(database.BusConfiguration);
        Assert.Equal(defaultsOnly ? CanBusMode.Classic : CanBusMode.Fd, bus.Mode);
        Assert.Equal(new BitTimingSetting(defaultsOnly ? 250 : 500, defaultsOnly ? 87.5 : 80), bus.Nominal);
        Assert.Equal(defaultsOnly ? null : new BitTimingSetting(1000, 75), bus.Data);
    }

    [Fact]
    public async Task ParseAsync_SkipsVectorIndependentSignalsMessage()
    {
        const string dbc = """
            VERSION ""
            BO_ 256 EngineStatus: 8 Vector__XXX
             SG_ EngineSpeed : 0|16@1+ (0.25,0) [0|16383.75] "rpm" Vector__XXX
            BO_ 0 VECTOR_INDEPENDENT_SIG_MSG: 0 Vector__XXX
             SG_ UnassignedSignal : 0|8@1+ (1,0) [0|255] "" Vector__XXX
            """;
        var parser = new DbcParser();
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes(dbc));

        var database = await parser.ParseAsync(input);

        var message = Assert.Single(database.Messages);
        Assert.Equal("EngineStatus", message.Name);
    }

    [Fact]
    public async Task ParseAsync_SkipsMessagesWithZeroIdOrZeroDlc()
    {
        const string dbc = """
            VERSION ""
            BO_ 256 EngineStatus: 8 Vector__XXX
             SG_ EngineSpeed : 0|16@1+ (0.25,0) [0|16383.75] "rpm" Vector__XXX
            BO_ 0 SomeOtherName: 8 Vector__XXX
             SG_ Unassigned : 0|8@1+ (1,0) [0|255] "" Vector__XXX
            BO_ 512 EmptyPlaceholder: 0 Vector__XXX
            """;
        var parser = new DbcParser();
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes(dbc));

        var database = await parser.ParseAsync(input);

        var message = Assert.Single(database.Messages);
        Assert.Equal("EngineStatus", message.Name);
    }

    [Fact]
    public async Task ParseAsync_DecodesCp949EncodedUnitStrings()
    {
        var parser = new DbcParser(); // triggers the static ctor that registers the CP949 provider
        const string dbc = """
            VERSION ""
            BO_ 256 CoolantStatus: 8 Vector__XXX
             SG_ CoolantTemp : 0|16@1+ (0.1,0) [0|6553.5] "℃" Vector__XXX
            """;
        var cp949 = Encoding.GetEncoding(949);
        await using var input = new MemoryStream(cp949.GetBytes(dbc));

        var database = await parser.ParseAsync(input);

        var message = Assert.Single(database.Messages);
        var signal = Assert.Single(message.Signals);
        Assert.Equal("℃", signal.Unit);
    }

    [Fact]
    public async Task ParseAsync_TreatsZeroZeroRangeAsUnset()
    {
        const string dbc = """
            VERSION ""
            BO_ 256 EngineStatus: 8 Vector__XXX
             SG_ EngineSpeed : 0|16@1+ (0.25,0) [0|0] "rpm" Vector__XXX
             SG_ CoolantTemp : 16|8@1+ (1,-40) [-40|100] "C" Vector__XXX
            """;
        var parser = new DbcParser();
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes(dbc));

        var database = await parser.ParseAsync(input);

        var message = Assert.Single(database.Messages);
        var unset = Assert.Single(message.Signals, s => s.Name == "EngineSpeed");
        Assert.Null(unset.Minimum);
        Assert.Null(unset.Maximum);
        var authored = Assert.Single(message.Signals, s => s.Name == "CoolantTemp");
        Assert.Equal(-40, authored.Minimum);
        Assert.Equal(100, authored.Maximum);
    }
}
