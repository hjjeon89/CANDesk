namespace CANDesk.Core.MessageDb;

public sealed record NewMessageSpec(uint CanId, string Name, byte Dlc, bool IsExtended = false);
public sealed record MessageEdit(string? Name = null, byte? Dlc = null, bool? IsExtended = null);
public sealed record NewSignalSpec(string Name, int StartBit, int BitLength, ByteOrder ByteOrder, bool IsSigned, double Factor = 1,
    double Offset = 0, string? Unit = null, MultiplexorRole MultiplexorRole = MultiplexorRole.None, int? MultiplexValue = null);
public sealed record SignalEdit(string? Name = null, double? Factor = null, double? Offset = null, string? Unit = null);
public interface IEditableMessageDatabase
{
    IMessageDatabase Snapshot { get; }
    bool IsDirty { get; }
    bool CanUndo { get; }
    bool CanRedo { get; }
    DbcMessage AddMessage(NewMessageSpec spec);
    void UpdateMessage(uint canId, MessageEdit edit);
    void ChangeMessageId(uint canId, uint newCanId);
    void RemoveMessage(uint canId);
    DbcSignal AddSignal(uint canId, NewSignalSpec spec);
    void UpdateSignal(uint canId, string signalName, SignalEdit edit);
    void RemoveSignal(uint canId, string signalName);
    IReadOnlyCollection<CanNode> Nodes { get; }
    CanNode AddNode(string name);
    void RemoveNode(string name);
    void SetTransmitterNode(uint canId, string? nodeName);
    void SetReceiverNodes(uint canId, IEnumerable<string> nodeNames);
    MessageNodeAssignment GetNodeAssignment(uint canId);
    void Undo(); void Redo();
}

