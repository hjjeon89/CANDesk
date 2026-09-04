using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CANDesk.Core.Dispatch;
using CANDesk.Hal;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CANDesk.App;

public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly DeviceConnectionService _connection;
    private RxDispatcher _dispatcher;
    private long _frameSequence;
    public ObservableCollection<TraceFrameRow> Frames { get; } = [];
    public ObservableCollection<MessageTreeItem> MessageTree { get; } =
    [new("0x100", "EngineStatus", "10ms · DLC:8", [new("EngineSpeed", "2,450 rpm"), new("CoolantTemp", "87.5 °C"), new("EngineState", "Running")]), new("0x200", "BatteryPackStatus", "50ms · DLC:8", [new("PackVoltage", "398.2 V"), new("PackCurrent", "-24.5 A"), new("StateOfCharge", "78 %")]), new("0x301", "VCU_Control", "Cyclic · DLC:8", [new("TorqueRequest", "120 Nm"), new("RollingCounter", "0")])];
    public ObservableCollection<TxJobRow> TxJobs { get; } = [new("0x301", "VCU_Control", "20 ms", "8", "AA BB CC 00 00 00 00 12", true, true, true), new("0x7DF", "OBD-II Req (Tester)", "Manual", "8", "02 01 0C 55 55 55 55 55", false, false, false)];
    [ObservableProperty] private string _status = "Disconnected";
    [ObservableProperty] private bool _isCapturing = true;
    [ObservableProperty] private bool _isAutoScroll = true;
    [ObservableProperty] private bool _isRawMode;
    [ObservableProperty] private bool _isDecodedMode = true;
    [ObservableProperty] private bool _showRx = true;
    [ObservableProperty] private bool _showTx = true;
    [ObservableProperty] private bool _errorsOnly;
    [ObservableProperty] private string _signalSearch = string.Empty;
    [ObservableProperty] private string _traceFilterText = string.Empty;
    [ObservableProperty] private TraceFrameRow _selectedFrame = TraceFrameRow.Empty;
    public DeviceConnectionViewModel Connection { get; }
    public string DeviceStatusText => Status == "Open" ? $"Connected: {Connection.SelectedVendor} ({Connection.SelectedChannel}) - {Connection.SelectedNominalTiming}" : "Disconnected";
    public string RxRate => "0";
    public string TxRate => "0";
    public long DroppedFrameCount => _dispatcher.DroppedFrameCount;

    public MainViewModel(DeviceConnectionService connection, DeviceConnectionViewModel connectionViewModel)
    {
        _connection = connection;
        Connection = connectionViewModel;
        _dispatcher = CreateDispatcher();
    }
    public async Task AttachCurrentDeviceAsync(CancellationToken cancellationToken = default)
    {
        var device = _connection.CurrentDevice ?? throw new InvalidOperationException("No CAN device is connected.");
        device.StatusChanged += (_, args) => { Status = args.Status.ToString(); OnPropertyChanged(nameof(DeviceStatusText)); };
        Status = device.Status.ToString(); OnPropertyChanged(nameof(DeviceStatusText));
        await _dispatcher.StartAsync(device.ReadFramesAsync(cancellationToken), cancellationToken);
    }
    [RelayCommand] private void ToggleCapture() => IsCapturing = !IsCapturing;
    [RelayCommand] private void ClearTrace() { Frames.Clear(); SelectedFrame = TraceFrameRow.Empty; }
    [RelayCommand] private async Task Connect()
    {
        var configuration = Connection.BuildConfiguration();
        await _dispatcher.DisposeAsync();
        _dispatcher = CreateDispatcher();
        await _connection.ConnectMockAsync(configuration);
        await AttachCurrentDeviceAsync();
    }
    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(DeviceStatusText));
    private void OnFramesBatched(object? sender, IReadOnlyList<CanFrame> frames) => Application.Current.Dispatcher.BeginInvoke(() =>
    {
        if (!IsCapturing) return;
        foreach (var frame in frames)
        {
            var bytes = Convert.ToHexString(frame.PayloadSpan).Chunk(2).Select(hex => new string(hex)).ToArray();
            var data = string.Join(" ", bytes);
            var row = new TraceFrameRow(Interlocked.Increment(ref _frameSequence), frame.SystemTime, $"0x{frame.Id:X3}", "RX", frame.Dlc, data, "Raw frame", bytes);
            Frames.Add(row); SelectedFrame = row;
        }
        while (Frames.Count > 10_000) Frames.RemoveAt(0);
        OnPropertyChanged(nameof(DroppedFrameCount));
    }, DispatcherPriority.Background);
    public ValueTask DisposeAsync() => _dispatcher.DisposeAsync();

    private RxDispatcher CreateDispatcher()
    {
        var dispatcher = new RxDispatcher();
        dispatcher.FramesBatched += OnFramesBatched;
        return dispatcher;
    }
}

public sealed record MessageTreeItem(string Id, string Name, string Meta, IReadOnlyList<SignalTreeItem> Signals);
public sealed record SignalTreeItem(string Name, string Value);
public sealed class TxJobRow(string id, string name, string period, string dlc, string payload, bool isEnabled, bool autoCounter, bool e2eCrc)
{
    public string Id { get; } = id; public string Name { get; } = name; public string Period { get; } = period; public string Dlc { get; } = dlc;
    public string Payload { get; set; } = payload; public bool IsEnabled { get; set; } = isEnabled; public bool AutoCounter { get; set; } = autoCounter; public bool E2eCrc { get; set; } = e2eCrc;
}
public sealed record TraceFrameRow(long Index, DateTime Time, string Id, string Direction, byte Dlc, string Data, string Summary, IReadOnlyList<string> Bytes)
{
    public static TraceFrameRow Empty { get; } = new(0, DateTime.MinValue, "—", "", 0, "", "Select a trace row to inspect its payload and decoded signals.", []);
}
