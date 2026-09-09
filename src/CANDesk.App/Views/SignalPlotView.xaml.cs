using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CANDesk.App.ViewModels;

namespace CANDesk.App.Views;

public partial class SignalPlotView : UserControl
{
    private readonly DispatcherTimer _redrawTimer;

    public SignalPlotView()
    {
        InitializeComponent();
        // Redraw on a fixed cadence rather than per decoded sample — the app already learned this
        // lesson twice this session (Message Monitor row-details, TX processing lag): redrawing a
        // chart per incoming frame at bus speed would visibly stall the UI.
        //
        // Priority is Render, not Background: RX frame delivery now runs at DispatcherPriority.Normal
        // (fixed earlier this session — Background was exactly the bug that made Monitor/Trace feel
        // laggy) and, while connected, keeps the dispatcher queue continuously busy with Normal-
        // priority work. A Background-priority timer sits below Normal and would get starved
        // indefinitely by that traffic, so this Tick callback would rarely-to-never actually run —
        // which is exactly why the plot never drew anything despite the view model collecting data
        // correctly. Render sits above Background (and below Normal), so it isn't starved by RX
        // processing but still stays out of the way of higher-priority UI work.
        _redrawTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(100) };
        _redrawTimer.Tick += (_, _) => RedrawIfRunning();
        _redrawTimer.Start();
        Unloaded += (_, _) => _redrawTimer.Stop();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is SignalPlotViewModel oldViewModel)
        {
            oldViewModel.Cleared -= OnCleared;
        }

        if (e.NewValue is SignalPlotViewModel newViewModel)
        {
            newViewModel.Cleared += OnCleared;
        }
    }

    // Clear must repaint immediately even while stopped — otherwise the buffers are wiped but the
    // on-screen chart (frozen on purpose so manual zoom/pan survives) never gets told to catch up,
    // which looks exactly like the Clear button doing nothing.
    private void OnCleared(object? sender, EventArgs e)
    {
        _yAxisInitializedLabels.Clear();
        Redraw();
    }

    private void RedrawIfRunning()
    {
        if (DataContext is not SignalPlotViewModel viewModel || !viewModel.IsRunning)
        {
            // Stopped: skip Clear+AutoScale entirely so the user's manual zoom/pan (ScottPlot's
            // built-in mouse interaction) is left completely undisturbed. Resuming Start picks the
            // redraw back up on the next tick.
            return;
        }

        Redraw();
    }

    // One color/axis pair per plotted series (max is SignalPlotViewModel.MaxSelectedSignals = 4).
    // Giving every series its own Y-axis is what actually fixes the "small-unit signal looks flat"
    // problem a shared axis has: a 0/1 relay state or a °C temperature next to an rpm signal would
    // otherwise be squashed to a barely-visible line by the rpm signal's much larger range.
    private static readonly ScottPlot.Color[] SeriesColors =
    [
        ScottPlot.Colors.SteelBlue, ScottPlot.Colors.OrangeRed, ScottPlot.Colors.SeaGreen, ScottPlot.Colors.MediumPurple,
    ];

    // Axes beyond the plot's built-in Left axis (one per series past the first). Plot.Plot.Clear()
    // only clears plottables, not axes added via AddLeftAxis/AddRightAxis — calling those every
    // redraw tick (every 100ms while running) was creating a brand new axis each time and never
    // removing the old ones, so the plot accumulated more axes forever. These are now created once
    // and reused across every redraw; unused ones are hidden rather than recreated or left dangling.
    private readonly List<ScottPlot.IYAxis> _extraYAxes = [];

    // Like an oscilloscope's per-channel volts/div: each Y-axis gets an initial fit once its
    // signal's data first appears, then stays exactly where it is — including wherever the user
    // has manually panned/zoomed it — no matter how much new data streams in afterward. Re-fitting
    // from scratch on every 100ms redraw tick (the previous behavior) fought any manual adjustment
    // made while the plot was running and undid it within one tick, which is exactly what made
    // "user moved the axis" look like "keeps resetting itself". A fresh Clear/Start (see
    // OnCleared) is what resets this and re-arms the initial fit. The shared time (X) axis is
    // deliberately NOT tracked here — see the AutoScaleX() call in Redraw() for why it keeps
    // flowing every tick instead.
    private readonly HashSet<string> _yAxisInitializedLabels = [];

    private ScottPlot.IYAxis GetExtraYAxis(int extraIndex)
    {
        while (_extraYAxes.Count <= extraIndex)
        {
            ScottPlot.IYAxis axis = _extraYAxes.Count % 2 == 0 ? Plot.Plot.Axes.AddRightAxis() : Plot.Plot.Axes.AddLeftAxis();
            _extraYAxes.Add(axis);
        }

        return _extraYAxes[extraIndex];
    }

    private void Redraw()
    {
        if (DataContext is not SignalPlotViewModel viewModel)
        {
            return;
        }

        var series = viewModel.GetSelectedSeriesSnapshot();
        Plot.Plot.Clear();

        foreach (var axis in _extraYAxes)
        {
            axis.IsVisible = false;
        }

        var hasAnyData = false;
        for (var i = 0; i < series.Count; i++)
        {
            var s = series[i];
            if (s.Data.Times.Length == 0)
            {
                continue;
            }

            var color = SeriesColors[i % SeriesColors.Length];
            ScottPlot.IYAxis yAxis;
            if (i == 0)
            {
                yAxis = Plot.Plot.Axes.Left;
            }
            else
            {
                yAxis = GetExtraYAxis(i - 1);
                yAxis.IsVisible = true;
            }

            yAxis.Label.Text = s.Label;
            yAxis.Label.ForeColor = color;
            yAxis.FrameLineStyle.Color = color;
            yAxis.TickLabelStyle.ForeColor = color;

            var scatter = Plot.Plot.Add.Scatter(s.Data.Times, s.Data.Values);
            scatter.LegendText = s.Label;
            scatter.Color = color;
            scatter.Axes.YAxis = yAxis;

            // Fit this axis once, the first time this signal has data — never again afterward, so
            // it doesn't fight a manual pan/zoom the user makes while the plot keeps running.
            if (_yAxisInitializedLabels.Add(s.Label))
            {
                SetYAxisLimits(yAxis, s.Data.Values);
            }

            hasAnyData = true;
        }

        // Unlike the Y axes, the shared time axis keeps flowing every tick while running — that's
        // the live-scrolling "roll mode" an oscilloscope's time axis behaves like, always showing
        // the latest window of data. Y axes are different: each one is fit once and then left where
        // the user put it (see the per-signal check above), because a signal's vertical scale is a
        // deliberate per-channel setting (volts/div) that shouldn't jump around, whereas the time
        // axis is expected to keep advancing. AutoScaleX() only touches X — it does NOT
        // recompute the Y axes (that was ScottPlot's plain, no-argument AutoScale(), which this
        // used to call and which flattened every signal but the largest-range one; see the
        // multi-axis scaling fix above).
        if (hasAnyData)
        {
            Plot.Plot.Axes.AutoScaleX();
        }

        Plot.Refresh();
    }

    // ScottPlot's generic AutoScale() computes one Y range and applies it to every axis it touches,
    // not a range per axis — so with several signals sharing the plot, whichever one has the
    // largest swing (or whichever axis is visited first) decided the range for everyone else too,
    // and a constant/near-constant signal collapsed to a razor-thin band around its own value. Each
    // axis now gets its limits set explicitly from just its own series' min/max, with a fixed
    // fallback range when a series hasn't varied yet (or ever) so it renders as a centered flat
    // line instead of a degenerate zero-height range.
    private void SetYAxisLimits(ScottPlot.IYAxis yAxis, double[] values)
    {
        if (values.Length == 0)
        {
            return;
        }

        var min = values[0];
        var max = values[0];
        foreach (var value in values)
        {
            if (value < min) min = value;
            if (value > max) max = value;
        }

        if (min == max)
        {
            var pad = min == 0 ? 1 : Math.Abs(min) * 0.1;
            min -= pad;
            max += pad;
        }
        else
        {
            var margin = (max - min) * 0.1;
            min -= margin;
            max += margin;
        }

        Plot.Plot.Axes.SetLimitsY(min, max, yAxis);
    }
}
