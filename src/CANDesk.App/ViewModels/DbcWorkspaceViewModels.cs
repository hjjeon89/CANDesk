using System.Collections.ObjectModel;
using System.IO;
using CANDesk.Core.MessageDb;
using CANDesk.Core.Scheduling;
using CANDesk.Hal;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CANDesk.App.ViewModels;

public sealed partial class DbcSignalTreeViewModel : ObservableObject
{
    // Synthetic bit layouts for the built-in demo messages, so "Send to TX" and signal
    // editing work out of the box before a real DBC is loaded.
    private static readonly DbcMessage EngineStatusMessage = new(0x100, "EngineStatus", 8,
    [
        new("EngineSpeed", 0, 16, ByteOrder.Intel, false, 0.1, 0, Unit: "rpm"),
        new("CoolantTemp", 16, 8, ByteOrder.Intel, false, 1, -40, Unit: "°C"),
        new("EngineState", 24, 8, ByteOrder.Intel, false)
    ]);
    private static readonly DbcMessage BatteryPackStatusMessage = new(0x200, "BatteryPackStatus", 8,
    [
        new("PackVoltage", 0, 16, ByteOrder.Intel, false, 0.1, 0, Unit: "V"),
        new("PackCurrent", 16, 16, ByteOrder.Intel, true, 0.1, 0, Unit: "A"),
        new("StateOfCharge", 32, 8, ByteOrder.Intel, false, Unit: "%")
    ]);
    private static readonly DbcMessage VcuControlMessage = new(0x301, "VCU_Control", 8,
    [
        new("TorqueRequest", 0, 16, ByteOrder.Intel, true, Unit: "Nm"),
        new("RollingCounter", 16, 8, ByteOrder.Intel, false)
    ]);

    public ObservableCollection<MessageTreeItem> Messages { get; } =
    [
        new("0x100", "EngineStatus", "10 ms | DLC: 8", [new("EngineSpeed", "2,450 rpm"), new("CoolantTemp", "87.5 C"), new("EngineState", "Running")], EngineStatusMessage),
        new("0x200", "BatteryPackStatus", "50 ms | DLC: 8", [new("PackVoltage", "398.2 V"), new("PackCurrent", "-24.5 A"), new("StateOfCharge", "78 %")], BatteryPackStatusMessage),
        new("0x301", "VCU_Control", "Cyclic | DLC: 8", [new("TorqueRequest", "120 Nm"), new("RollingCounter", "0")], VcuControlMessage)
    ];

    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>Raised when the user asks to create a Transmit job from a message (e.g. via "+ TX").</summary>
    public event Action<DbcMessage>? SendToTransmitRequested;

    public void Load(IMessageDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        Messages.Clear();
        foreach (var message in database.Messages.OrderBy(message => message.CanId))
        {
            var signals = message.Signals
                .Select(signal => new SignalTreeItem(signal.Name, signal.Unit ?? string.Empty))
                .ToArray();
            Messages.Add(new MessageTreeItem($"0x{message.CanId:X3}", message.Name, $"DLC: {message.Dlc}", signals, message));
        }
    }

    [RelayCommand(CanExecute = nameof(CanSendToTransmit))]
    private void SendToTransmit(MessageTreeItem? item)
    {
        if (item?.Source is null) return;
        SendToTransmitRequested?.Invoke(item.Source);
    }

    private static bool CanSendToTransmit(MessageTreeItem? item) => item?.Source is not null;
}

public sealed partial class MessageDbEditorViewModel : ObservableObject
{
    private EditableMessageDatabase _database = new(new MessageDatabase([]));
    private bool _isSyncingAssignments;

    public ObservableCollection<EditableMessageRow> Messages { get; } = [];

    [ObservableProperty]
    private EditableMessageRow? _selectedMessage;

    public ObservableCollection<EditorNodeRow> Nodes { get; } = [];

    /// <summary>Raised after the available node set changes.</summary>
    public event EventHandler? NodesChanged;

    [ObservableProperty]
    private EditorNodeRow? _selectedNode;

    [ObservableProperty]
    private string _messageIdText = string.Empty;

    [ObservableProperty]
    private string _messageNameText = string.Empty;

    [ObservableProperty]
    private string _messageDlcText = string.Empty;

    [ObservableProperty]
    private bool _isExtendedMessage;

    [ObservableProperty]
    private string _editError = string.Empty;

    public bool CanUndo => _database.CanUndo;
    public bool CanRedo => _database.CanRedo;
    public bool HasSelectedMessage => SelectedMessage is not null;

