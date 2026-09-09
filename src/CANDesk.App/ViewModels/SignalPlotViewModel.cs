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
/// Backs the Signal Plot tab: lets the user pick up to <see cref="MaxSelectedSignals"/> decoded
/// signals from the loaded DBC and keeps a rolling time-series buffer (seconds since the plot was
/// last cleared, on the X axis) for each. <see cref="SignalPlotView"/> redraws from these buffers
/// on its own timer rather than per frame — the same "coalesce, don't redraw per event" lesson this
/// session already re-learned twice (Message Monitor row-details, TX processing lag).
/// </summary>
public sealed partial class SignalPlotViewModel : ObservableObject
{
    public const int MaxSelectedSignals = 4;

    /// <summary>Samples older than this relative to the newest one are dropped so a long-running
    /// plot doesn't grow its buffers (and redraw cost) without bound — the X axis keeps flowing
    /// (see SignalPlotView.Redraw's AutoScaleX), so anything older than this just scrolls off the
    /// left edge rather than staying visible.</summary>
    private static readonly TimeSpan RetentionWindow = TimeSpan.FromSeconds(60);

    private readonly Dictionary<(uint CanId, string SignalName), PlotSeries> _seriesByKey = [];
    private IMessageDatabase? _database;
    private SignalDecoder? _decoder;
    private DateTime? _plotStartUtc;

    public ObservableCollection<PlotSignalOption> AvailableSignals { get; } = [];

    /// <summary>Filtered view of <see cref="AvailableSignals"/> the picker binds to. Large DBCs can
    /// carry thousands of signals, far more than a flat scrollable checkbox list is comfortable to
    /// search through — see <see cref="FilterText"/>.</summary>
    public ICollectionView AvailableSignalsView { get; }

    [ObservableProperty]
    private string _filterText = string.Empty;

    public SignalPlotViewModel()
    {
        AvailableSignalsView = CollectionViewSource.GetDefaultView(AvailableSignals);
        AvailableSignalsView.Filter = Matches;
    }

    partial void OnFilterTextChanged(string value) => AvailableSignalsView.Refresh();

