using System.Windows.Controls;
using System.Windows.Input;

namespace CANDesk.App.Views;

public partial class TransmitPanelView : UserControl
{
    public TransmitPanelView() => InitializeComponent();

    private void OnSignalValueKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        ((TextBox)sender).GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        e.Handled = true;
    }
}