    [ObservableProperty]
    private string _saveError = string.Empty;

    [RelayCommand]
    private async Task SaveAsync()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CANDesk XML database (*.xml)|*.xml",
            FileName = "candesk-database.xml",
            Title = "Save Message Database"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            await using var stream = File.Create(dialog.FileName);
            await new CandeskXmlWriter().WriteAsync(_database.Snapshot, stream);
            SaveError = string.Empty;
        }
        catch (Exception exception)
        {
            SaveError = $"Save failed: {exception.Message}";
        }
    }

    [RelayCommand]
    private void AddMessage()
    {
        var canId = (uint)(0x400 + Messages.Count);
        var message = _database.AddMessage(new NewMessageSpec(canId, "NewMessage", 8));
        SyncFromDatabase(message.CanId);
    }

    [RelayCommand]
    private void AddNode()
    {
        var index = Nodes.Count + 1;
        var coreNode = _database.AddNode($"Node {index}");
        SyncFromDatabase(selectedNodeName: coreNode.Name);
    }

    [RelayCommand]
    private void RemoveSelectedMessage()
    {
        if (SelectedMessage is null || !TryParseCanId(SelectedMessage.Id, out var canId)) return;
        _database.RemoveMessage(canId);
        SyncFromDatabase();
    }

    [RelayCommand]
    private void RemoveSelectedNode()
    {
        if (SelectedNode is null) return;
        _database.RemoveNode(SelectedNode.Name);
        SyncFromDatabase();
    }

    [RelayCommand(CanExecute = nameof(CanApplySelectedMessage))]
    private void ApplySelectedMessage()
    {
        if (SelectedMessage is null || !TryParseCanId(SelectedMessage.Id, out var currentCanId)) return;
        if (!TryParseCanId(MessageIdText, out var newCanId))
        {
            EditError = "CAN ID must be a hexadecimal value.";
            return;
        }
        if (string.IsNullOrWhiteSpace(MessageNameText))
        {
            EditError = "Message name is required.";
            return;
        }
        if (!byte.TryParse(MessageDlcText, out var dlc) || dlc > 64)
        {
            EditError = "DLC must be a number from 0 to 64.";
            return;
        }

        try
        {
            _database.ChangeMessageId(currentCanId, newCanId);
            _database.UpdateMessage(newCanId, new MessageEdit(MessageNameText.Trim(), dlc, IsExtendedMessage));
            EditError = string.Empty;
            SyncFromDatabase(newCanId, SelectedNode?.Name);
        }
        catch (Exception exception)
        {
            EditError = exception.Message;
        }
    }

    private bool CanApplySelectedMessage() => SelectedMessage is not null;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        uint? selectedCanId = SelectedMessage is not null && TryParseCanId(SelectedMessage.Id, out var canId)
            ? canId
            : null;
        var selectedNodeName = SelectedNode?.Name;
        _database.Undo();
        SyncFromDatabase(selectedCanId, selectedNodeName);
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        uint? selectedCanId = SelectedMessage is not null && TryParseCanId(SelectedMessage.Id, out var canId)
            ? canId
            : null;
        var selectedNodeName = SelectedNode?.Name;
        _database.Redo();
        SyncFromDatabase(selectedCanId, selectedNodeName);
    }

    public void Clear()
    {
        _database = new EditableMessageDatabase(new MessageDatabase([]));
        SyncFromDatabase();
    }

    /// <summary>Returns messages assigned to the named transmitter node.</summary>
    public IReadOnlyList<DbcMessage> GetTransmitMessages(string nodeName)
    {
        var emulation = new NodeEmulationService(_database);
        emulation.SelectNode(nodeName);
        return emulation.GetTransmitMessages();
    }

    partial void OnSelectedMessageChanged(EditableMessageRow? value)
    {
        ApplySelectedMessageCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasSelectedMessage));
        if (value is null || !TryParseCanId(value.Id, out var canId))
        {
            MessageIdText = string.Empty;
            MessageNameText = string.Empty;
            MessageDlcText = string.Empty;
            IsExtendedMessage = false;
            return;
        }
        var message = _database.Snapshot.Messages.Single(message => message.CanId == canId);
        MessageIdText = $"0x{message.CanId:X3}";
        MessageNameText = message.Name;
        MessageDlcText = message.Dlc.ToString();
        IsExtendedMessage = message.IsExtended;
        EditError = string.Empty;
        var assignment = _database.GetNodeAssignment(canId);
        _isSyncingAssignments = true;
        try
        {
            foreach (var node in Nodes)
            {
                node.IsTransmitter = node.Name == assignment.TransmitterNode;
                node.IsReceiver = assignment.ReceiverNodes.Contains(node.Name);
            }
        }
        finally
        {
            _isSyncingAssignments = false;
        }
    }

    private void OnNodePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (_isSyncingAssignments || SelectedMessage is null || sender is not EditorNodeRow node || !TryParseCanId(SelectedMessage.Id, out var canId)) return;
        if (args.PropertyName == nameof(EditorNodeRow.IsTransmitter) && node.IsTransmitter)
        {
            _database.SetTransmitterNode(canId, node.Name);
            _isSyncingAssignments = true;
            try
            {
                foreach (var other in Nodes.Where(item => item != node)) other.IsTransmitter = false;
            }
            finally { _isSyncingAssignments = false; }
        }
        else if (args.PropertyName == nameof(EditorNodeRow.IsTransmitter) && _database.GetNodeAssignment(canId).TransmitterNode == node.Name)
        {
            _database.SetTransmitterNode(canId, null);
        }
        if (args.PropertyName == nameof(EditorNodeRow.IsReceiver))
        {
            _database.SetReceiverNodes(canId, Nodes.Where(item => item.IsReceiver).Select(item => item.Name));
        }
        RefreshCommandState();
    }

    private static bool TryParseCanId(string text, out uint canId)
    {
        var normalized = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
        return uint.TryParse(normalized, System.Globalization.NumberStyles.HexNumber, null, out canId);
    }

    private void SyncFromDatabase(uint? selectedCanId = null, string? selectedNodeName = null)
    {
        selectedCanId ??= SelectedMessage is not null && TryParseCanId(SelectedMessage.Id, out var currentCanId)
            ? currentCanId
            : null;
        selectedNodeName ??= SelectedNode?.Name;

        Messages.Clear();
        foreach (var message in _database.Snapshot.Messages.OrderBy(message => message.CanId))
        {
            Messages.Add(new EditableMessageRow($"0x{message.CanId:X3}", message.Name, message.Dlc));
        }

        foreach (var node in Nodes)
        {
            node.PropertyChanged -= OnNodePropertyChanged;
        }
        Nodes.Clear();
        foreach (var node in _database.Nodes.OrderBy(node => node.Name, StringComparer.Ordinal))
        {
            var row = new EditorNodeRow(node.Name);
            row.PropertyChanged += OnNodePropertyChanged;
            Nodes.Add(row);
        }

        SelectedMessage = selectedCanId is not null
            ? Messages.FirstOrDefault(message => TryParseCanId(message.Id, out var canId) && canId == selectedCanId)
            : Messages.LastOrDefault();
        SelectedNode = selectedNodeName is not null
            ? Nodes.FirstOrDefault(node => node.Name == selectedNodeName)
            : Nodes.LastOrDefault();
        RefreshCommandState();
        NodesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshCommandState()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }
}