    private bool Matches(object item)
    {
        if (item is not PlotSignalOption option)
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(FilterText)
            || option.Label.Contains(FilterText.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Gates both new-sample capture (<see cref="Apply"/>) and the view's redraw loop.
    /// Defaults to false: the plot stays idle until the user explicitly presses Start, rather than
    /// silently capturing before they've picked signals / positioned the view. Stopping freezes the
    /// chart in place — no more incoming samples, no more periodic Clear+Autoscale — so the user can
    /// zoom/pan with the mouse without it snapping back on the next redraw tick. Starting again
    /// clears the previous buffers first (see <see cref="Start"/>) and begins a fresh plot, matching
    /// <see cref="TraceLogViewModel"/>'s Stop-ends-the-session / Start-begins-fresh convention.</summary>
    [ObservableProperty]
    private bool _isRunning;

    public void SetDatabase(IMessageDatabase? database)
    {
        foreach (var option in AvailableSignals)
        {
            option.PropertyChanged -= OnSignalOptionPropertyChanged;
        }

        _database = database;
        _decoder = database is not null ? new SignalDecoder(database) : null;

        AvailableSignals.Clear();
        _seriesByKey.Clear();
        _plotStartUtc = null;

        if (database is null)
        {
            return;
        }

        foreach (var message in database.Messages)
        {
            foreach (var signal in message.Signals)
            {
                var option = new PlotSignalOption(message.CanId, message.Name, signal.Name, signal.Unit);
                option.PropertyChanged += OnSignalOptionPropertyChanged;
                AvailableSignals.Add(option);
            }
        }
    }

    public void ProcessFrames(IReadOnlyList<CanFrame> frames)
    {
        foreach (var frame in frames)
        {
            Apply(frame);
        }
    }

    public void ProcessTransmittedFrame(in CanFrame frame) => Apply(frame);

    private void Apply(in CanFrame frame)
    {
        if (!IsRunning || _seriesByKey.Count == 0 || _decoder is null || _database is null || !_database.TryGetMessage(frame.Id, out _))
        {
            return;
        }

        DateTime? sampleAtUtc = null;
        foreach (var signal in _decoder.Decode(frame))
        {
            var key = (frame.Id, signal.SignalName);
            if (!_seriesByKey.TryGetValue(key, out var series))
            {
                continue; // not selected for plotting
            }

            _plotStartUtc ??= signal.SystemTime;
            sampleAtUtc ??= signal.SystemTime;
            var seconds = (sampleAtUtc.Value - _plotStartUtc.Value).TotalSeconds;
            series.Add(seconds, signal.PhysicalValue);
        }

        if (sampleAtUtc is not null)
        {
            PruneOldSamples(sampleAtUtc.Value);
        }
    }

    // Batched rather than run on every single incoming frame: at bus speed (or 1kHz+ CAN-FD
    // traffic) that would mean an O(n) FindIndex+RemoveRange per series on every frame just to
    // trim a couple of stale samples off the front. A ~1s cadence is plenty responsive for a 2-
    // minute retention window and cuts the pruning cost by orders of magnitude under sustained load.
    private static readonly TimeSpan PruneInterval = TimeSpan.FromSeconds(1);
    private DateTime? _lastPruneUtc;

    private void PruneOldSamples(DateTime latestSampleUtc)
    {
        if (_lastPruneUtc is not null && latestSampleUtc - _lastPruneUtc.Value < PruneInterval)
        {
            return;
        }

        _lastPruneUtc = latestSampleUtc;

        var cutoffSeconds = (latestSampleUtc - _plotStartUtc!.Value).TotalSeconds - RetentionWindow.TotalSeconds;
        if (cutoffSeconds <= 0)
        {
            return;
        }

        foreach (var series in _seriesByKey.Values)
        {
            series.PruneBefore(cutoffSeconds);
        }
    }

    /// <summary>Snapshot of every currently-selected signal's buffer, for the view to redraw from.
    /// Returns fresh arrays (not live references) so the view can read them off its own timer
    /// without racing frame processing.</summary>
    public IReadOnlyList<PlotSeriesSnapshot> GetSelectedSeriesSnapshot()
    {
        var snapshots = new List<PlotSeriesSnapshot>(_seriesByKey.Count);
        foreach (var option in AvailableSignals)
        {
            if (!option.IsSelected)
            {
                continue;
            }

            if (_seriesByKey.TryGetValue((option.CanId, option.SignalName), out var series))
            {
                snapshots.Add(new PlotSeriesSnapshot(option.Label, series.ToArrays()));
            }
        }

        return snapshots;
    }

    /// <summary>Raised whenever <see cref="Clear"/> runs. The view listens for this to force an
    /// immediate redraw even while <see cref="IsRunning"/> is false — the redraw timer otherwise
    /// skips touching the chart while stopped (that's what keeps manual zoom/pan from resetting),
    /// which meant Clear looked like it did nothing when pressed while stopped: the buffers were
    /// wiped but the on-screen chart never got told to catch up.</summary>
    public event EventHandler? Cleared;

    [RelayCommand]
    private void Clear()
    {
        foreach (var series in _seriesByKey.Values)
        {
            series.Clear();
        }

        _plotStartUtc = null;
        _lastPruneUtc = null;
        Cleared?.Invoke(this, EventArgs.Empty);
    }

    // Mirrors TraceLogViewModel's Start/Stop semantics: Stop ends the session, so restarting
    // begins a fresh plot (X axis back to 0) rather than resuming mid-buffer.
    [RelayCommand]
    private void Start()
    {
        Clear();
        IsRunning = true;
    }

    [RelayCommand]
    private void Stop() => IsRunning = false;

    /// <summary>Enforces <see cref="MaxSelectedSignals"/> and keeps <see cref="_seriesByKey"/> in
    /// sync with the picker. Reverting a rejected selection (setting IsSelected back to false)
    /// re-enters this handler once more, harmlessly hitting the "unselect" branch below.</summary>
    private void OnSignalOptionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PlotSignalOption.IsSelected) || sender is not PlotSignalOption option)
        {
            return;
        }

        if (option.IsSelected)
        {
            if (AvailableSignals.Count(o => o.IsSelected) > MaxSelectedSignals)
            {
                option.IsSelected = false;
                return;
            }

            _seriesByKey[(option.CanId, option.SignalName)] = new PlotSeries();
        }
        else
        {
            _seriesByKey.Remove((option.CanId, option.SignalName));
        }
    }

    private sealed class PlotSeries
    {
        private readonly List<double> _times = [];
        private readonly List<double> _values = [];

        public void Add(double seconds, double value)
        {
            _times.Add(seconds);
            _values.Add(value);
        }

        public void PruneBefore(double cutoffSeconds)
        {
            var firstKeptIndex = _times.FindIndex(t => t >= cutoffSeconds);
            if (firstKeptIndex <= 0)
            {
                return;
            }

            _times.RemoveRange(0, firstKeptIndex);
            _values.RemoveRange(0, firstKeptIndex);
        }

        public void Clear()
        {
            _times.Clear();
            _values.Clear();
        }

        public (double[] Times, double[] Values) ToArrays() => (_times.ToArray(), _values.ToArray());
    }
}

/// <summary>One selectable signal in the picker list. <see cref="SignalPlotViewModel"/> enforces
/// the max-selection limit by watching <see cref="IsSelected"/>'s PropertyChanged and reverting it
/// when rejected.</summary>
public sealed partial class PlotSignalOption(uint canId, string messageName, string signalName, string? unit) : ObservableObject
{
    public uint CanId { get; } = canId;
    public string MessageName { get; } = messageName;
    public string SignalName { get; } = signalName;
    public string Label { get; } = string.IsNullOrEmpty(unit) ? $"{messageName}.{signalName}" : $"{messageName}.{signalName} ({unit})";

    [ObservableProperty]
    private bool _isSelected;
}

public sealed record PlotSeriesSnapshot(string Label, (double[] Times, double[] Values) Data);
