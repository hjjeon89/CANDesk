using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CANDesk.App.ViewModels;

public sealed partial class DbcSignalTreeViewModel : ObservableObject
{
    public ObservableCollection<MessageTreeItem> Messages { get; } =
    [
        new("0x100", "EngineStatus", "10 ms | DLC: 8", [new("EngineSpeed", "2,450 rpm"), new("CoolantTemp", "87.5 C"), new("EngineState", "Running")]),
        new("0x200", "BatteryPackStatus", "50 ms | DLC: 8", [new("PackVoltage", "398.2 V"), new("PackCurrent", "-24.5 A"), new("StateOfCharge", "78 %")]),
        new("0x301", "VCU_Control", "Cyclic | DLC: 8", [new("TorqueRequest", "120 Nm"), new("RollingCounter", "0")])
    ];

    [ObservableProperty]
    private string _searchText = string.Empty;
}

public sealed partial class MessageDbEditorViewModel : ObservableObject
{
    public ObservableCollection<EditableMessageRow> Messages { get; } = [];

    [RelayCommand]
    private void AddMessage() => Messages.Add(new($"0x{0x400 + Messages.Count:X3}", "NewMessage", 8));
}

public sealed partial class TransmitPanelViewModel : ObservableObject
{
    public ObservableCollection<TxJobRow> Jobs { get; } =
    [
        new("0x301", "VCU_Control", "20 ms", "8", "AA BB CC 00 00 00 00 12", true, true, true),
        new("0x7DF", "OBD-II Req (Tester)", "Manual", "8", "02 01 0C 55 55 55 55 55", false, false, false)
    ];

    [RelayCommand]
    private void AddJob() => Jobs.Add(new("0x000", "New TX Job", "Manual", "8", "00 00 00 00 00 00 00 00", false, false, false));
}

public sealed record EditableMessageRow(string Id, string Name, int Dlc);
