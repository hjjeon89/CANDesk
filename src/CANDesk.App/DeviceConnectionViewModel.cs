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
    private readonly IBitTimingCalculator _timingCalculator;
    private const int DefaultControllerClockHz = 80_000_000;
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
    [ObservableProperty] private bool _useRawNominalTiming;
    [ObservableProperty] private bool _useRawDataTiming;
    [ObservableProperty] private string _nominalPrescaler = "10";
    [ObservableProperty] private string _nominalTseg1 = "13";
    [ObservableProperty] private string _nominalTseg2 = "2";
    [ObservableProperty] private string _nominalSjw = "2";
    [ObservableProperty] private string _dataPrescaler = "2";
    [ObservableProperty] private string _dataTseg1 = "15";
    [ObservableProperty] private string _dataTseg2 = "4";
    [ObservableProperty] private string _dataSjw = "4";
    [ObservableProperty] private string _nominalRawDescription = string.Empty;
    [ObservableProperty] private string _dataRawDescription = string.Empty;

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

    public DeviceConnectionViewModel(IBitrateTableProvider bitrateTable, IBitTimingCalculator timingCalculator)
    {
        _bitrateTable = bitrateTable;
        _timingCalculator = timingCalculator;
        NominalPresets = bitrateTable.GetNominalPresets();
        DataPresets = bitrateTable.GetFdDataPhasePresets();
        _selectedNominalTiming = NominalPresets.Single(timing => timing.BitrateKbps == 500);
        _selectedDataTiming = DataPresets.Single(timing => timing.BitrateKbps == 2_000);
        SyncNominalRawFromPreset(_selectedNominalTiming);
        SyncDataRawFromPreset(_selectedDataTiming);
        RefreshRawDescriptions();
    }

    public CanBusConfig BuildConfiguration()
    {
        var nominalPreset = UseCustomNominalTiming
            ? CreateCustomTiming(CustomNominalBitrateKbps, CustomNominalSamplePoint)
            : SelectedNominalTiming;
        var dataPreset = Mode == CanBusMode.Fd
            ? UseCustomDataTiming
                ? CreateCustomTiming(CustomDataBitrateKbps, CustomDataSamplePoint)
            : SelectedDataTiming
            : null;
        var nominalRaw = CreateRawSegments(NominalPrescaler, NominalTseg1, NominalTseg2, NominalSjw);
        var dataRaw = CreateRawSegments(DataPrescaler, DataTseg1, DataTseg2, DataSjw);
        var nominalTiming = UseRawNominalTiming
            ? new BitTimingConfig { InputMode = BitTimingInputMode.RawSegments, Preset = null, RawSegments = nominalRaw }
            : new BitTimingConfig { Preset = nominalPreset };
        var dataTiming = Mode == CanBusMode.Fd
            ? UseRawDataTiming
                ? new BitTimingConfig { InputMode = BitTimingInputMode.RawSegments, Preset = null, RawSegments = dataRaw }
                : new BitTimingConfig { Preset = dataPreset }
            : null;
        var nominal = UseRawNominalTiming ? DescribeAsSetting(nominalRaw) : nominalPreset;
        var data = Mode == CanBusMode.Fd ? UseRawDataTiming ? DescribeAsSetting(dataRaw) : dataPreset : null;
        return new CanBusConfig { Mode = Mode, Nominal = nominal, Data = data, NominalTiming = nominalTiming, DataTiming = dataTiming };
    }

    partial void OnModeChanged(CanBusMode value)
    {
        OnPropertyChanged(nameof(IsClassicMode));
        OnPropertyChanged(nameof(IsFdMode));
    }

    // Picking a bitrate (preset table or Custom entry) recomputes BRP/TSeg1/TSeg2/SJW so the
    // Advanced fields always show the register values for whatever is currently selected,
    // instead of going stale the moment the user touches the combo box.
    partial void OnSelectedNominalTimingChanged(BitTimingSetting value)
    {
        if (!UseCustomNominalTiming) SyncNominalRawFromPreset(value);
    }

    partial void OnSelectedDataTimingChanged(BitTimingSetting value)
    {
        if (!UseCustomDataTiming) SyncDataRawFromPreset(value);
    }

    partial void OnUseCustomNominalTimingChanged(bool value) => SyncNominalRawFromCurrentSelection();
    partial void OnUseCustomDataTimingChanged(bool value) => SyncDataRawFromCurrentSelection();
    partial void OnCustomNominalBitrateKbpsChanged(string value) { if (UseCustomNominalTiming) SyncNominalRawFromCurrentSelection(); }
    partial void OnCustomNominalSamplePointChanged(string value) { if (UseCustomNominalTiming) SyncNominalRawFromCurrentSelection(); }
    partial void OnCustomDataBitrateKbpsChanged(string value) { if (UseCustomDataTiming) SyncDataRawFromCurrentSelection(); }
    partial void OnCustomDataSamplePointChanged(string value) { if (UseCustomDataTiming) SyncDataRawFromCurrentSelection(); }

    partial void OnNominalPrescalerChanged(string value) => RefreshRawDescriptions();
    partial void OnNominalTseg1Changed(string value) => RefreshRawDescriptions();
    partial void OnNominalTseg2Changed(string value) => RefreshRawDescriptions();
    partial void OnNominalSjwChanged(string value) => RefreshRawDescriptions();
    partial void OnDataPrescalerChanged(string value) => RefreshRawDescriptions();
    partial void OnDataTseg1Changed(string value) => RefreshRawDescriptions();
    partial void OnDataTseg2Changed(string value) => RefreshRawDescriptions();
    partial void OnDataSjwChanged(string value) => RefreshRawDescriptions();

    private void RefreshRawDescriptions()
    {
        NominalRawDescription = Describe(NominalPrescaler, NominalTseg1, NominalTseg2, NominalSjw);
        DataRawDescription = Describe(DataPrescaler, DataTseg1, DataTseg2, DataSjw);
    }

    private void SyncNominalRawFromCurrentSelection()
    {
        var preset = UseCustomNominalTiming
            ? TryParsePreviewSetting(CustomNominalBitrateKbps, CustomNominalSamplePoint)
            : SelectedNominalTiming;
        if (preset is not null) SyncNominalRawFromPreset(preset);
    }

    private void SyncDataRawFromCurrentSelection()
    {
        var preset = UseCustomDataTiming
            ? TryParsePreviewSetting(CustomDataBitrateKbps, CustomDataSamplePoint)
            : SelectedDataTiming;
        if (preset is not null) SyncDataRawFromPreset(preset);
    }

    private void SyncNominalRawFromPreset(BitTimingSetting preset)
    {
        if (!TryCalculate(preset, out var segments)) return;
        NominalPrescaler = segments.Prescaler.ToString();
        NominalTseg1 = segments.Tseg1.ToString();
        NominalTseg2 = segments.Tseg2.ToString();
        NominalSjw = segments.Sjw.ToString();
    }

    private void SyncDataRawFromPreset(BitTimingSetting preset)
    {
        if (!TryCalculate(preset, out var segments)) return;
        DataPrescaler = segments.Prescaler.ToString();
        DataTseg1 = segments.Tseg1.ToString();
        DataTseg2 = segments.Tseg2.ToString();
        DataSjw = segments.Sjw.ToString();
    }

    private bool TryCalculate(BitTimingSetting preset, out RawBitTimingSegments segments)
    {
        try
        {
            segments = _timingCalculator.Calculate(preset, DefaultControllerClockHz);
            return true;
        }
        catch (InvalidOperationException)
        {
            // The preset cannot be represented at the reference clock; leave the existing raw fields as-is.
            segments = default!;
            return false;
        }
    }

    private static BitTimingSetting? TryParsePreviewSetting(string bitrateText, string samplePointText)
    {
        if (!int.TryParse(bitrateText, out var bitrate) || bitrate <= 0) return null;
        if (!double.TryParse(samplePointText, out var samplePoint) || samplePoint is <= 0 or > 100) return null;
        return new BitTimingSetting(bitrate, samplePoint);
    }

    private string Describe(string prescaler, string tseg1, string tseg2, string sjw)
    {
        try
        {
            var value = _timingCalculator.Describe(CreateRawSegments(prescaler, tseg1, tseg2, sjw), DefaultControllerClockHz);
            return $"Effective: {value.BitrateKbps:N0} kbps / {value.SamplePointPercent:0.#}%";
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return "Enter valid BRP / TSeg1 / TSeg2 / SJW";
        }
    }

    private BitTimingSetting DescribeAsSetting(RawBitTimingSegments segments)
    {
        var value = _timingCalculator.Describe(segments, DefaultControllerClockHz);
        return new BitTimingSetting(value.BitrateKbps, value.SamplePointPercent, true);
    }

    private static RawBitTimingSegments CreateRawSegments(string prescaler, string tseg1, string tseg2, string sjw)
    {
        if (!int.TryParse(prescaler, out var brp) || !int.TryParse(tseg1, out var first) ||
            !int.TryParse(tseg2, out var second) || !int.TryParse(sjw, out var jump))
            throw new InvalidOperationException("BRP, TSeg1, TSeg2, and SJW must be integers.");
        var segments = new RawBitTimingSegments(brp, first, second, jump);
        segments.Validate();
        return segments;
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
