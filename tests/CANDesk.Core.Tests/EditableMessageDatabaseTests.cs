using CANDesk.Core.MessageDb;
using Xunit;

namespace CANDesk.Core.Tests;

public sealed class EditableMessageDatabaseTests
{
    [Fact]
    public void NodeAssignments_TrackTransmitterAndReceiversPerMessage()
    {
        var database = new EditableMessageDatabase(new MessageDatabase([new DbcMessage(0x120, "Status", 8, [])]));
        database.AddNode("VCU");
        database.AddNode("BMS");
        database.AddNode("Cluster");

        database.SetTransmitterNode(0x120, "VCU");
        database.SetReceiverNodes(0x120, ["BMS", "Cluster"]);

        var assignment = database.GetNodeAssignment(0x120);
        Assert.Equal("VCU", assignment.TransmitterNode);
        Assert.Equal(["BMS", "Cluster"], assignment.ReceiverNodes.OrderBy(name => name));
    }

    [Fact]
    public void RemovingNode_RemovesItFromExistingAssignments()
    {
        var database = new EditableMessageDatabase(new MessageDatabase([new DbcMessage(0x120, "Status", 8, [])]));
        database.AddNode("VCU");
        database.AddNode("BMS");
        database.SetTransmitterNode(0x120, "VCU");
        database.SetReceiverNodes(0x120, ["BMS"]);

        database.RemoveNode("VCU");
        database.RemoveNode("BMS");

        var assignment = database.GetNodeAssignment(0x120);
        Assert.Null(assignment.TransmitterNode);
        Assert.Empty(assignment.ReceiverNodes);
    }

    [Fact]
    public void NodeEmulation_ReturnsOnlyMessagesTransmittedBySelectedNode()
    {
        var database = new EditableMessageDatabase(new MessageDatabase(
        [
            new DbcMessage(0x120, "Status", 8, []),
            new DbcMessage(0x121, "Command", 8, [])
        ]));
        database.AddNode("VCU");
        database.AddNode("BMS");
        database.SetTransmitterNode(0x120, "VCU");
        database.SetTransmitterNode(0x121, "BMS");
        var emulation = new NodeEmulationService(database);

        emulation.SelectNode("VCU");

        Assert.Collection(emulation.GetTransmitMessages(), message => Assert.Equal((uint)0x120, message.CanId));
    }

    [Fact]
    public void Undo_RestoresNodeAssignmentsTogetherWithMessages()
    {
        var database = new EditableMessageDatabase(new MessageDatabase([new DbcMessage(0x120, "Status", 8, [])]));
        database.AddNode("VCU");
        database.AddNode("BMS");
        database.SetTransmitterNode(0x120, "VCU");
        database.SetReceiverNodes(0x120, ["BMS"]);

        database.RemoveNode("BMS");
        database.Undo();

        Assert.Contains(database.Nodes, node => node.Name == "BMS");
        var assignment = database.GetNodeAssignment(0x120);
        Assert.Equal("VCU", assignment.TransmitterNode);
        Assert.Contains("BMS", assignment.ReceiverNodes);
        Assert.True(database.CanRedo);
    }

    [Fact]
    public void ChangeMessageId_PreservesNodeAssignmentAndSupportsUndo()
    {
        var database = new EditableMessageDatabase(new MessageDatabase([new DbcMessage(0x120, "Status", 8, [])]));
        database.AddNode("VCU");
        database.SetTransmitterNode(0x120, "VCU");

        database.ChangeMessageId(0x120, 0x220);

        Assert.Equal((uint)0x220, Assert.Single(database.Snapshot.Messages).CanId);
        Assert.Equal("VCU", database.GetNodeAssignment(0x220).TransmitterNode);

        database.Undo();

        Assert.Equal((uint)0x120, Assert.Single(database.Snapshot.Messages).CanId);
        Assert.Equal("VCU", database.GetNodeAssignment(0x120).TransmitterNode);
    }
}
