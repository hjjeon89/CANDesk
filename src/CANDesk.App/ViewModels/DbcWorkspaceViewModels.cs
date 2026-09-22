using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using CANDesk.Core.MessageDb;
using CANDesk.Core.Scheduling;
using CANDesk.Hal;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CANDesk.App.ViewModels;

public sealed partial class DbcSignalTreeViewModel : ObservableObject
{
    public ObservableCollection<MessageTreeItem> Messages { get; } = [];

    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>Filtered view of <see cref="Messages"/> the tree actually binds to — a message
    /// stays visible if its own name/ID matches <see cref="SearchText"/>, or if any of its signals
    /// do (searching for a signal name should surface the message that carries it). Needed once a
    /// real DBC is loaded: large vehicle DBCs can carry thousands of signals across hundreds of
    /// messages, far more than a flat scroll is comfortable to hunt through.</summary>
    public ICollectionView MessagesView { get; }

    /// <summary>Raised when the user asks to create a Transmit job from a message (e.g. via "+ TX").</summary>
    public event Action<DbcMessage>? SendToTransmitRequested;

    public DbcSignalTreeViewModel()
    {
        MessagesView = CollectionViewSource.GetDefaultView(Messages);
        MessagesView.Filter = Matches;
    }

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

    partial void OnSearchTextChanged(string value) => MessagesView.Refresh();

    private bool Matches(object item)
    {
        if (item is not MessageTreeItem message)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(SearchText))
        {
            return true;
        }

