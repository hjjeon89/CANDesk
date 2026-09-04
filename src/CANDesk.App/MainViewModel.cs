using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using CANDesk.Core.Dispatch;
using CANDesk.Core.MessageDb;
using CANDesk.Core.Scheduling;
using CANDesk.Hal;
using CANDesk.App.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using System.IO;

namespace CANDesk.App;

public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly DeviceConnectionService _connection;
    private RxDispatcher _dispatcher;
    private ITxScheduler? _txScheduler;
    public event EventHandler? MessageEditorRequested;
    public ObservableCollection<TraceFrameRow> Frames { get; } = [];
    public ICollectionView TraceFramesView { get; }
    public MessageMonitorViewModel Monitor { get; } = new();
    public TraceLogViewModel TraceLog { get; } = new();
    public DbcSignalTreeViewModel DbcSignalTree { get; } = new();
    public MessageDbEditorViewModel MessageDbEditor { get; } = new();
    public TransmitPanelViewModel TransmitPanel { get; } = new();
    public ObservableCollection<MessageTreeItem> MessageTree { get; } =
    [new("0x100", "EngineStatus", "10ms · DLC:8", [new("EngineSpeed", "2,450 rpm"), new("CoolantTemp", "87.5 °C"), new("EngineState", "Running")]), new("0x200", "BatteryPackStatus", "50ms · DLC:8", [new("PackVoltage", "398.2 V"), new("PackCurrent", "-24.5 A"), new("StateOfCharge", "78 %")]), new("0x301", "VCU_Control", "Cyclic · DLC:8", [new("TorqueRequest", "120 Nm"), new("RollingCounter", "0")])];
    public ObservableCollection<TxJobRow> TxJobs { get; } = [new("0x301", "VCU_Control", "20 ms", "8", "AA BB CC 00 00 00 00 12", true, true, true), new("0x7DF", "OBD-II Req (Tester)", "Manual", "8", "02 01 0C 55 55 55 55 55", false, false, false)];
    [ObservableProperty] private string _status = "Disconnected";
    [ObservableProperty] private string _lastDeviceError = string.Empty;
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
    // Message Monitor (index 1) is the default landing tab; Trace is opt-in via the tab strip.
    [ObservableProperty] private int _selectedCenterTabIndex = 1;
    [ObservableProperty] private int _selectedLeftTabIndex;
    public string CaptureCommandText => IsCapturing ? "Pause" : "Start";
    public DeviceConnectionViewModel Connection { get; }
    public string DeviceStatusText => Status == "Open" ? $"Connected: {Connection.SelectedVendor} ({Connection.SelectedChannel}) - {Connection.SelectedNominalTiming}" : "Disconnected";
    public string BusStatusText => Status switch
    {
        "Open" => "Normal (OK)",
        "BusOff" => "Bus-Off",
        "Faulted" => "Device Fault",
        "Opening" => "Opening",
        _ => "Disconnected"
    };
    public Brush BusStatusBrush => Status switch
    {
        "Open" => Brushes.SeaGreen,
        "BusOff" or "Faulted" => Brushes.Firebrick,
        _ => Brushes.SlateGray
    };
    public string RxRate => "0";
    public string TxRate => "0";
    public long DroppedFrameCount => _dispatcher.DroppedFrameCount;
    public Brush DroppedFrameBrush => DroppedFrameCount > 0 ? Brushes.Firebrick : Brushes.SlateGray;

    public MainViewModel(DeviceConnectionService connection, DeviceConnectionViewModel connectionViewModel)
    {
        _connection = connection;
        Connection = connectionViewModel;
        _dispatcher = CreateDispatcher();
        TraceFramesView = CollectionViewSource.GetDefaultView(Frames);
        TraceFramesView.Filter = MatchesTraceFilter;
        DbcSignalTree.SendToTransmitRequested += message =>
        {
            TransmitPanel.AddJobFromMessage(message);
            SelectedCenterTabIndex = 2;
        };
        TransmitPanel.SetNodeEmulationProviders(
            () => MessageDbEditor.Nodes.Select(node => node.Name).ToArray(),
            nodeName => MessageDbEditor.GetTransmitMessages(nodeName));
        MessageDbEditor.NodesChanged += (_, _) => TransmitPanel.RefreshEmulatedNodes();
    }
    public async Task AttachCurrentDeviceAsync(CancellationToken cancellationToken = default)
    {
        var device = _connection.CurrentDevice ?? throw new InvalidOperationException("No CAN device is connected.");
        device.StatusChanged += (_, args) => DispatchToUi(() => Status = args.Status.ToString());
        device.ErrorOccurred += (_, args) => DispatchToUi(() => LastDeviceError = $"{args.Kind}: {args.Message}");
        if (_txScheduler is not null)
        {
            await _txScheduler.DisposeAsync().ConfigureAwait(false);
        }
        var txScheduler = new TxScheduler(device);
        _txScheduler = txScheduler;
        txScheduler.ErrorOccurred += (_, args) => DispatchToUi(() => LastDeviceError = $"TX 0x{args.Frame.Id:X3}: {args.Exception.Message}");
        TransmitPanel.SetScheduler(txScheduler);
        TransmitPanel.SetSendHandler(async frame =>
        {
            await txScheduler.SendOnceAsync(frame);
            DispatchToUi(() =>
            {
                TraceLog.ProcessTransmittedFrame(frame);
                Monitor.ProcessTransmittedFrame(frame);
            });
        });
        Status = device.Status.ToString(); OnPropertyChanged(nameof(DeviceStatusText));
        await _dispatcher.StartAsync(device.ReadFramesAsync(cancellationToken), cancellationToken);
    }
    [RelayCommand] private void ToggleCapture() => IsCapturing = !IsCapturing;

    partial void OnIsCapturingChanged(bool value) => OnPropertyChanged(nameof(CaptureCommandText));

    [RelayCommand]
    private void NewMessageDatabase()
    {
        MessageDbEditor.Clear();
        SelectedLeftTabIndex = 1;
        LastDeviceError = string.Empty;
    }

    [RelayCommand]
    private void ShowMessageDatabase() => SelectedLeftTabIndex = 1;

    [RelayCommand]
    private void OpenMessageEditor() => MessageEditorRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void UndoMessageEdit()
    {
        SelectedLeftTabIndex = 1;
        MessageDbEditor.UndoCommand.Execute(null);
    }

    [RelayCommand]
    private void RedoMessageEdit()
    {
        SelectedLeftTabIndex = 1;
        MessageDbEditor.RedoCommand.Execute(null);
    }

    [RelayCommand]
    private async Task OpenDatabaseAsync()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "CAN database (*.dbc)|*.dbc",
            Title = "Open CAN Database"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            await using var stream = File.OpenRead(dialog.FileName);
            var database = await new DbcParser().ParseAsync(stream);
            DbcSignalTree.Load(database);
            SelectedLeftTabIndex = 0;
            LastDeviceError = string.Empty;
        }
        catch (Exception exception)
        {
            LastDeviceError = $"Database load failed: {exception.Message}";
        }
    }

    [RelayCommand]
    private async Task ExportTraceAsync()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "CSV file (*.csv)|*.csv",
            FileName = "candesk-trace.csv",
            Title = "Export Trace"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var lines = new List<string> { "Index,Timestamp,CanId,Direction,Dlc,Payload" };
        lines.AddRange(TraceLog.Frames.Select(frame => string.Join(',', frame.Index, frame.Time.ToString("O"), frame.Id, frame.Direction, frame.Dlc, $"\"{frame.Data}\"")));
        await File.WriteAllLinesAsync(dialog.FileName, lines);
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        if (_txScheduler is not null)
        {
            await _txScheduler.DisposeAsync();
            _txScheduler = null;
        }
        TransmitPanel.SetSendHandler(null);
        TransmitPanel.SetScheduler(null);
        await _dispatcher.DisposeAsync().ConfigureAwait(false);
        _dispatcher = CreateDispatcher();
        await _connection.DisconnectAsync();
        Status = CanDeviceStatus.Closed.ToString();
    }

    [RelayCommand]
    private async Task ResetBusAsync()
    {
        var device = _connection.CurrentDevice;
        if (device is null)
        {
            LastDeviceError = "No CAN device is connected.";
            return;
        }

        await device.ResetBusAsync();
        LastDeviceError = string.Empty;
    }
    [RelayCommand] private void ClearTrace()
    {
        TraceLog.Clear();
        Monitor.Clear();
    }
    [RelayCommand] private async Task Connect()
    {
        var configuration = Connection.BuildConfiguration();
        await _dispatcher.DisposeAsync();
        _dispatcher = CreateDispatcher();
        await _connection.ConnectMockAsync(configuration);
        await AttachCurrentDeviceAsync();
    }
    partial void OnStatusChanged(string value)
    {
        OnPropertyChanged(nameof(DeviceStatusText));
        OnPropertyChanged(nameof(BusStatusText));
        OnPropertyChanged(nameof(BusStatusBrush));
    }
    private void OnFramesBatched(object? sender, IReadOnlyList<CanFrame> frames) => DispatchToUi(() =>
    {
        if (!IsCapturing) return;
        Monitor.ProcessFrames(frames);
        TraceLog.ProcessFrames(frames);
        OnPropertyChanged(nameof(DroppedFrameCount));
        OnPropertyChanged(nameof(DroppedFrameBrush));
    });
    public async ValueTask DisposeAsync()
    {
        if (_txScheduler is not null)
        {
            await _txScheduler.DisposeAsync().ConfigureAwait(false);
        }
        await _dispatcher.DisposeAsync().ConfigureAwait(false);
    }

    partial void OnTraceFilterTextChanged(string value) => TraceFramesView.Refresh();
    partial void OnShowRxChanged(bool value) => TraceFramesView.Refresh();
    partial void OnShowTxChanged(bool value) => TraceFramesView.Refresh();
    partial void OnErrorsOnlyChanged(bool value) => TraceFramesView.Refresh();

    private bool MatchesTraceFilter(object item)
    {
        if (item is not TraceFrameRow frame) return false;
        if (ErrorsOnly && frame.Direction != "ERR") return false;
        if (frame.Direction == "RX" && !ShowRx) return false;
        if (frame.Direction == "TX" && !ShowTx) return false;
        return string.IsNullOrWhiteSpace(TraceFilterText) || frame.Id.Contains(TraceFilterText.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private RxDispatcher CreateDispatcher()
    {
        var dispatcher = new RxDispatcher();
        dispatcher.FramesBatched += OnFramesBatched;
        return dispatcher;
    }

    private static void DispatchToUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return;
        }
        if (dispatcher.CheckAccess())
        {
            action();
            return;
        }
        try
        {
            _ = dispatcher.BeginInvoke(action, DispatcherPriority.Background);
        }
        catch (InvalidOperationException) when (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
        }
    }
}

