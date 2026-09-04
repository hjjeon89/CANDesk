using CANDesk.Hal;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CANDesk.App;

/// <summary>
/// Connection settings are kept independently from trace and transmit state so a user
/// can switch Classic/FD mode and timing up to the moment a channel is opened.
/// </summary>
public sealed partial class DeviceConnectionViewModel : ObservableObject
{
    private readonly IBitrateTableProvider _bitrateTable;
    public IReadOnlyList<string> Vendors { get; } = ["Mock (Loopback)", "PEAK", "Kvaser", "Vector", "CANable"];
    public IReadOnlyList<string> Channels { get; } = ["Mock Channel 0", "Channel 1", "Channel 2"];
    public IReadOnlyList<BitTimingSetting> NominalPresets { get; }
    public IReadOnlyList<BitTimingSetting> DataPresets { get; }

    [ObservableProperty] private string _selectedVendor = "Mock (Loopback)";
    [ObservableProperty] private string _selectedChannel = "Mock Channel 0";
    [ObservableProperty] private CanBusMode _mode = CanBusMode.Classic;
    [ObservableProperty] private BitTimingSetting _selectedNominalTiming;
    [ObservableProperty] private BitTimingSetting _selectedDataTiming;
    [ObservableProperty] private bool _useCustomNominalTiming;
    [ObservableProperty] private bool _useCustomDataTiming;
    [ObservableProperty] private string _customNominalBitrateKbps = "500";
    [ObservableProperty] private string _customNominalSamplePoint = "87.5";
    [ObservableProperty] private string _customDataBitrateKbps = "2000";
    [ObservableProperty] private string _customDataSamplePoint = "80";

    public bool IsClassicMode
    {
        get => Mode == CanBusMode.Classic;
        set { if (value) Mode = CanBusMode.Classic; }
    }
    public bool IsFdMode
    {
        get => Mode == CanBusMode.Fd;
        set { if (value) Mode = CanBusMode.Fd; }
    }

    public DeviceConnectionViewModel(IBitrateTableProvider bitrateTable)
    {
        _bitrateTable = bitrateTable;
        NominalPresets = bitrateTable.GetNominalPresets();
        DataPresets = bitrateTable.GetFdDataPhasePresets();
        _selectedNominalTiming = NominalPresets.Single(timing => timing.BitrateKbps == 500);
        _selectedDataTiming = DataPresets.Single(timing => timing.BitrateKbps == 2_000);
    }

    public CanBusConfig BuildConfiguration()
    {
        var nominal = UseCustomNominalTiming
            ? CreateCustomTiming(CustomNominalBitrateKbps, CustomNominalSamplePoint)
            : SelectedNominalTiming;
        var data = Mode == CanBusMode.Fd
            ? UseCustomDataTiming
                ? CreateCustomTiming(CustomDataBitrateKbps, CustomDataSamplePoint)
                : SelectedDataTiming
            : null;
        return new CanBusConfig { Mode = Mode, Nominal = nominal, Data = data };
    }

    partial void OnModeChanged(CanBusMode value)
    {
        OnPropertyChanged(nameof(IsClassicMode));
        OnPropertyChanged(nameof(IsFdMode));
    }

    private BitTimingSetting CreateCustomTiming(string bitrateText, string samplePointText)
    {
        if (!int.TryParse(bitrateText, out var bitrate) || bitrate <= 0)
            throw new InvalidOperationException("Bitrate must be a positive integer in kbps.");
        if (!double.TryParse(samplePointText, out var samplePoint) || samplePoint is <= 0 or > 100)
            throw new InvalidOperationException("Sample point must be greater than 0 and no more than 100 percent.");
        var custom = new BitTimingSetting(bitrate, samplePoint, true);
        _bitrateTable.RegisterCustomPreset(custom);
        return custom;
    }
}
