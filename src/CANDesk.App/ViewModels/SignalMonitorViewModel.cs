using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CANDesk.Core.Dispatch;
using CANDesk.Core.MessageDb;
using CANDesk.Hal;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CANDesk.App.ViewModels;

/// <summary>
/// Flat, one-row-per-decoded-signal live view — an alternative to Message Monitor's per-message
/// row with an inline "Signal=Value" summary. Deliberately kept in its own window rather than an
/// expandable per-message row-details sub-grid: an earlier attempt at that sub-grid design caused
/// visible RX processing lag (WPF DataGridRow row-details re-layout cost under live traffic), so
/// this trades the nested-grid UI for a single flat, virtualization-friendly DataGrid instead.
/// </summary>
public sealed partial class SignalMonitorViewModel : ObservableObject
{
    private readonly Dictionary<(uint CanId, string SignalName), SignalMonitorRow> _rowsByKey = [];
    private IMessageDatabase? _database;
    private SignalDecoder? _decoder;

    public ObservableCollection<SignalMonitorRow> Rows { get; } = [];
    public ICollectionView RowsView { get; }
    [ObservableProperty] private string _filterText = string.Empty;

    public SignalMonitorViewModel()
    {
        RowsView = CollectionViewSource.GetDefaultView(Rows);
        RowsView.Filter = Matches;
    }

    /// <summary>Sets the message database used to decode signals as frames arrive. Pass null to stop
    /// decoding (e.g. before a DBC is loaded) — mirrors <c>TraceLogViewModel.SetDatabase</c>. Frames
    /// for unmapped IDs, or arriving before any database is set, are silently skipped: unlike Message
    /// Monitor there is no per-message "Unmapped" placeholder row here, only decoded signals.</summary>
    public void SetDatabase(IMessageDatabase? database)
    {
        _database = database;
        _decoder = database is not null ? new SignalDecoder(database) : null;
    }

    /// <summary>Processes a whole RX batch and raises each touched row's property-changed
    /// notifications only once at the end, no matter how many frames in the batch touched that
    /// signal — same coalescing rationale as <c>MessageMonitorViewModel.ProcessFrames</c>.</summary>
    public void ProcessFrames(IReadOnlyList<CanFrame> frames)
    {
        var touchedRows = new HashSet<SignalMonitorRow>();
        foreach (var frame in frames)
        {
            Apply(frame, touchedRows);
        }

        foreach (var row in touchedRows)
        {
            row.EndBatchUpdate();
        }
    }

    public void ProcessTransmittedFrame(in CanFrame frame)
    {
        var touchedRows = new HashSet<SignalMonitorRow>();
        Apply(frame, touchedRows);
        foreach (var row in touchedRows)
        {
            row.EndBatchUpdate();
        }
    }

    private void Apply(in CanFrame frame, HashSet<SignalMonitorRow> touchedRows)
    {
        if (_decoder is null || _database is null || !_database.TryGetMessage(frame.Id, out var message))
        {
            return;
        }

        foreach (var signal in _decoder.Decode(frame))
        {
            var key = (frame.Id, signal.SignalName);
            if (!_rowsByKey.TryGetValue(key, out var row))
            {
                row = new SignalMonitorRow(frame.Id, message.Name, signal.SignalName);
                _rowsByKey.Add(key, row);
                Rows.Add(row);
            }

            if (touchedRows.Add(row))
            {
                row.BeginBatchUpdate();
            }

            row.Update(signal.PhysicalValue, signal.Unit ?? string.Empty, frame.SystemTime);
        }
    }

    [RelayCommand]
    public void Clear()
    {
        _rowsByKey.Clear();
        Rows.Clear();
    }

    partial void OnFilterTextChanged(string value) => RowsView.Refresh();

    private bool Matches(object item)
    {
        if (item is not SignalMonitorRow row)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(FilterText))
        {
            return true;
        }

        var text = FilterText.Trim();
        return row.SignalName.Contains(text, StringComparison.OrdinalIgnoreCase)
            || row.MessageName.Contains(text, StringComparison.OrdinalIgnoreCase)
            || row.Id.Contains(text, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed partial class SignalMonitorRow(uint canId, string messageName, string signalName) : ObservableObject
{
    public uint CanId { get; } = canId;
    public string Id { get; } = $"0x{canId:X3}";
    public string MessageName { get; } = messageName;
    public string SignalName { get; } = signalName;
    [ObservableProperty] private string _value = string.Empty;
    [ObservableProperty] private string _unit = string.Empty;
    [ObservableProperty] private DateTime _lastReceived;

    private bool _suppressNotifications;

    internal void BeginBatchUpdate() => _suppressNotifications = true;

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

    public void Update(double physicalValue, string unit, DateTime systemTime)
    {
        Value = physicalValue.ToString("0.###");
        Unit = unit;
        LastReceived = systemTime;
    }
}
