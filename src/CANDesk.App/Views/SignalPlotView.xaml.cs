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
    private void OnCleared(object? sender, EventArgs e) => Redraw();

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

    private void Redraw()
    {
        if (DataContext is not SignalPlotViewModel viewModel)
        {
            return;
        }

        var series = viewModel.GetSelectedSeriesSnapshot();
        Plot.Plot.Clear();

        for (var i = 0; i < series.Count; i++)
        {
            var s = series[i];
            if (s.Data.Times.Length == 0)
            {
                continue;
            }

            var color = SeriesColors[i % SeriesColors.Length];
            var yAxis = i == 0 ? Plot.Plot.Axes.Left : (i % 2 == 1 ? Plot.Plot.Axes.AddRightAxis() : Plot.Plot.Axes.AddLeftAxis());
            yAxis.Label.Text = s.Label;
            yAxis.Label.ForeColor = color;
            yAxis.FrameLineStyle.Color = color;
            yAxis.TickLabelStyle.ForeColor = color;

            var scatter = Plot.Plot.Add.Scatter(s.Data.Times, s.Data.Values);
            scatter.LegendText = s.Label;
            scatter.Color = color;
            scatter.Axes.YAxis = yAxis;
        }

        Plot.Plot.Axes.AutoScale();
        Plot.Refresh();
    }
}
