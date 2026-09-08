using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CANDesk.Core.Dispatch;
using CANDesk.Core.MessageDb;
using CANDesk.Hal;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CANDesk.App.ViewModels;

/// <summary>Maintains the latest frame per CAN ID; unlike Trace this never appends duplicate IDs.</summary>
public sealed partial class MessageMonitorViewModel : ObservableObject
{
    private readonly Dictionary<uint, MessageMonitorRow> _rowsById = [];
    private IMessageDatabase? _database;
    private SignalDecoder? _decoder;
    public ObservableCollection<MessageMonitorRow> Rows { get; } = [];
    public ICollectionView RowsView { get; }
    [ObservableProperty] private string _idFilter = string.Empty;
    [ObservableProperty] private bool _showRx = true;
    [ObservableProperty] private bool _showTx = true;
    [ObservableProperty] private bool _errorsOnly;

    public MessageMonitorViewModel()
    {
        RowsView = CollectionViewSource.GetDefaultView(Rows);
        RowsView.Filter = Matches;
    }

    /// <summary>Sets the message database used to decode <see cref="MessageMonitorRow.MessageName"/>
    /// and <see cref="MessageMonitorRow.Summary"/> as frames arrive. Pass null to fall back to
    /// "Unmapped"/no summary (e.g. before a DBC is loaded) — mirrors <c>TraceLogViewModel.SetDatabase</c>.</summary>
    public void SetDatabase(IMessageDatabase? database)
    {
        _database = database;
        _decoder = database is not null ? new SignalDecoder(database) : null;
    }

    /// <summary>Processes a whole RX batch (arrives every ~33ms from <c>RxDispatcher</c>) and raises
    /// each touched row's property-changed notifications only once at the end, no matter how many
    /// frames in the batch shared that CAN ID. Without this, a bursty ID fires a full round of
    /// PropertyChanged (Payload/Direction/IsError/Dlc/ReceiveCount/CycleTimeMs/LastReceived) per
    /// frame even though only the final state after the batch is ever visible on screen.</summary>
    public void ProcessFrames(IReadOnlyList<CanFrame> frames)
    {
        var touchedRows = new HashSet<MessageMonitorRow>();
        foreach (var frame in frames)
        {
            var row = GetOrAddRow(frame.Id);
            if (touchedRows.Add(row))
            {
                row.BeginBatchUpdate();
            }
            var (messageName, summary) = Describe(frame);
            row.Update(frame, "RX", messageName, summary);
        }

        foreach (var row in touchedRows)
        {
            row.EndBatchUpdate();
        }
    }

    public void ProcessTransmittedFrame(in CanFrame frame)
    {
        var (messageName, summary) = Describe(frame);
        GetOrAddRow(frame.Id).Update(frame, "TX", messageName, summary);
    }

    /// <summary>Decodes a frame's message name and "Signal=Value" summary the same way
    /// <c>TraceLogViewModel.Summarize</c> does. Returns (null, "") when no database is loaded or the
    /// ID isn't in it, leaving the row's existing "Unmapped" name untouched.</summary>
    private (string? MessageName, string Summary) Describe(in CanFrame frame)
    {
        if (_decoder is null || _database is null || !_database.TryGetMessage(frame.Id, out var message))
        {
            return (null, string.Empty);
        }

        var decoded = _decoder.Decode(frame);
        var summary = decoded.Count == 0
            ? string.Empty
            : string.Join(", ", decoded.Select(signal => $"{signal.SignalName}={signal.PhysicalValue:0.###}{signal.Unit}"));
        return (message.Name, summary);
    }

    [RelayCommand]
    public void Clear()
    {
        _rowsById.Clear();
        Rows.Clear();
    }

    private MessageMonitorRow GetOrAddRow(uint canId)
    {
        if (!_rowsById.TryGetValue(canId, out var row))
        {
            row = new MessageMonitorRow(canId);
            _rowsById.Add(canId, row);
            Rows.Add(row);
        }

        return row;
    }

    partial void OnIdFilterChanged(string value) => RowsView.Refresh();
    partial void OnShowRxChanged(bool value) => RowsView.Refresh();
    partial void OnShowTxChanged(bool value) => RowsView.Refresh();
    partial void OnErrorsOnlyChanged(bool value) => RowsView.Refresh();

    private bool Matches(object item)
    {
        if (item is not MessageMonitorRow row) return false;
        if (ErrorsOnly && !row.IsError) return false;
        if (row.Direction == "RX" && !ShowRx || row.Direction == "TX" && !ShowTx) return false;
        if (string.IsNullOrWhiteSpace(IdFilter)) return true;
        var text = IdFilter.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
        return uint.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out var id) && row.CanId == id;
    }
}

public sealed partial class MessageMonitorRow(uint canId) : ObservableObject
{
    public uint CanId { get; } = canId;
    public string Id { get; } = $"0x{canId:X3}";
    [ObservableProperty] private string _messageName = "Unmapped";
    [ObservableProperty] private string _payload = string.Empty;
    [ObservableProperty] private string _direction = "RX";
    [ObservableProperty] private bool _isError;
    [ObservableProperty] private byte _dlc;
    [ObservableProperty] private long _receiveCount;
    [ObservableProperty] private double? _cycleTimeMs;
    [ObservableProperty] private DateTime _lastReceived;
    /// <summary>"Signal=Value" decode, same format as Trace's Summary column; empty until a DBC is
    /// loaded and this ID is mapped in it.</summary>
    [ObservableProperty] private string _summary = string.Empty;

    private bool _suppressNotifications;

    /// <summary>Suppresses PropertyChanged while a batch of frames for this row is being applied;
    /// pair with <see cref="EndBatchUpdate"/> to raise one coalesced notification instead of one
    /// per touched property per frame.</summary>
    internal void BeginBatchUpdate() => _suppressNotifications = true;

    /// <summary>Ends suppression and raises a single "all properties changed" notification
    /// (empty property name, the standard WPF convention) reflecting the final state.</summary>
    internal void EndBatchUpdate()
    {
        _suppressNotifications = false;
        OnPropertyChanged(new PropertyChangedEventArgs(string.Empty));
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (_suppressNotifications)
        {
            return;
        }

        base.OnPropertyChanged(e);
    }

    public void Update(in CanFrame frame, string direction, string? messageName, string summary)
    {
        if (LastReceived != default) CycleTimeMs = (frame.SystemTime - LastReceived).TotalMilliseconds;
        LastReceived = frame.SystemTime;
        Dlc = frame.Dlc;
        Payload = string.Join(" ", Convert.ToHexString(frame.PayloadSpan).Chunk(2).Select(hex => new string(hex)));
        Direction = direction;
        IsError = frame.Flags.HasFlag(CanFrameFlags.ErrorFrame);
        ReceiveCount++;
        if (messageName is not null) MessageName = messageName;
        Summary = summary;
    }
}
