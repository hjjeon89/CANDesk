using System.Windows;

namespace CANDesk.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private MessageDbEditorWindow? _messageEditorWindow;

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        _viewModel.MessageEditorRequested += OnMessageEditorRequested;
        Closed += (_, _) => _viewModel.MessageEditorRequested -= OnMessageEditorRequested;
    }

    private void OnMessageEditorRequested(object? sender, EventArgs args)
    {
        if (_messageEditorWindow is { IsVisible: true } window)
        {
            if (window.WindowState == WindowState.Minimized)
            {
                window.WindowState = WindowState.Normal;
            }
            window.Activate();
            return;
        }

        _messageEditorWindow = new MessageDbEditorWindow(_viewModel.MessageDbEditor) { Owner = this };
        _messageEditorWindow.Closed += (_, _) => _messageEditorWindow = null;
        _messageEditorWindow.Show();
    }
}
