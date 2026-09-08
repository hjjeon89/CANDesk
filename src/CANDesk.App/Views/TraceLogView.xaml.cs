using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CANDesk.App.ViewModels;

namespace CANDesk.App.Views;

public partial class TraceLogView : UserControl
{
    private bool _scrollPending;

    public TraceLogView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is TraceLogViewModel oldViewModel)
        {
            oldViewModel.FramesView.CollectionChanged -= OnFramesViewChanged;
        }

        if (e.NewValue is TraceLogViewModel newViewModel)
        {
            newViewModel.FramesView.CollectionChanged += OnFramesViewChanged;
        }
    }

    // Scrolls to the newest visible row as frames arrive, unless the user turned Auto Scroll off
    // (e.g. to inspect an older frame without the grid yanking the view away). Filtered-out frames
    // never reach FramesView, so this only reacts to what's actually shown.
    //
    // The scroll itself is deferred to a later dispatcher pass rather than done inline here: this
    // handler runs synchronously as part of FramesView.CollectionChanged, the same event the
    // DataGrid's own ItemContainerGenerator is subscribed to, and calling Items/ScrollIntoView
    // before the grid has finished reacting to the same change throws "ItemsControl does not
    // match its items source" (System.InvalidOperationException). The _scrollPending guard
    // coalesces bursts of Add notifications within one dispatch cycle into a single scroll.
    private void OnFramesViewChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add)
        {
            return;
        }

        if (DataContext is not TraceLogViewModel viewModel || !viewModel.IsAutoScroll)
        {
            return;
        }

        if (_scrollPending)
        {
            return;
        }

        _scrollPending = true;
        Dispatcher.BeginInvoke(() =>
        {
            _scrollPending = false;
            if (TraceGrid.Items.Count > 0)
            {
                TraceGrid.ScrollIntoView(TraceGrid.Items[^1]);
            }
        }, DispatcherPriority.Background);
    }
}