public sealed partial class TransmitPanelViewModel : ObservableObject
{
    private Func<CanFrame, Task>? _sendFrameAsync;
    private ITxScheduler? _scheduler;
    private readonly Dictionary<TxJobRow, Guid> _cyclicJobIds = [];
    private Func<IReadOnlyList<string>>? _nodeNamesProvider;
    private Func<string, IReadOnlyList<DbcMessage>>? _nodeMessagesProvider;
    public ObservableCollection<TxJobRow> Jobs { get; } =
    [
        new("0x301", "VCU_Control", "20 ms", "8", "AA BB CC 00 00 00 00 12", true, true, true),
        new("0x7DF", "OBD-II Req (Tester)", "Manual", "8", "02 01 0C 55 55 55 55 55", false, false, false)
    ];

    public ObservableCollection<string> EmulatedNodes { get; } = [];

    [ObservableProperty]
    private string? _selectedEmulatedNode;

    [RelayCommand]
    private void AddJob() => Jobs.Add(new("0x000", "New TX Job", "Manual", "8", "00 00 00 00 00 00 00 00", false, false, false));

    public void SetNodeEmulationProviders(
        Func<IReadOnlyList<string>> nodeNamesProvider,
        Func<string, IReadOnlyList<DbcMessage>> nodeMessagesProvider)
    {
        _nodeNamesProvider = nodeNamesProvider;
        _nodeMessagesProvider = nodeMessagesProvider;
        RefreshEmulatedNodes();
    }

