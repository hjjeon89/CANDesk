using System.Windows;
using CANDesk.App.ViewModels;

namespace CANDesk.App;

public partial class SignalMonitorWindow : Window
{
    public SignalMonitorWindow(SignalMonitorViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
