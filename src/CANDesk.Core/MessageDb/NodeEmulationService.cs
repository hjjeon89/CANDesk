namespace CANDesk.Core.MessageDb;

/// <summary>Provides the messages transmitted by the node currently emulated by CANDesk.</summary>
public interface INodeEmulationService
{
    string? EmulatedNode { get; }
    void SelectNode(string? nodeName);
    IReadOnlyList<DbcMessage> GetTransmitMessages();
}

public sealed class NodeEmulationService(IEditableMessageDatabase database) : INodeEmulationService
{
    public string? EmulatedNode { get; private set; }

    public void SelectNode(string? nodeName)
    {
        if (nodeName is not null && !database.Nodes.Any(node => node.Name == nodeName))
        {
            throw new KeyNotFoundException($"Node '{nodeName}' was not found.");
        }

        EmulatedNode = nodeName;
    }

    public IReadOnlyList<DbcMessage> GetTransmitMessages()
    {
        if (EmulatedNode is null)
        {
            return [];
        }

        return database.Snapshot.Messages
            .Where(message => database.GetNodeAssignment(message.CanId).TransmitterNode == EmulatedNode)
            .OrderBy(message => message.CanId)
            .ToArray();
    }
}
