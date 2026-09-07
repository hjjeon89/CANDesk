using CANDesk.Core.MessageDb;
using Xunit;

namespace CANDesk.Core.Tests;

public sealed class CandeskXmlParserTests
{
    [Fact]
    public async Task WriteThenParseDocument_RoundTripsMessagesSignalsNodesAndAssignments()
    {
        var database = new MessageDatabase([
            new DbcMessage(0x301, "VCU_Control", 8,
            [
                new("TorqueRequest", 0, 16, ByteOrder.Intel, true, 1, 0, Unit: "Nm"),
                new("GearSelector", 24, 4, ByteOrder.Intel, false, MultiplexorRole: MultiplexorRole.Switch),
                new("GearRatio", 28, 8, ByteOrder.Intel, false, 0.1, 0, Minimum: 0, Maximum: 10, MultiplexorRole: MultiplexorRole.Value, MultiplexValue: 1)
            ])
        ]);
        var nodes = new[] { new CanNode("VCU"), new CanNode("BMS") };
        var assignments = new Dictionary<uint, MessageNodeAssignment>
        {
            [0x301] = new("VCU", new HashSet<string> { "BMS" })
        };

        await using var stream = new MemoryStream();
        await new CandeskXmlWriter().WriteAsync(nodes, database, canId => assignments[canId], stream);
        stream.Position = 0;

        var document = await new CandeskXmlParser().ParseDocumentAsync(stream);

        Assert.Equal(["BMS", "VCU"], document.Nodes.Select(node => node.Name).OrderBy(name => name, StringComparer.Ordinal));
        var message = Assert.Single(document.Database.Messages);
        Assert.Equal(0x301u, message.CanId);
        Assert.Equal("VCU_Control", message.Name);
        Assert.Equal((byte)8, message.Dlc);
        Assert.False(message.IsExtended);

        var assignment = document.Assignments[0x301];
        Assert.Equal("VCU", assignment.TransmitterNode);
        Assert.Equal(["BMS"], assignment.ReceiverNodes);

        Assert.Collection(message.Signals,
            signal =>
            {
                Assert.Equal("TorqueRequest", signal.Name);
                Assert.True(signal.IsSigned);
                Assert.Equal("Nm", signal.Unit);
            },
            signal =>
            {
                Assert.Equal(MultiplexorRole.Switch, signal.MultiplexorRole);
            },
            signal =>
            {
                Assert.Equal(MultiplexorRole.Value, signal.MultiplexorRole);
                Assert.Equal(1, signal.MultiplexValue);
                Assert.Equal(0, signal.Minimum);
                Assert.Equal(10, signal.Maximum);
            });
    }

    [Fact]
    public async Task ParseAsync_IgnoresNodeAndAssignmentMetadata()
    {
        var database = new MessageDatabase([new DbcMessage(0x100, "EngineStatus", 8, [])]);
        var nodes = new[] { new CanNode("ECU") };
        await using var stream = new MemoryStream();
        await new CandeskXmlWriter().WriteAsync(nodes, database, _ => new MessageNodeAssignment("ECU", new HashSet<string>()), stream);
        stream.Position = 0;

        var parsedDatabase = await new CandeskXmlParser().ParseAsync(stream);

        var message = Assert.Single(parsedDatabase.Messages);
        Assert.Equal(0x100u, message.CanId);
    }

    [Fact]
    public async Task Save_ThenReopen_SeedsEditableDatabaseWithNodesAndAssignments()
    {
        var database = new MessageDatabase([new DbcMessage(0x200, "BatteryPackStatus", 8, [])]);
        var editable = new EditableMessageDatabase(database);
        editable.AddNode("BMS");
        editable.SetTransmitterNode(0x200, "BMS");

        await using var stream = new MemoryStream();
        await new CandeskXmlWriter().WriteAsync(editable.Nodes, editable.Snapshot, editable.GetNodeAssignment, stream);
        stream.Position = 0;

        var document = await new CandeskXmlParser().ParseDocumentAsync(stream);
        var reopened = new EditableMessageDatabase(document.Database, document.Nodes, document.Assignments);

        Assert.Equal("BMS", Assert.Single(reopened.Nodes).Name);
        Assert.Equal("BMS", reopened.GetNodeAssignment(0x200).TransmitterNode);
    }
}
