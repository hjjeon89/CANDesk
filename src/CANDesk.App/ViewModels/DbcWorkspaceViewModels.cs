using System.Collections.ObjectModel;
using CANDesk.Core.MessageDb;
using CANDesk.Core.Scheduling;
using CANDesk.Hal;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CANDesk.App.ViewModels;

public sealed partial class DbcSignalTreeViewModel : ObservableObject
{
    public ObservableCollection<MessageTreeItem> Messages { get; } =
    [
        new("0x100", "EngineStatus", "10 ms | DLC: 8", [new("EngineSpeed", "2,450 rpm"), new("CoolantTemp", "87.5 C"), new("EngineState", "Running")]),
        new("0x200", "BatteryPackStatus", "50 ms | DLC: 8", [new("PackVoltage", "398.2 V"), new("PackCurrent", "-24.5 A"), new("StateOfCharge", "78 %")]),
        new("0x301", "VCU_Control", "Cyclic | DLC: 8", [new("TorqueRequest", "120 Nm"), new("RollingCounter", "0")])
    ];

    [ObservableProperty]
    private string _searchText = string.Empty;

    public void Load(IMessageDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        Messages.Clear();
        foreach (var message in database.Messages.OrderBy(message => message.CanId))
        {
            var signals = message.Signals
                .Select(signal => new SignalTreeItem(signal.Name, signal.Unit ?? string.Empty))
                .ToArray();
            Messages.Add(new MessageTreeItem($"0x{message.CanId:X3}", message.Name, $"DLC: {message.Dlc}", signals));
        }
    }
}

public sealed partial class MessageDbEditorViewModel : ObservableObject
{
    private readonly Stack<EditableMessageRow> _redoMessages = [];

    public ObservableCollection<EditableMessageRow> Messages { get; } = [];

    [ObservableProperty]
    private EditableMessageRow? _selectedMessage;

    public bool CanUndo => Messages.Count > 0;
    public bool CanRedo => _redoMessages.Count > 0;

    [RelayCommand]
    private void AddMessage()
    {
        Messages.Add(new($"0x{0x400 + Messages.Count:X3}", "NewMessage", 8));
        _redoMessages.Clear();
        RefreshCommandState();
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        var message = SelectedMessage ?? Messages.LastOrDefault();
        if (message is null)
        {
            return;
        }

        Messages.Remove(message);
        _redoMessages.Push(message);
        SelectedMessage = Messages.LastOrDefault();
        RefreshCommandState();
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        var message = _redoMessages.Pop();
        Messages.Add(message);
        SelectedMessage = message;
        RefreshCommandState();
    }

    public void Clear()
    {
        Messages.Clear();
        _redoMessages.Clear();
        SelectedMessage = null;
        RefreshCommandState();
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
    public ObservableCollection<TxJobRow> Jobs { get; } =
    [
        new("0x301", "VCU_Control", "20 ms", "8", "AA BB CC 00 00 00 00 12", true, true, true),
        new("0x7DF", "OBD-II Req (Tester)", "Manual", "8", "02 01 0C 55 55 55 55 55", false, false, false)
    ];

    [RelayCommand]
    private void AddJob() => Jobs.Add(new("0x000", "New TX Job", "Manual", "8", "00 00 00 00 00 00 00 00", false, false, false));

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
