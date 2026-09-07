using CANDesk.Core.MessageDb;
using Xunit;

namespace CANDesk.Core.Tests;

public sealed class DbcWriterTests
{
    [Fact]
    public async Task WriteThenParse_RoundTripsMessageAndMultiplexedSignals()
    {
        var database = new MessageDatabase([
            new DbcMessage(0x100, "EngineStatus", 8,
            [
                new("Mode", 0, 4, ByteOrder.Intel, false, MultiplexorRole: MultiplexorRole.Switch),
                new("EngineSpeed", 7, 16, ByteOrder.Motorola, false, 0.25, 0, Minimum: 0, Maximum: 16383.75, Unit: "rpm", MultiplexorRole: MultiplexorRole.Value, MultiplexValue: 2)
            ])
        ]);

        await using var stream = new MemoryStream();
        await new DbcWriter().WriteAsync(database, stream);
        stream.Position = 0;

        var reparsed = await new DbcParser().ParseAsync(stream);

        var message = Assert.Single(reparsed.Messages);
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
                Assert.Equal(0.25, signal.Factor);
            });
    }

    [Fact]
    public async Task WriteAsync_WithNodesAndAssignments_EmitsTransmitterAndReceivers()
    {
        var database = new MessageDatabase([new DbcMessage(0x301, "VCU_Control", 8, [])]);
        var nodes = new[] { new CanNode("VCU"), new CanNode("BMS") };
        var assignments = new Dictionary<uint, MessageNodeAssignment>
        {
            [0x301] = new("VCU", new HashSet<string> { "BMS" })
        };

        await using var stream = new MemoryStream();
        await new DbcWriter().WriteAsync(nodes, database, canId => assignments[canId], stream);
        stream.Position = 0;
        using var reader = new StreamReader(stream);
        var text = await reader.ReadToEndAsync();

        Assert.Contains("BU_: VCU BMS", text);
        Assert.Contains("BO_ 769 VCU_Control: 8 VCU", text);
    }

    [Fact]
    public async Task WriteAsync_SetsExtendedIdBit_WhenMessageIsExtended()
    {
        var database = new MessageDatabase([new DbcMessage(0x1FFFFFFF, "ExtendedMessage", 8, [], IsExtended: true)]);

        await using var stream = new MemoryStream();
        await new DbcWriter().WriteAsync(database, stream);
        stream.Position = 0;
        using var reader = new StreamReader(stream);
        var text = await reader.ReadToEndAsync();

        Assert.Contains($"BO_ {0x1FFFFFFFu | 0x8000_0000u} ExtendedMessage:", text);
    }
}
