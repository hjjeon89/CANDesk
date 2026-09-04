using System.Windows;
namespace CANDesk.App;
public partial class MainWindow : Window { public MainWindow(MainViewModel viewModel) { InitializeComponent(); DataContext = viewModel; } }
