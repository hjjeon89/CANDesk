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
    DbcMessage AddMessage(NewMessageSpec spec);
    void UpdateMessage(uint canId, MessageEdit edit);
    void RemoveMessage(uint canId);
    DbcSignal AddSignal(uint canId, NewSignalSpec spec);
    void UpdateSignal(uint canId, string signalName, SignalEdit edit);
    void RemoveSignal(uint canId, string signalName);
    void Undo(); void Redo();
}

public sealed class EditableMessageDatabase(IMessageDatabase initial) : IEditableMessageDatabase
{
    private Dictionary<uint, DbcMessage> _messages = initial.Messages.ToDictionary(message => message.CanId);
    private readonly Stack<Dictionary<uint, DbcMessage>> _undo = new();
    private readonly Stack<Dictionary<uint, DbcMessage>> _redo = new();
    public IMessageDatabase Snapshot => new MessageDatabase(_messages.Values);
    public bool IsDirty { get; private set; }
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
    public void RemoveMessage(uint canId) { Get(canId); Save(); _messages.Remove(canId); }
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
    public void Undo() { if (_undo.TryPop(out var state)) { _redo.Push(Clone()); _messages = state; IsDirty = true; } }
    public void Redo() { if (_redo.TryPop(out var state)) { _undo.Push(Clone()); _messages = state; IsDirty = true; } }
    private DbcMessage Get(uint canId) => _messages.TryGetValue(canId, out var message) ? message : throw new KeyNotFoundException($"CAN ID 0x{canId:X} was not found.");
    private void Save() { _undo.Push(Clone()); _redo.Clear(); IsDirty = true; }
    private Dictionary<uint, DbcMessage> Clone() => new(_messages);
    private static void ValidateSignal(DbcMessage message, int startBit, int bitLength, ByteOrder byteOrder, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || startBit < 0 || bitLength is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(startBit));
        if (startBit + bitLength > message.Dlc * 8 && byteOrder == ByteOrder.Intel) throw new InvalidOperationException("Signal exceeds message DLC.");
        if (message.Signals.Any(signal => signal.Name == name)) throw new InvalidOperationException($"Signal '{name}' already exists.");
    }
}

public interface IMessageDatabaseWriter { MessageDbFormat Format { get; } Task WriteAsync(IMessageDatabase database, Stream output, CancellationToken cancellationToken = default); }