public sealed class EditableMessageDatabase(IMessageDatabase initial) : IEditableMessageDatabase
{
    private Dictionary<uint, DbcMessage> _messages = initial.Messages.ToDictionary(message => message.CanId);
    private readonly Stack<DatabaseState> _undo = new();
    private readonly Stack<DatabaseState> _redo = new();
    private readonly Dictionary<string, CanNode> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, MessageNodeAssignment> _nodeAssignments = [];
    public IMessageDatabase Snapshot => new MessageDatabase(_messages.Values);
    public bool IsDirty { get; private set; }
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public IReadOnlyCollection<CanNode> Nodes => _nodes.Values;
    public DbcMessage AddMessage(NewMessageSpec spec)
    {
        if (_messages.ContainsKey(spec.CanId)) throw new InvalidOperationException($"CAN ID 0x{spec.CanId:X} already exists.");
        if (spec.Dlc > 64) throw new ArgumentOutOfRangeException(nameof(spec.Dlc));
        Save(); var message = new DbcMessage(spec.CanId, spec.Name, spec.Dlc, [], spec.IsExtended); _messages.Add(spec.CanId, message); return message;
    }
    public void UpdateMessage(uint canId, MessageEdit edit)
    {
        var message = Get(canId); Save(); _messages[canId] = message with { Name = edit.Name ?? message.Name, Dlc = edit.Dlc ?? message.Dlc, IsExtended = edit.IsExtended ?? message.IsExtended };
    }
    public void ChangeMessageId(uint canId, uint newCanId)
    {
        if (canId == newCanId) return;
        var message = Get(canId);
        if (_messages.ContainsKey(newCanId)) throw new InvalidOperationException($"CAN ID 0x{newCanId:X} already exists.");
        Save();
        _messages.Remove(canId);
        _messages.Add(newCanId, message with { CanId = newCanId });
        if (_nodeAssignments.Remove(canId, out var assignment)) _nodeAssignments.Add(newCanId, assignment);
    }
    public void RemoveMessage(uint canId)
    {
        Get(canId);
        Save();
        _messages.Remove(canId);
        _nodeAssignments.Remove(canId);
    }
    public DbcSignal AddSignal(uint canId, NewSignalSpec spec)
    {
        var message = Get(canId); ValidateSignal(message, spec.StartBit, spec.BitLength, spec.ByteOrder, spec.Name);
        Save(); var signal = new DbcSignal(spec.Name, spec.StartBit, spec.BitLength, spec.ByteOrder, spec.IsSigned, spec.Factor, spec.Offset, Unit: spec.Unit, MultiplexorRole: spec.MultiplexorRole, MultiplexValue: spec.MultiplexValue);
        _messages[canId] = message with { Signals = message.Signals.Append(signal).ToArray() }; return signal;
    }
    public void UpdateSignal(uint canId, string signalName, SignalEdit edit)
    {
        var message = Get(canId); var signal = message.Signals.SingleOrDefault(signal => signal.Name == signalName) ?? throw new KeyNotFoundException($"Signal '{signalName}' was not found.");
        Save(); _messages[canId] = message with { Signals = message.Signals.Select(item => item == signal ? item with { Name = edit.Name ?? item.Name, Factor = edit.Factor ?? item.Factor, Offset = edit.Offset ?? item.Offset, Unit = edit.Unit ?? item.Unit } : item).ToArray() };
    }
    public void RemoveSignal(uint canId, string signalName)
    { var message = Get(canId); if (!message.Signals.Any(signal => signal.Name == signalName)) throw new KeyNotFoundException($"Signal '{signalName}' was not found."); Save(); _messages[canId] = message with { Signals = message.Signals.Where(signal => signal.Name != signalName).ToArray() }; }
    public CanNode AddNode(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Node name is required.", nameof(name));
        if (_nodes.ContainsKey(name)) throw new InvalidOperationException($"Node '{name}' already exists.");
        Save();
        var node = new CanNode(name); _nodes.Add(name, node); return node;
    }
    public void RemoveNode(string name)
    {
        if (!_nodes.ContainsKey(name)) throw new KeyNotFoundException($"Node '{name}' was not found.");
        Save();
        _nodes.Remove(name);
        foreach (var canId in _nodeAssignments.Keys.ToArray())
        {
            var assignment = _nodeAssignments[canId];
            _nodeAssignments[canId] = assignment with
            {
                TransmitterNode = assignment.TransmitterNode == name ? null : assignment.TransmitterNode,
                ReceiverNodes = assignment.ReceiverNodes.Where(node => node != name).ToHashSet(StringComparer.Ordinal)
            };
        }
    }
    public void SetTransmitterNode(uint canId, string? nodeName)
    {
        Get(canId); EnsureNodeExists(nodeName);
        Save();
        var assignment = GetNodeAssignment(canId); _nodeAssignments[canId] = assignment with { TransmitterNode = nodeName };
    }
    public void SetReceiverNodes(uint canId, IEnumerable<string> nodeNames)
    {
        Get(canId); var receivers = nodeNames.Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        foreach (var node in receivers) EnsureNodeExists(node);
        Save();
        var assignment = GetNodeAssignment(canId); _nodeAssignments[canId] = assignment with { ReceiverNodes = receivers };
    }
    public MessageNodeAssignment GetNodeAssignment(uint canId) => _nodeAssignments.TryGetValue(canId, out var assignment)
        ? assignment : new(null, new HashSet<string>(StringComparer.Ordinal));
    public void Undo()
    {
        if (!_undo.TryPop(out var state)) return;
        _redo.Push(CaptureState());
        RestoreState(state);
        IsDirty = true;
    }
    public void Redo()
    {
        if (!_redo.TryPop(out var state)) return;
        _undo.Push(CaptureState());
        RestoreState(state);
        IsDirty = true;
    }
    private DbcMessage Get(uint canId) => _messages.TryGetValue(canId, out var message) ? message : throw new KeyNotFoundException($"CAN ID 0x{canId:X} was not found.");
    private void Save() { _undo.Push(CaptureState()); _redo.Clear(); IsDirty = true; }
    private void EnsureNodeExists(string? name) { if (name is not null && !_nodes.ContainsKey(name)) throw new KeyNotFoundException($"Node '{name}' was not found."); }
    private DatabaseState CaptureState() => new(
        new Dictionary<uint, DbcMessage>(_messages),
        new Dictionary<string, CanNode>(_nodes, StringComparer.Ordinal),
        _nodeAssignments.ToDictionary(
            pair => pair.Key,
            pair => pair.Value with { ReceiverNodes = pair.Value.ReceiverNodes.ToHashSet(StringComparer.Ordinal) }));
    private void RestoreState(DatabaseState state)
    {
        _messages = new Dictionary<uint, DbcMessage>(state.Messages);
        _nodes.Clear();
        foreach (var node in state.Nodes) _nodes.Add(node.Key, node.Value);
        _nodeAssignments.Clear();
        foreach (var assignment in state.Assignments)
        {
            _nodeAssignments.Add(assignment.Key, assignment.Value with
            {
                ReceiverNodes = assignment.Value.ReceiverNodes.ToHashSet(StringComparer.Ordinal)
            });
        }
    }
    private sealed record DatabaseState(
        Dictionary<uint, DbcMessage> Messages,
        Dictionary<string, CanNode> Nodes,
        Dictionary<uint, MessageNodeAssignment> Assignments);
    private static void ValidateSignal(DbcMessage message, int startBit, int bitLength, ByteOrder byteOrder, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || startBit < 0 || bitLength is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(startBit));
        if (startBit + bitLength > message.Dlc * 8 && byteOrder == ByteOrder.Intel) throw new InvalidOperationException("Signal exceeds message DLC.");
        if (message.Signals.Any(signal => signal.Name == name)) throw new InvalidOperationException($"Signal '{name}' already exists.");
    }
}

public interface IMessageDatabaseWriter { MessageDbFormat Format { get; } Task WriteAsync(IMessageDatabase database, Stream output, CancellationToken cancellationToken = default); }
