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

    private void Redraw()
    {
        if (DataContext is not SignalPlotViewModel viewModel)
        {
            return;
        }

        var series = viewModel.GetSelectedSeriesSnapshot();
        Plot.Plot.Clear();
        foreach (var s in series)
        {
            if (s.Data.Times.Length == 0)
            {
                continue;
            }

            var scatter = Plot.Plot.Add.Scatter(s.Data.Times, s.Data.Values);
            scatter.LegendText = s.Label;
        }

        Plot.Plot.Axes.AutoScale();
        Plot.Refresh();
    }
}
