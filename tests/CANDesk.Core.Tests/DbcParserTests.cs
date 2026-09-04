using System.Text;
using CANDesk.Core.MessageDb;
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
}