public sealed record MessageTreeItem(string Id, string Name, string Meta, IReadOnlyList<SignalTreeItem> Signals, DbcMessage? Source = null);
public sealed record SignalTreeItem(string Name, string Value);
public sealed partial class TxJobRow : ObservableObject
{
    public TxJobRow(string id, string name, string period, string dlc, string payload, bool isEnabled, bool autoCounter, bool e2eCrc)
    {
        Id = id;
        Name = name;
        Period = period;
        Dlc = dlc;
        _payload = payload;
        _isEnabled = isEnabled;
        _autoCounter = autoCounter;
        _e2eCrc = e2eCrc;
    }

    public string Id { get; }
    public string Name { get; }
    public string Period { get; }
    public string Dlc { get; }

    /// <summary>The DBC message this job was created from, if any. Null for freeform hex-only jobs.</summary>
    public DbcMessage? Message { get; init; }

    /// <summary>Editable per-signal values for <see cref="Message"/>; empty for freeform jobs.</summary>
    public ObservableCollection<TxSignalEditRow> Signals { get; } = [];

    [ObservableProperty] private string _payload;
    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private bool _autoCounter;
    [ObservableProperty] private bool _e2eCrc;
}

/// <summary>
/// Edits one signal's physical value for a <see cref="TxJobRow"/>. Changing the value re-encodes
/// just that signal's bits into the job's hex payload via <see cref="CANDesk.Core.Dispatch.SignalEncoder"/>,
/// leaving every other byte untouched so multiple signals in the same message compose correctly.
/// </summary>
public sealed partial class TxSignalEditRow : ObservableObject
{
    private readonly TxJobRow _job;
    private readonly DbcSignal _signal;