        var term = SearchText.Trim();
        return message.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
            || message.Id.Contains(term, StringComparison.OrdinalIgnoreCase)
            || message.Signals.Any(signal => signal.Name.Contains(term, StringComparison.OrdinalIgnoreCase));
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
            await new CandeskXmlWriter().WriteAsync(_database.Nodes, _database.Snapshot, _database.GetNodeAssignment, stream);
            SaveError = string.Empty;
        }
        catch (Exception exception)
        {
            SaveError = $"Save failed: {exception.Message}";
        }
    }

    [RelayCommand]
    private async Task ExportDbcAsync()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "DBC database (*.dbc)|*.dbc",
            FileName = "candesk-database.dbc",
            Title = "Export Message Database as DBC"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            await using var stream = File.Create(dialog.FileName);
            await new DbcWriter().WriteAsync(_database.Nodes, _database.Snapshot, _database.GetNodeAssignment, stream);
            SaveError = string.Empty;
        }
        catch (Exception exception)
        {
            SaveError = $"Export failed: {exception.Message}";
        }
    }

    [RelayCommand]
    private async Task OpenAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "CANDesk XML database (*.xml)|*.xml",
            Title = "Open Message Database"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            await using var stream = File.OpenRead(dialog.FileName);
            var document = await new CandeskXmlParser().ParseDocumentAsync(stream);
            _database = new EditableMessageDatabase(document.Database, document.Nodes, document.Assignments);
            SyncFromDatabase();
            SaveError = string.Empty;
        }
        catch (Exception exception)
        {
            SaveError = $"Open failed: {exception.Message}";
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
    private readonly Dictionary<TxJobRow, Guid> _triggeredJobIds = [];
    private Func<IReadOnlyList<string>>? _nodeNamesProvider;
    private Func<string, IReadOnlyList<DbcMessage>>? _nodeMessagesProvider;
    public ObservableCollection<TxJobRow> Jobs { get; } = [];

    public ObservableCollection<string> EmulatedNodes { get; } = [];

    [ObservableProperty]
    private string? _selectedEmulatedNode;

    public TransmitPanelViewModel()
    {
        foreach (var job in Jobs)
        {
            job.PropertyChanged += OnJobPropertyChanged;
        }

        Jobs.CollectionChanged += (_, e) =>
        {
            if (e.OldItems is not null)
            {
                foreach (TxJobRow job in e.OldItems) job.PropertyChanged -= OnJobPropertyChanged;
            }

            if (e.NewItems is not null)
            {
                foreach (TxJobRow job in e.NewItems) job.PropertyChanged += OnJobPropertyChanged;
            }
        };
    }

    /// <summary>Pushes a Raw-Hex or signal-value edit into the running cyclic job so it actually
    /// changes what's on the bus, instead of only updating the grid. <see cref="TxJobRow.Payload"/>
    /// is the single point both edit paths (typing hex directly, or <see cref="TxSignalEditRow"/>
    /// re-encoding a physical value) funnel through, so one subscription covers both.</summary>
    private void OnJobPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TxJobRow.Payload) || sender is not TxJobRow job)
        {
            return;
        }

        if (!_cyclicJobIds.ContainsKey(job) && !_triggeredJobIds.ContainsKey(job))
        {
            return;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromHexString(job.Payload.Replace(" ", string.Empty, StringComparison.Ordinal));
        }
        catch (FormatException)
        {
            return; // mid-edit invalid hex (odd digit count, stray character); next valid edit pushes through
        }

        try
        {
            if (_cyclicJobIds.TryGetValue(job, out var cyclicJobId))
            {
                _scheduler?.UpdatePayload(cyclicJobId, bytes);
            }

            if (_triggeredJobIds.TryGetValue(job, out var triggeredJobId))
            {
                _scheduler?.UpdatePayload(triggeredJobId, bytes);
            }
        }
        catch (KeyNotFoundException)
        {
            // The job was cancelled concurrently (e.g. Stop clicked right as this fired); nothing to update.
        }
    }

    [RelayCommand]
    private void AddJob() => Jobs.Add(new("0x000", "New TX Job", "0", "8", "00 00 00 00 00 00 00 00", false, false, false));

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
        var job = new TxJobRow($"0x{message.CanId:X3}", message.Name, "0", message.Dlc.ToString(), zeroPayload, false, false, false, message.IsExtended)
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
        foreach (var jobId in _triggeredJobIds.Values)
        {
            _scheduler?.Cancel(jobId);
        }
        _cyclicJobIds.Clear();
        _triggeredJobIds.Clear();
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
            var modifier = E2EProfile1.CreateModifier(job.AutoCounter, job.E2eCrc);
            _cyclicJobIds.Add(job, _scheduler.ScheduleCyclic(CreateFrame(job), TimeSpan.FromMilliseconds(milliseconds), modifier));
            job.IsEnabled = true;
            LastSendError = string.Empty;
        }
        catch (Exception exception)
        {
            LastSendError = exception.Message;
        }
    }

    [RelayCommand]
    private void ToggleTriggered(TxJobRow? job)
    {
        if (job is null) return;
        if (_triggeredJobIds.Remove(job, out var jobId))
        {
            _scheduler?.Cancel(jobId);
            job.IsTriggered = false;
            LastSendError = string.Empty;
            return;
        }
        if (_scheduler is null)
        {
            LastSendError = "Connect a CAN device before scheduling triggered transmission.";
            return;
        }
        try
        {
            var triggerId = ParseCanId(job.TriggerId);
            var triggerMask = ParseCanId(job.TriggerMask);
            var pattern = ParsePayload(job.TriggerPayloadPattern);
            var delay = ParseDelay(job.ResponseDelayMs);
            var repeatCount = ParseRepeatCount(job.RepeatCountText);
            var modifier = E2EProfile1.CreateModifier(job.AutoCounter, job.E2eCrc);
            var rule = new TriggeredTxRule(triggerId, triggerMask, pattern, CreateFrame(job), delay, repeatCount);
            _triggeredJobIds.Add(job, _scheduler.ScheduleTriggered(rule.ResponseFrame, rule, rule.ResponseDelay, rule.RepeatCount, modifier));
            job.IsTriggered = true;
            LastSendError = string.Empty;
        }
        catch (Exception exception)
        {
            LastSendError = exception.Message;
        }
    }

    private const uint StandardIdMax = 0x7FF;
    private const uint ExtendedIdMax = 0x1FFF_FFFF;

    private static CanFrame CreateFrame(TxJobRow job)
    {
        var id = ParseCanId(job.Id);
        if (id > ExtendedIdMax)
        {
            throw new InvalidOperationException($"CAN ID 0x{id:X} exceeds the 29-bit extended ID range (max 0x{ExtendedIdMax:X}).");
        }

        // A standard (11-bit) frame can't carry an ID above 0x7FF, so an ID that large only ever
        // makes sense as extended — auto-promote rather than silently sending it as (invalid)
        // standard, and reflect the correction back into the "Ext" checkbox so the UI doesn't show
        // a stale unchecked state for a frame that actually went out extended.
        if (id > StandardIdMax && !job.IsExtended)
        {
            job.IsExtended = true;
        }

        var flags = job.IsExtended ? CanFrameFlags.Extended : CanFrameFlags.None;
        return CanFrame.Create(id, ParsePayload(job.Payload), flags);
    }

    private static uint ParseCanId(string text)
    {
        var trimmed = text.Trim();
        var idText = trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? trimmed[2..] : trimmed;
        return Convert.ToUInt32(idText, 16);
    }

    private static byte[] ParsePayload(string text)
    {
        var payloadText = text.Replace(" ", string.Empty, StringComparison.Ordinal);
        return string.IsNullOrWhiteSpace(payloadText) ? [] : Convert.FromHexString(payloadText);
    }

    private static TimeSpan ParseDelay(string text)
    {
        if (!double.TryParse(text.Replace("ms", string.Empty, StringComparison.OrdinalIgnoreCase).Trim(), out var milliseconds) || milliseconds < 0)
        {
            throw new InvalidOperationException("Response delay must be zero or a positive millisecond value.");
        }

        return TimeSpan.FromMilliseconds(milliseconds);
    }

    private static int? ParseRepeatCount(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (!int.TryParse(text.Trim(), out var repeatCount) || repeatCount <= 0)
        {
            throw new InvalidOperationException("Repeat count must be blank or a positive integer.");
        }

        return repeatCount;
    }
}

public sealed record EditableMessageRow(string Id, string Name, int Dlc);
public sealed partial class EditorNodeRow(string name) : ObservableObject
{
    [ObservableProperty] private bool _isTransmitter;
    [ObservableProperty] private bool _isReceiver;
    public string Name { get; } = name;
}
