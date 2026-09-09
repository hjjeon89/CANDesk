using System.Runtime.Versioning;
using System.Threading.Channels;
using CANDesk.Hal;

namespace CANDesk.Hal.Peak;

/// <summary>
/// <see cref="ICanDevice"/> adapter over a PEAK PCAN-USB channel via PCAN-Basic. Classic CAN only
/// (see <see cref="PCanBasicNative"/> remarks); polls <c>CAN_Read</c> on a background loop rather
/// than using PCAN-Basic's Windows-event notification (<c>PCAN_RECEIVE_EVENT</c>), which is a
/// reasonable v1 trade-off — lower throughput ceiling, no dependency on wrapping a native event
/// handle — and a documented follow-up if latency/CPU under load turns out to matter.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class PeakCanDevice(ushort channel, string channelName) : ICanDevice
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(1);
    private static readonly TimeSpan StatusPollInterval = TimeSpan.FromMilliseconds(200);

    private readonly Channel<CanFrame> _frames = Channel.CreateBounded<CanFrame>(
        new BoundedChannelOptions(32_768) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _sessionCancellation;
    private Task? _readLoop;
    private Task? _statusLoop;
    private int _disposeState;
    // TPCANTimestamp is a millisecond counter that (per PCAN-Basic's documented behavior) restarts
    // near zero at CAN_Initialize, not a wall-clock time — anchoring it to the wall clock at Open
    // gives each frame a real SystemTime instead of the dequeue-time DateTime.UtcNow this replaced,
    // which added polling-loop/scheduler jitter noise to Monitor/Trace's Cycle Time readings.
    private DateTime _timeBaseUtc;

    public string ChannelName { get; } = channelName;
    public CanDeviceStatus Status { get; private set; } = CanDeviceStatus.Closed;

    public event EventHandler<CanErrorEventArgs>? ErrorOccurred;
    public event EventHandler<CanDeviceStatusChangedEventArgs>? StatusChanged;

    public Task OpenAsync(CanBusConfig config, CancellationToken cancellationToken = default)
    {
        if (Status == CanDeviceStatus.Open)
        {
            return Task.CompletedTask;
        }

        config.Validate();
        if (config.Mode == CanBusMode.Fd)
        {
            throw new NotSupportedException("PEAK CAN-FD support is not implemented yet (Classic CAN only in this release).");
        }

        var btr0Btr1 = config.NominalTiming.InputMode == BitTimingInputMode.RawSegments
            ? PeakBitTiming.FromRawSegments(config.NominalTiming.RawSegments!)
            : PeakBitTiming.FromPreset(config.NominalTiming.Preset!);

        SetStatus(CanDeviceStatus.Opening);
        var result = PCanBasicNative.Initialize(channel, btr0Btr1, hwType: 0, ioPort: 0, interrupt: 0);
        if (result != PCanBasicNative.StatusOk)
        {
            SetStatus(CanDeviceStatus.Faulted);
            throw new InvalidOperationException($"CAN_Initialize failed for channel 0x{channel:X}: PCAN status 0x{result:X}.");
        }

        _timeBaseUtc = DateTime.UtcNow;
        var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        _sessionCancellation = sessionCancellation;
        SetStatus(CanDeviceStatus.Open);
        _readLoop = Task.Run(() => ReadLoopAsync(sessionCancellation.Token));
        _statusLoop = Task.Run(() => StatusLoopAsync(sessionCancellation.Token));
        return Task.CompletedTask;
    }

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        if (Status == CanDeviceStatus.Closed)
        {
            return;
        }

        SetStatus(CanDeviceStatus.Closed);
        _sessionCancellation?.Cancel();
        var pending = new List<Task>();
        if (_readLoop is not null) pending.Add(_readLoop);
        if (_statusLoop is not null) pending.Add(_statusLoop);
        if (pending.Count > 0)
        {
            await Task.WhenAll(pending).WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        _readLoop = null;
        _statusLoop = null;
        _sessionCancellation?.Dispose();
        _sessionCancellation = null;
        PCanBasicNative.Uninitialize(channel);
    }

    public ValueTask SendAsync(CanFrame frame, CancellationToken cancellationToken = default)
    {
        EnsureOpen();

        var message = new PCanBasicNative.TPCANMsg
        {
            Id = frame.Id,
            MsgType = (byte)((frame.Flags.HasFlag(CanFrameFlags.Extended) ? PCanBasicNative.MessageExtended : PCanBasicNative.MessageStandard)
                | (frame.Flags.HasFlag(CanFrameFlags.RemoteTransmissionRequest) ? PCanBasicNative.MessageRtr : 0)),
            Length = frame.PayloadLength,
        };
        unsafe
        {
            var payload = frame.PayloadSpan;
            for (var i = 0; i < payload.Length && i < 8; i++)
            {
                message.Data[i] = payload[i];
            }
        }

        var result = PCanBasicNative.Write(channel, ref message);
        if (result != PCanBasicNative.StatusOk)
        {
            throw new InvalidOperationException($"CAN_Write failed for channel 0x{channel:X}: PCAN status 0x{result:X}.");
        }

        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<CanFrame> ReadFramesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var frame in _frames.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return frame;
        }
    }

    public Task ResetBusAsync(CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        var result = PCanBasicNative.Reset(channel);
        if (result != PCanBasicNative.StatusOk)
        {
            throw new InvalidOperationException($"CAN_Reset failed for channel 0x{channel:X}: PCAN status 0x{result:X}.");
        }

        SetStatus(CanDeviceStatus.Open);
        return Task.CompletedTask;
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = PCanBasicNative.Read(channel, out var message, out var timestamp);
                if (result == PCanBasicNative.StatusQueueReceiveEmpty)
                {
                    await Task.Delay(PollInterval, ct).ConfigureAwait(false);
                    continue;
                }

                if (result != PCanBasicNative.StatusOk)
                {
                    ErrorOccurred?.Invoke(this, new(ClassifyError(result), $"CAN_Read returned PCAN status 0x{result:X}."));
                    await Task.Delay(PollInterval, ct).ConfigureAwait(false);
                    continue;
                }

                // PCAN_MESSAGE_STATUS frames carry bus-state info rather than payload data; skip them here,
                // the status loop already surfaces bus condition changes.
                if ((message.MsgType & PCanBasicNative.MessageStatus) != 0)
                {
                    continue;
                }

                _frames.Writer.TryWrite(ToCanFrame(message, _timeBaseUtc, timestamp));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private async Task StatusLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(StatusPollInterval, ct).ConfigureAwait(false);
                var status = PCanBasicNative.GetStatus(channel);
                if (status == PCanBasicNative.StatusOk)
                {
                    continue;
                }

                if ((status & PCanBasicNative.StatusBusOff) != 0)
                {
                    SetStatus(CanDeviceStatus.BusOff);
                }

                ErrorOccurred?.Invoke(this, new(ClassifyError(status), $"CAN_GetStatus returned PCAN status 0x{status:X}."));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private static CanErrorKind ClassifyError(uint status) => status switch
    {
        _ when (status & PCanBasicNative.StatusBusOff) != 0 => CanErrorKind.BusOff,
        _ when (status & PCanBasicNative.StatusBusPassive) != 0 => CanErrorKind.ErrorPassive,
        _ when (status & (PCanBasicNative.StatusBusLight | PCanBasicNative.StatusBusHeavy)) != 0 => CanErrorKind.ErrorWarning,
        _ => CanErrorKind.Driver,
    };

    private static unsafe CanFrame ToCanFrame(in PCanBasicNative.TPCANMsg message, DateTime timeBaseUtc, in PCanBasicNative.TPCANTimestamp timestamp)
    {
        var flags = CanFrameFlags.None;
        if ((message.MsgType & PCanBasicNative.MessageExtended) != 0) flags |= CanFrameFlags.Extended;
        if ((message.MsgType & PCanBasicNative.MessageRtr) != 0) flags |= CanFrameFlags.RemoteTransmissionRequest;
        if ((message.MsgType & PCanBasicNative.MessageErrorFrame) != 0) flags |= CanFrameFlags.ErrorFrame;

        var length = Math.Min(message.Length, (byte)8);
        Span<byte> payload = stackalloc byte[length];
        for (var i = 0; i < length; i++)
        {
            payload[i] = message.Data[i];
        }

        // Millis wraps at 2^32; MillisOverflow counts how many times it has. Reconstructing the
        // full elapsed time from all three fields (rather than dequeue-time DateTime.UtcNow) gives
        // Monitor/Trace the driver's own capture time, free of polling-loop jitter.
        var elapsedMs = timestamp.Millis + timestamp.MillisOverflow * 4_294_967_296.0;
        var systemTime = timeBaseUtc.AddMilliseconds(elapsedMs).AddTicks(timestamp.Micros * 10L);

        return CanFrame.Create(message.Id, payload, flags, systemTime: systemTime);
    }

    private void EnsureOpen()
    {
        if (Status != CanDeviceStatus.Open)
        {
            throw new InvalidOperationException("The PEAK CAN device is not open.");
        }
    }

    private void SetStatus(CanDeviceStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, new(status));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        await CloseAsync().ConfigureAwait(false);
        await _lifetime.CancelAsync().ConfigureAwait(false);
        _frames.Writer.TryComplete();
        _lifetime.Dispose();
    }
}