    public TxSignalEditRow(TxJobRow job, DbcSignal signal, double initialValue)
    {
        _job = job;
        _signal = signal;
        _valueText = initialValue.ToString("0.###");
    }

    public string DisplayName => string.IsNullOrEmpty(_signal.Unit) ? _signal.Name : $"{_signal.Name} ({_signal.Unit})";

    [ObservableProperty] private string _valueText;

    partial void OnValueTextChanged(string value)
    {
        if (!double.TryParse(value, out var physicalValue)) return;

        byte[] bytes;
        try
        {
            bytes = Convert.FromHexString(_job.Payload.Replace(" ", string.Empty, StringComparison.Ordinal));
        }
        catch (FormatException)
        {
            return;
        }

        try
        {
            CANDesk.Core.Dispatch.SignalEncoder.Encode(bytes, _signal, physicalValue);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        _job.Payload = string.Join(' ', bytes.Select(b => b.ToString("X2")));
    }
}
public sealed record TraceFrameRow(long Index, DateTime Time, string Id, string Direction, byte Dlc, string Data, string Summary, IReadOnlyList<string> Bytes)
{
    public static TraceFrameRow Empty { get; } = new(0, DateTime.MinValue, "—", "", 0, "", "Select a trace row to inspect its payload and decoded signals.", []);
}
