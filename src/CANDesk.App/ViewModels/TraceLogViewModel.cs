using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Media;
using CANDesk.Hal;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CANDesk.App.ViewModels;

/// <summary>Recording state for the trace log, independent of the global connection-level capture toggle.</summary>
public enum TraceCaptureStatus { Running, Paused, Stopped }

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
    // Trace recording starts off; the user opts in via Start rather than logging by default.
    [ObservableProperty] private TraceCaptureStatus _captureStatus = TraceCaptureStatus.Stopped;

    public bool CanStart => CaptureStatus != TraceCaptureStatus.Running;
    public bool CanPause => CaptureStatus == TraceCaptureStatus.Running;
    public bool CanStop => CaptureStatus != TraceCaptureStatus.Stopped;

    public string CaptureStatusText => CaptureStatus switch
    {
        TraceCaptureStatus.Running => "● Recording",
        TraceCaptureStatus.Paused => "❚❚ Paused",
        _ => "■ Stopped"
    };

    public Brush CaptureStatusBrush => CaptureStatus switch
    {
        TraceCaptureStatus.Running => Brushes.SeaGreen,
        TraceCaptureStatus.Paused => Brushes.DarkOrange,
        _ => Brushes.SlateGray
    };

    public TraceLogViewModel()
    {
        FramesView = CollectionViewSource.GetDefaultView(Frames);
        FramesView.Filter = Matches;
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void Start() => CaptureStatus = TraceCaptureStatus.Running;

    [RelayCommand(CanExecute = nameof(CanPause))]
    private void Pause() => CaptureStatus = TraceCaptureStatus.Paused;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop() => CaptureStatus = TraceCaptureStatus.Stopped;

    partial void OnCaptureStatusChanged(TraceCaptureStatus value)
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(CaptureStatusText));
        OnPropertyChanged(nameof(CaptureStatusBrush));
        StartCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    public void ProcessFrames(IReadOnlyList<CanFrame> frames)
    {
        foreach (var frame in frames)
        {
            Append(frame, frame.Flags.HasFlag(CanFrameFlags.ErrorFrame) ? "ERR" : "RX");
        }

        TrimToCapacity();
    }

    public void ProcessTransmittedFrame(in CanFrame frame)
    {
        Append(frame, "TX");
        TrimToCapacity();
    }

    private void Append(in CanFrame frame, string direction)
    {
        if (CaptureStatus != TraceCaptureStatus.Running) return;

        var bytes = Convert.ToHexString(frame.PayloadSpan).Chunk(2).Select(static value => new string(value)).ToArray();
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

    private void TrimToCapacity()
    {
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
