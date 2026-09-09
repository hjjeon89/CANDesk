using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Threading;
using System.Windows;
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
    private readonly DispatcherTimer _rateTimer;
    private RxDispatcher _dispatcher;
    private ITxScheduler? _txScheduler;
    private long _rxFrameCount;
    private long _txFrameCount;
    private long _busBitsAccumulated;
    // Some vendor drivers/hardware modes echo a transmitted frame back through the same RX path
    // used for genuine bus traffic (PCAN "receive own messages" style behavior; on Vector this can
    // slip through if the driver delivers the TX echo as a plain RECEIVE_MSG event rather than a
    // distinctly-tagged one). Content-matching a frame against what TxScheduler.FrameSent just
    // reported — rather than trusting a vendor-specific "this is an echo" flag — catches the echo
    // regardless of which vendor/mode produced it, so it isn't double-counted as RX and doesn't
    // flip a just-sent message's Direction back to "RX" in Monitor/Trace/Signal Monitor.
    private readonly ConcurrentDictionary<CanFrame, DateTime> _recentlyTransmitted = new();
    private static readonly TimeSpan EchoSuppressionWindow = TimeSpan.FromMilliseconds(250);
    public event EventHandler? MessageEditorRequested;
    public event EventHandler? SignalMonitorRequested;
    public MessageMonitorViewModel Monitor { get; } = new();
    public SignalMonitorViewModel SignalMonitor { get; } = new();
    public SignalPlotViewModel SignalPlot { get; } = new();
    public TraceLogViewModel TraceLog { get; } = new();
    public DbcSignalTreeViewModel DbcSignalTree { get; } = new();
    public MessageDbEditorViewModel MessageDbEditor { get; } = new();
    public TransmitPanelViewModel TransmitPanel { get; } = new();
    [ObservableProperty] private string _status = "Disconnected";
    [ObservableProperty] private string _lastDeviceError = string.Empty;
    [ObservableProperty] private bool _isCapturing = true;
    [ObservableProperty] private bool _isRawMode;
    [ObservableProperty] private bool _isDecodedMode = true;
    [ObservableProperty] private string _signalSearch = string.Empty;
    // Message Monitor (index 1) is the default landing tab; Trace is opt-in via the tab strip.
    [ObservableProperty] private int _selectedCenterTabIndex = 1;
    [ObservableProperty] private int _selectedLeftTabIndex;
    public string CaptureCommandText => IsCapturing ? "Pause" : "Start";
    public DeviceConnectionViewModel Connection { get; }
    // "Opening" briefly disables both buttons so a double-click can't fire overlapping connect
    // attempts; every other status (including BusOff/Faulted) still allows a fresh Connect to recover.
    public bool CanConnect => Status != "Opening";
    public bool CanDisconnect => Status is "Open" or "BusOff" or "Faulted";
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
    [ObservableProperty] private string _rxRate = "0";
    [ObservableProperty] private string _txRate = "0";
    [ObservableProperty] private double _busLoadPercent;
    public string BusLoadText => $"{BusLoadPercent:0.0}%";
    /// <summary>Pixel width of the status-bar bus-load indicator fill, scaled against the 60px track
    /// drawn in <c>MainWindow.xaml</c>.</summary>
    public double BusLoadBarWidth => Math.Clamp(BusLoadPercent, 0, 100) / 100.0 * 60.0;
    public long DroppedFrameCount => _dispatcher.DroppedFrameCount;
    public Brush DroppedFrameBrush => DroppedFrameCount > 0 ? Brushes.Firebrick : Brushes.SlateGray;

    public MainViewModel(DeviceConnectionService connection, DeviceConnectionViewModel connectionViewModel)
    {
        _connection = connection;
        Connection = connectionViewModel;
        _dispatcher = CreateDispatcher();
        DbcSignalTree.SendToTransmitRequested += message =>
        {
            TransmitPanel.AddJobFromMessage(message);
            SelectedCenterTabIndex = 2;
        };
        TransmitPanel.SetNodeEmulationProviders(
            () => MessageDbEditor.Nodes.Select(node => node.Name).ToArray(),
            nodeName => MessageDbEditor.GetTransmitMessages(nodeName));
        MessageDbEditor.NodesChanged += (_, _) => TransmitPanel.RefreshEmulatedNodes();
        _rateTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _rateTimer.Tick += (_, _) => UpdateRates();
        _rateTimer.Start();
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
        // One subscription covers every send path (single, cyclic, triggered) so cyclic TX frames
        // are no longer invisible to Trace/Monitor, and TX rate counting doesn't miss them either.
        txScheduler.FrameSent += (_, frame) =>
        {
            _recentlyTransmitted[frame] = DateTime.UtcNow;
            Interlocked.Increment(ref _txFrameCount);
            Interlocked.Add(ref _busBitsAccumulated, EstimateFrameBits(frame));
            DispatchToUi(() =>
            {
                TraceLog.ProcessTransmittedFrame(frame);
                Monitor.ProcessTransmittedFrame(frame);
                SignalMonitor.ProcessTransmittedFrame(frame);
                SignalPlot.ProcessTransmittedFrame(frame);
            });
        };
        TransmitPanel.SetScheduler(txScheduler);
        TransmitPanel.SetSendHandler(frame => txScheduler.SendOnceAsync(frame).AsTask());
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
    private void OpenSignalMonitor() => SignalMonitorRequested?.Invoke(this, EventArgs.Empty);

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
            TraceLog.SetDatabase(database);
            Monitor.SetDatabase(database);
            SignalMonitor.SetDatabase(database);
            SignalPlot.SetDatabase(database);
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

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
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
    [RelayCommand(CanExecute = nameof(CanConnect))] private async Task Connect()
    {
        var configuration = Connection.BuildConfiguration();
        await _dispatcher.DisposeAsync();
        _dispatcher = CreateDispatcher();
        try
        {
            await _connection.ConnectByVendorAsync(Connection.SelectedVendor, Connection.SelectedChannel, configuration);
        }
        catch (Exception exception)
        {
            LastDeviceError = $"Connect failed: {exception.Message}";
            return;
        }

        LastDeviceError = string.Empty;
        await AttachCurrentDeviceAsync();
    }
    partial void OnStatusChanged(string value)
    {
        OnPropertyChanged(nameof(DeviceStatusText));
        OnPropertyChanged(nameof(BusStatusText));
        OnPropertyChanged(nameof(BusStatusBrush));
        OnPropertyChanged(nameof(CanConnect));
        OnPropertyChanged(nameof(CanDisconnect));
        ConnectCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
    }
    private void OnFramesBatched(object? sender, IReadOnlyList<CanFrame> frames)
    {
        List<CanFrame>? genuineRx = null;
        for (var i = 0; i < frames.Count; i++)
        {
            var frame = frames[i];
            if (_recentlyTransmitted.TryGetValue(frame, out var sentAt) && DateTime.UtcNow - sentAt <= EchoSuppressionWindow)
            {
                // Our own transmit, echoed back through the RX path — already accounted for via
                // FrameSent above; skip it so it isn't double-counted or shown as an RX direction.
                _recentlyTransmitted.TryRemove(frame, out _);
                genuineRx ??= [.. frames.Take(i)];
                continue;
            }

            genuineRx?.Add(frame);
        }

        var rxFrames = genuineRx ?? frames;
        if (rxFrames.Count == 0)
        {
            return;
        }

        // Counted here (off the UI thread, as frames actually arrive) rather than in IsCapturing-gated
        // UI processing below, so RxRate/BusLoad reflect real bus traffic even while capture is paused.
        Interlocked.Add(ref _rxFrameCount, rxFrames.Count);
        long bits = 0;
        foreach (var frame in rxFrames) bits += EstimateFrameBits(frame);
        Interlocked.Add(ref _busBitsAccumulated, bits);

        DispatchToUi(() =>
        {
            if (!IsCapturing) return;
            Monitor.ProcessFrames(rxFrames);
            TraceLog.ProcessFrames(rxFrames);
            SignalMonitor.ProcessFrames(rxFrames);
            SignalPlot.ProcessFrames(rxFrames);
            OnPropertyChanged(nameof(DroppedFrameCount));
            OnPropertyChanged(nameof(DroppedFrameBrush));
        });
    }

    private void UpdateRates()
    {
        RxRate = Interlocked.Exchange(ref _rxFrameCount, 0).ToString();
        TxRate = Interlocked.Exchange(ref _txFrameCount, 0).ToString();

        var bits = Interlocked.Exchange(ref _busBitsAccumulated, 0);
        var bitrateKbps = Connection.SelectedNominalTiming.BitrateKbps;
        BusLoadPercent = bitrateKbps > 0 ? Math.Min(100.0, bits / (bitrateKbps * 1000.0) * 100.0) : 0.0;

        // A cyclic TX job with an auto-incrementing counter/CRC byte produces a distinct CanFrame
        // value on every tick, so unmatched entries (no echo arrived, or the vendor doesn't echo at
        // all) would otherwise accumulate here forever over a long capture session.
        var cutoff = DateTime.UtcNow - EchoSuppressionWindow;
        foreach (var (frame, sentAt) in _recentlyTransmitted)
        {
            if (sentAt < cutoff)
            {
                _recentlyTransmitted.TryRemove(frame, out _);
            }
        }
    }

    /// <summary>Rough on-wire bit-length estimate for bus-load purposes: fixed frame overhead
    /// (SOF/ID/control/CRC/ACK/EOF/IFS, extended IDs costing 20 more ID bits) plus 8 bits per data
    /// byte. Deliberately ignores bit stuffing (~+20% worst case), which real CAN traffic rarely hits
    /// continuously, so this is an approximation rather than an exact wire count.</summary>
    private static int EstimateFrameBits(in CanFrame frame)
    {
        var overheadBits = frame.Flags.HasFlag(CanFrameFlags.Extended) ? 67 : 47;
        return overheadBits + frame.PayloadLength * 8;
    }
    public async ValueTask DisposeAsync()
    {
        _rateTimer.Stop();
        if (_txScheduler is not null)
        {
            await _txScheduler.DisposeAsync().ConfigureAwait(false);
        }
        await _dispatcher.DisposeAsync().ConfigureAwait(false);
    }

    partial void OnBusLoadPercentChanged(double value)
    {
        OnPropertyChanged(nameof(BusLoadText));
        OnPropertyChanged(nameof(BusLoadBarWidth));
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
            // Normal, not Background: Background (4) sits below Input/Loaded/Render/DataBind (5-8)
            // in the Dispatcher queue, so under any sustained UI activity (grid virtualization,
            // hover, scrolling) RX frame delivery to Monitor/Trace kept getting starved behind that
            // other work, showing up as visibly laggy/batched updates instead of near-live ones.
            _ = dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
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

    /// <summary>Set while a <see cref="TxSignalEditRow"/> is re-encoding <see cref="Payload"/> from a
    /// physical value change, so <see cref="OnPayloadChanged"/> does not redundantly decode it back.</summary>
    internal bool IsSyncingFromSignalEdit { get; set; }

    [ObservableProperty] private string _payload;
    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private bool _autoCounter;
    [ObservableProperty] private bool _e2eCrc;

    partial void OnPayloadChanged(string value)
    {
        if (IsSyncingFromSignalEdit || Message is null || Signals.Count == 0) return;

        byte[] bytes;
        try
        {
            bytes = Convert.FromHexString(value.Replace(" ", string.Empty, StringComparison.Ordinal));
        }
        catch (FormatException)
        {
            return;
        }

        foreach (var signal in Signals)
        {
            signal.RefreshFromPayload(bytes);
        }
    }
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

    private bool _isSyncingFromPayload;

    [ObservableProperty] private string _valueText;

    /// <summary>Re-reads this signal's physical value from a freshly-edited raw payload, without
    /// re-triggering the encode path back into <see cref="TxJobRow.Payload"/>.</summary>
    internal void RefreshFromPayload(byte[] payload)
    {
        double physicalValue;
        try
        {
            physicalValue = CANDesk.Core.Dispatch.SignalDecoder.DecodeValue(payload, _signal);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        _isSyncingFromPayload = true;
        try
        {
            ValueText = physicalValue.ToString("0.###");
        }
        finally
        {
            _isSyncingFromPayload = false;
        }
    }

    partial void OnValueTextChanged(string value)
    {
        if (_isSyncingFromPayload || !double.TryParse(value, out var physicalValue)) return;

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

        _job.IsSyncingFromSignalEdit = true;
        try
        {
            _job.Payload = string.Join(' ', bytes.Select(b => b.ToString("X2")));
        }
        finally
        {
            _job.IsSyncingFromSignalEdit = false;
        }
    }
}
public sealed record TraceFrameRow(long Index, DateTime Time, string Id, string Direction, byte Dlc, string Data, string Summary, IReadOnlyList<string> Bytes)
{
    public static TraceFrameRow Empty { get; } = new(0, DateTime.MinValue, "—", "", 0, "", "Select a trace row to inspect its payload and decoded signals.", []);
}