    public void RefreshEmulatedNodes()
    {
        var previousSelection = SelectedEmulatedNode;
        EmulatedNodes.Clear();
        if (_nodeNamesProvider is not null)
        {
            foreach (var name in _nodeNamesProvider())
            {
                EmulatedNodes.Add(name);
            }
        }
        SelectedEmulatedNode = previousSelection is not null && EmulatedNodes.Contains(previousSelection)
            ? previousSelection
            : EmulatedNodes.FirstOrDefault();
        AddEmulatedNodeJobsCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanAddEmulatedNodeJobs))]
    private void AddEmulatedNodeJobs()
    {
        if (SelectedEmulatedNode is null || _nodeMessagesProvider is null)
        {
            return;
        }

        foreach (var message in _nodeMessagesProvider(SelectedEmulatedNode))
        {
            AddJobFromMessage(message);
        }
    }

    private bool CanAddEmulatedNodeJobs() => SelectedEmulatedNode is not null && _nodeMessagesProvider is not null;

    partial void OnSelectedEmulatedNodeChanged(string? value) => AddEmulatedNodeJobsCommand.NotifyCanExecuteChanged();

    /// <summary>Creates a TX job from a DBC message with one editable field per signal, so
    /// payload bytes can be set by physical value instead of typing raw hex.</summary>
    public TxJobRow AddJobFromMessage(DbcMessage message)
    {
        var zeroPayload = string.Join(' ', Enumerable.Repeat("00", message.Dlc));
        var job = new TxJobRow($"0x{message.CanId:X3}", message.Name, "Manual", message.Dlc.ToString(), zeroPayload, false, false, false)
        {
            Message = message
        };
        foreach (var signal in message.Signals)
        {
            job.Signals.Add(new TxSignalEditRow(job, signal, signal.Offset));
        }
        Jobs.Add(job);
        return job;
    }

    [ObservableProperty]
    private string _lastSendError = string.Empty;

    public void SetSendHandler(Func<CanFrame, Task>? sendFrameAsync) => _sendFrameAsync = sendFrameAsync;

    public void SetScheduler(ITxScheduler? scheduler)
    {
        foreach (var jobId in _cyclicJobIds.Values)
        {
            _scheduler?.Cancel(jobId);
        }
        _cyclicJobIds.Clear();
        _scheduler = scheduler;
    }

    [RelayCommand]
    private async Task SendNowAsync(TxJobRow? job)
    {
        if (job is null)
        {
            return;
        }

        if (_sendFrameAsync is null)
        {
            LastSendError = "Connect a CAN device before sending.";
            return;
        }

        try
        {
            await _sendFrameAsync(CreateFrame(job));
            LastSendError = string.Empty;
        }
        catch (Exception exception)
        {
            LastSendError = exception.Message;
        }
    }

    [RelayCommand]
    private void ToggleCyclic(TxJobRow? job)
    {
        if (job is null) return;
        if (_cyclicJobIds.Remove(job, out var jobId))
        {
            _scheduler?.Cancel(jobId);
            job.IsEnabled = false;
            LastSendError = string.Empty;
            return;
        }
        if (_scheduler is null)
        {
            LastSendError = "Connect a CAN device before scheduling cyclic transmission.";
            return;
        }
        try
        {
            var periodText = job.Period.Replace("ms", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
            if (!double.TryParse(periodText, out var milliseconds) || milliseconds <= 0)
                throw new InvalidOperationException("A cyclic TX job requires a positive period in milliseconds.");
            _cyclicJobIds.Add(job, _scheduler.ScheduleCyclic(CreateFrame(job), TimeSpan.FromMilliseconds(milliseconds)));
            job.IsEnabled = true;
            LastSendError = string.Empty;
        }
        catch (Exception exception)
        {
            LastSendError = exception.Message;
        }
    }

    private static CanFrame CreateFrame(TxJobRow job)
    {
        var idText = job.Id.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? job.Id[2..] : job.Id;
        var payloadText = job.Payload.Replace(" ", string.Empty, StringComparison.Ordinal);
        return CanFrame.Create(Convert.ToUInt32(idText, 16), Convert.FromHexString(payloadText));
    }
}

public sealed record EditableMessageRow(string Id, string Name, int Dlc);
public sealed partial class EditorNodeRow(string name) : ObservableObject
{
    [ObservableProperty] private bool _isTransmitter;
    [ObservableProperty] private bool _isReceiver;
    public string Name { get; } = name;
}
