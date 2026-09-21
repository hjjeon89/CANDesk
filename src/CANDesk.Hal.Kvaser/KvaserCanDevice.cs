using System.Runtime.Versioning;
using System.Threading.Channels;
using CANDesk.Hal;

namespace CANDesk.Hal.Kvaser;

[SupportedOSPlatform("windows")]
internal sealed class KvaserCanDevice : ICanDevice
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromMilliseconds(50);

    private readonly int _channelIndex;
    private readonly Channel<CanFrame> _frames;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _sessionCancellation;
    private Task? _readLoop;
    private DateTime _timeBaseUtc;
    private int _handle = -1;
    private int _disposeState;
    private long _dropped;

    public KvaserCanDevice(int channelIndex, string channelName)
    {
        _channelIndex = channelIndex;
        ChannelName = channelName;
        _frames = Channel.CreateBounded<CanFrame>(
            new BoundedChannelOptions(32_768) { FullMode = BoundedChannelFullMode.DropOldest },
            _ => Interlocked.Increment(ref _dropped));
    }

    public string ChannelName { get; }
    public CanDeviceStatus Status { get; private set; } = CanDeviceStatus.Closed;
    public long DroppedFrameCount => Interlocked.Read(ref _dropped);

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
            throw new NotSupportedException("Kvaser CAN-FD support is not implemented yet (Classic CAN only in this release).");
        }

        SetStatus(CanDeviceStatus.Opening);
        KvaserCanlibNative.InitializeLibrary();
        var handle = KvaserCanlibNative.OpenChannel(_channelIndex, KvaserCanlibNative.OpenAcceptVirtual);
        if (handle < 0)
        {
            SetStatus(CanDeviceStatus.Faulted);
            throw new InvalidOperationException($"canOpenChannel failed for Kvaser channel {_channelIndex}: CANlib status {handle}.");
        }

        _handle = handle;
        try
        {
            ConfigureBitrate(handle, config);
            CheckStatus(KvaserCanlibNative.SetBusOutputControl(handle, KvaserCanlibNative.DriverNormal), "canSetBusOutputControl");
            CheckStatus(KvaserCanlibNative.BusOn(handle), "canBusOn");
        }
        catch
        {
            KvaserCanlibNative.Close(handle);
            _handle = -1;
            SetStatus(CanDeviceStatus.Faulted);
            throw;
        }

        _timeBaseUtc = DateTime.UtcNow;
        var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        _sessionCancellation = sessionCancellation;
        SetStatus(CanDeviceStatus.Open);
        _readLoop = Task.Run(() => ReadLoopAsync(sessionCancellation.Token));
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
        if (_readLoop is not null)
        {
            await _readLoop.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        _readLoop = null;
        _sessionCancellation?.Dispose();
        _sessionCancellation = null;

        if (_handle >= 0)
        {
            KvaserCanlibNative.BusOff(_handle);
            KvaserCanlibNative.Close(_handle);
            _handle = -1;
        }
    }

    public ValueTask SendAsync(CanFrame frame, CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        if (frame.PayloadLength > 8)
        {
            throw new NotSupportedException("Kvaser Classic CAN send supports up to 8 payload bytes.");
        }

        var payload = new byte[8];
        frame.PayloadSpan.CopyTo(payload);
        var flags = ToKvaserFlags(frame.Flags);
        var status = KvaserCanlibNative.Write(_handle, (int)frame.Id, payload, frame.PayloadLength, flags);
        CheckStatus(status, "canWrite");
        status = KvaserCanlibNative.WriteSync(_handle, 100);
        CheckStatus(status, "canWriteSync");
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
        CheckStatus(KvaserCanlibNative.BusOff(_handle), "canBusOff");
        CheckStatus(KvaserCanlibNative.BusOn(_handle), "canBusOn");
        SetStatus(CanDeviceStatus.Open);
        return Task.CompletedTask;
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var data = new byte[64];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var status = KvaserCanlibNative.ReadWait(_handle, out var id, data, out var dlc, out var flags,
                    out var timestampMs, (uint)ReadTimeout.TotalMilliseconds);
                if (status == KvaserCanlibNative.ErrorNoMessage)
                {
                    continue;
                }

                if (status != KvaserCanlibNative.StatusOk)
                {
                    ErrorOccurred?.Invoke(this, new(CanErrorKind.Driver, $"canReadWait returned CANlib status {status}."));
                    await Task.Delay(1, ct).ConfigureAwait(false);
                    continue;
                }

                var length = (int)Math.Min(dlc, 8);
                var systemTime = _timeBaseUtc.AddMilliseconds(timestampMs);
                var frame = CanFrame.Create((uint)id, data.AsSpan(0, length), FromKvaserFlags(flags), systemTime: systemTime);
                _frames.Writer.TryWrite(frame);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private static int GetPresetBitrateConstant(int bitrateKbps) => bitrateKbps switch
    {
        1000 => KvaserCanlibNative.Bitrate1M,
        500 => KvaserCanlibNative.Bitrate500K,
        250 => KvaserCanlibNative.Bitrate250K,
        125 => KvaserCanlibNative.Bitrate125K,
        100 => KvaserCanlibNative.Bitrate100K,
        62 or 63 => KvaserCanlibNative.Bitrate62K,
        50 => KvaserCanlibNative.Bitrate50K,
        83 => KvaserCanlibNative.Bitrate83K,
        10 => KvaserCanlibNative.Bitrate10K,
        _ => 0
    };

    private static void ConfigureBitrate(int handle, CanBusConfig config)
    {
        var timing = config.NominalTiming;
        if (timing.InputMode == BitTimingInputMode.RawSegments && timing.RawSegments is not null)
        {
            var raw = timing.RawSegments;
            var bitrate = checked((int)(config.Nominal.BitrateKbps * 1_000));
            CheckStatus(KvaserCanlibNative.SetBusParams(handle, bitrate, (uint)raw.Tseg1, (uint)raw.Tseg2, (uint)raw.Sjw, 1, 0), "canSetBusParams");
            return;
        }

        var presetConstant = GetPresetBitrateConstant(config.Nominal.BitrateKbps);
        if (presetConstant != 0)
        {
            CheckStatus(KvaserCanlibNative.SetBusParams(handle, presetConstant, 0, 0, 0, 1, 0), "canSetBusParams");
            return;
        }

        var freq = checked((int)(config.Nominal.BitrateKbps * 1_000));
        var translateStatus = KvaserCanlibNative.TranslateBaud(ref freq, out var tseg1, out var tseg2, out var sjw, out var noSamp, out var syncMode);
        if (translateStatus == KvaserCanlibNative.StatusOk)
        {
            CheckStatus(KvaserCanlibNative.SetBusParams(handle, freq, tseg1, tseg2, sjw, noSamp, syncMode), "canSetBusParams");
            return;
        }

        throw new NotSupportedException(
            $"Kvaser Classic CAN does not have a standard preset for {config.Nominal.BitrateKbps} kbps. " +
            "Please use Advanced timing mode (RawSegments) to configure BRP/TSeg1/TSeg2/SJW directly.");
    }

    private static uint ToKvaserFlags(CanFrameFlags flags)
    {
        var nativeFlags = 0u;
        if (flags.HasFlag(CanFrameFlags.Extended)) nativeFlags |= KvaserCanlibNative.MessageExtended;
        if (flags.HasFlag(CanFrameFlags.RemoteTransmissionRequest)) nativeFlags |= KvaserCanlibNative.MessageRtr;
        if (flags.HasFlag(CanFrameFlags.ErrorFrame)) nativeFlags |= KvaserCanlibNative.MessageErrorFrame;
        return nativeFlags;
    }

    private static CanFrameFlags FromKvaserFlags(uint flags)
    {
        var frameFlags = CanFrameFlags.None;
        if ((flags & KvaserCanlibNative.MessageExtended) != 0) frameFlags |= CanFrameFlags.Extended;
        if ((flags & KvaserCanlibNative.MessageRtr) != 0) frameFlags |= CanFrameFlags.RemoteTransmissionRequest;
        if ((flags & KvaserCanlibNative.MessageErrorFrame) != 0) frameFlags |= CanFrameFlags.ErrorFrame;
        return frameFlags;
    }

    private static void CheckStatus(int status, string operation)
    {
        if (status != KvaserCanlibNative.StatusOk)
        {
            throw new InvalidOperationException($"{operation} failed: CANlib status {status}.");
        }
    }

    private void EnsureOpen()
    {
        if (Status != CanDeviceStatus.Open)
        {
            throw new InvalidOperationException("The Kvaser CAN device is not open.");
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
