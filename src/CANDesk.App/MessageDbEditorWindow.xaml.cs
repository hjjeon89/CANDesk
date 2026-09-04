using System.Windows;
using CANDesk.App.ViewModels;

namespace CANDesk.App;

public partial class MessageDbEditorWindow : Window
{
    public MessageDbEditorWindow(MessageDbEditorViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
