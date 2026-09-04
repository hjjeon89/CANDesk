using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CANDesk.Hal;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CANDesk.App.ViewModels;

/// <summary>Append-only trace state, filters, and selected-frame inspection data.</summary>
public sealed partial class TraceLogViewModel : ObservableObject
{
    private long _sequence;

    public ObservableCollection<TraceFrameRow> Frames { get; } = [];
    public ICollectionView FramesView { get; }

    [ObservableProperty] private bool _showRx = true;
    [ObservableProperty] private bool _showTx = true;
    [ObservableProperty] private bool _errorsOnly;
    [ObservableProperty] private string _filterText = string.Empty;
    [ObservableProperty] private TraceFrameRow _selectedFrame = TraceFrameRow.Empty;

    public TraceLogViewModel()
    {
        FramesView = CollectionViewSource.GetDefaultView(Frames);
        FramesView.Filter = Matches;
    }

    public void ProcessFrames(IReadOnlyList<CanFrame> frames)
    {
        foreach (var frame in frames)
        {
            var bytes = Convert.ToHexString(frame.PayloadSpan).Chunk(2).Select(static value => new string(value)).ToArray();
            var direction = frame.Flags.HasFlag(CanFrameFlags.ErrorFrame) ? "ERR" : "RX";
            var row = new TraceFrameRow(
                Interlocked.Increment(ref _sequence),
                frame.SystemTime,
                $"0x{frame.Id:X3}",
                direction,
                frame.Dlc,
                string.Join(" ", bytes),
                "Raw frame",
                bytes);
            Frames.Add(row);
            SelectedFrame = row;
        }

        while (Frames.Count > 10_000)
        {
            Frames.RemoveAt(0);
        }
    }

    public void Clear()
    {
        Frames.Clear();
        SelectedFrame = TraceFrameRow.Empty;
    }

    partial void OnFilterTextChanged(string value) => FramesView.Refresh();
    partial void OnShowRxChanged(bool value) => FramesView.Refresh();
    partial void OnShowTxChanged(bool value) => FramesView.Refresh();
    partial void OnErrorsOnlyChanged(bool value) => FramesView.Refresh();

    private bool Matches(object item)
    {
        if (item is not TraceFrameRow frame)
        {
            return false;
        }

        if (ErrorsOnly && frame.Direction != "ERR")
        {
            return false;
        }

        if (frame.Direction == "RX" && !ShowRx || frame.Direction == "TX" && !ShowTx)
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(FilterText)
            || frame.Id.Contains(FilterText.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
