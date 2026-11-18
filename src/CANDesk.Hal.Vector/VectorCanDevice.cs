using System.Runtime.Versioning;
using System.Threading.Channels;
using CANDesk.Hal;
using Microsoft.Win32.SafeHandles;

namespace CANDesk.Hal.Vector;

/// <summary>
/// <see cref="ICanDevice"/> adapter over one Vector XL channel (identified by its channel mask,
/// resolved by <see cref="VectorCanDeviceFactory"/> via <c>xlGetDriverConfig</c>).
/// Supports Classic CAN and ISO CAN FD.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class VectorCanDevice : ICanDevice
{
    private static readonly TimeSpan NotificationTimeout = TimeSpan.FromMilliseconds(50);

    private readonly ulong _channelMask;
    private readonly Channel<CanFrame> _frames;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _sessionCancellation;
    private IDisposable? _driverScope;
    private AutoResetEvent? _notification;
    private Task? _readLoop;
    private int _portHandle = -1;
    private bool _isCanFd;
    private long _dropped;
    private int _disposeState;
    // xlEvent.TimeStamp is nanoseconds since an arbitrary driver epoch, not wall-clock time.
    // xlResetClock zeroes it at Open so anchoring it to the wall clock here gives each frame a real
    // SystemTime instead of the dequeue-time DateTime.UtcNow this replaced, which added
    // notification/polling-loop jitter noise to Monitor/Trace's Cycle Time readings.
    private DateTime _timeBaseUtc;

    public VectorCanDevice(ulong channelMask, string channelName)
    {
        _channelMask = channelMask;
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
        cancellationToken.ThrowIfCancellationRequested();
        var fdConfig = config.IsCanFd ? VectorBitTiming.ToFdConfiguration(config) : default;

        SetStatus(CanDeviceStatus.Opening);
        IDisposable? driverScope = null;
        try
        {
            driverScope = VectorDriverScope.Acquire();
            if (config.IsCanFd)
            {
                var queryStatus = VectorXlNative.GetDriverConfig(out var driverConfig);
                if (queryStatus != 0)
                    throw new InvalidOperationException($"xlGetDriverConfig failed: XL status {queryStatus}.");
                if (!VectorXlNative.SupportsIsoCanFd(driverConfig, _channelMask))
                    throw new NotSupportedException("The selected Vector channel does not support ISO CAN FD.");
            }

            var permissionMask = _channelMask;
            var openStatus = VectorXlNative.OpenPort(out var portHandle, VectorXlNative.ApplicationName, _channelMask,
                ref permissionMask, config.IsCanFd ? 524_288u : 8192u,
                config.IsCanFd ? VectorXlNative.InterfaceVersionV4 : VectorXlNative.InterfaceVersionV3, VectorXlNative.BusTypeCan);
            if (openStatus != 0)
            {
                throw new InvalidOperationException($"xlOpenPort failed for channel mask 0x{_channelMask:X}: XL status {openStatus}.");
            }

            _portHandle = portHandle;

            // Only a port with init access (its bits set in permissionMask) may configure the bitrate;
            // when another application already owns and configured this channel, skip rather than fail.
            var initAccessMask = permissionMask & _channelMask;
            if (initAccessMask != 0)
            {
                var bitrateStatus = config.IsCanFd
                    ? VectorXlNative.CanFdSetConfiguration(portHandle, initAccessMask, fdConfig)
                    : VectorXlNative.CanSetChannelBitrate(portHandle, initAccessMask, (uint)config.Nominal.BitrateKbps * 1000u);
                if (bitrateStatus != 0)
                {
                    throw new InvalidOperationException($"{(config.IsCanFd ? "xlCanFdSetConfiguration" : "xlCanSetChannelBitrate")} returned XL status {bitrateStatus}.");
                }
            }
            else if (config.IsCanFd)
            {
                var queryStatus = VectorXlNative.GetDriverConfig(out var activeConfig);
                var channel = VectorXlNative.FindChannel(activeConfig, _channelMask);
                if (queryStatus != 0 || channel is null || !VectorBitTiming.MatchesActiveConfiguration(channel.Value, fdConfig))
                    throw new InvalidOperationException("Another application owns this Vector channel with different or unknown CAN FD timing. Match its ISO CAN FD settings or release the channel in that application.");
            }

            var activateStatus = VectorXlNative.ActivateChannel(portHandle, _channelMask, VectorXlNative.BusTypeCan, 0);
            if (activateStatus != 0)
            {
                throw new InvalidOperationException($"xlActivateChannel failed for channel mask 0x{_channelMask:X}: XL status {activateStatus}.");
            }

            // Best-effort: even if this fails, _timeBaseUtc is still anchored to "now" below, which
            // keeps Cycle Time deltas between consecutive frames correct regardless of whatever
            // absolute value the driver's clock happens to already be at.
            VectorXlNative.ResetClock(portHandle);
            _timeBaseUtc = DateTime.UtcNow;

            var notifyStatus = VectorXlNative.SetNotification(portHandle, out var xlHandle, queueLevel: 1);
            if (notifyStatus == 0 && xlHandle != 0)
            {
                _notification = new AutoResetEvent(false) { SafeWaitHandle = new SafeWaitHandle(xlHandle, ownsHandle: false) };
            }
            // else: fall back to plain polling in the read loop below.

            _driverScope = driverScope;
            _isCanFd = config.IsCanFd;
        }
        catch
        {
            if (_portHandle != -1)
            {
                VectorXlNative.ClosePort(_portHandle);
                _portHandle = -1;
            }
            _notification?.Dispose();
            _notification = null;
            driverScope?.Dispose();
            SetStatus(CanDeviceStatus.Faulted);
            throw;
        }

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
        _notification?.Set();
        if (_readLoop is not null)
        {
            await _readLoop.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        _readLoop = null;
        _sessionCancellation?.Dispose();
        _sessionCancellation = null;

        if (_portHandle != -1)
        {
            VectorXlNative.DeactivateChannel(_portHandle, _channelMask);
            VectorXlNative.ClosePort(_portHandle);
            _portHandle = -1;
        }
        _isCanFd = false;
        _notification?.Dispose();
        _notification = null;
        _driverScope?.Dispose();
        _driverScope = null;
    }

    public ValueTask SendAsync(CanFrame frame, CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        cancellationToken.ThrowIfCancellationRequested();

        if (_isCanFd)
        {
            var message = ToFdMessage(frame);
            var fdStatus = VectorXlNative.CanTransmitEx(_portHandle, _channelMask, 1, out var sentCount, message);
            if (fdStatus != 0 || sentCount != 1)
                throw new InvalidOperationException($"xlCanTransmitEx failed for channel mask 0x{_channelMask:X}: XL status {fdStatus}, sent {sentCount}/1.");
            return ValueTask.CompletedTask;
        }

        var xlEvent = ToClassicMessage(frame);

        var messageCount = 1u;
        var status = VectorXlNative.CanTransmit(_portHandle, _channelMask, ref messageCount, ref xlEvent);
        if (status != 0 || messageCount != 1)
        {
            throw new InvalidOperationException($"xlCanTransmit failed for channel mask 0x{_channelMask:X}: XL status {status}.");
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

    /// <summary>Vector's XL API has no direct bus-reset counterpart to PCAN's <c>CAN_Reset</c>;
    /// deactivating and reactivating the channel is the closest equivalent (clears the controller's
    /// error state the same way a physical bus-off recovery would).</summary>
    public Task ResetBusAsync(CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        VectorXlNative.DeactivateChannel(_portHandle, _channelMask);
        var status = VectorXlNative.ActivateChannel(_portHandle, _channelMask, VectorXlNative.BusTypeCan, 0);
        if (status != 0)
        {
            throw new InvalidOperationException($"xlActivateChannel (reset) failed for channel mask 0x{_channelMask:X}: XL status {status}.");
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
                var notification = _notification;
                if (notification is not null)
                {
                    await Task.Run(() => notification.WaitOne(NotificationTimeout), ct).ConfigureAwait(false);
                }
                else
                {
                    await Task.Delay(1, ct).ConfigureAwait(false);
                }

                if (ct.IsCancellationRequested)
                {
                    break;
                }

                if (_isCanFd) DrainFdReceiveQueue();
                else DrainReceiveQueue();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetStatus(CanDeviceStatus.Faulted);
            ErrorOccurred?.Invoke(this, new(CanErrorKind.Driver, exception.Message, exception));
        }
    }

    private void DrainFdReceiveQueue()
    {
        while (_sessionCancellation?.IsCancellationRequested == false)
        {
            var status = VectorXlNative.CanReceive(_portHandle, out var xlEvent);
            if (status == VectorXlNative.QueueIsEmpty) return;
            if (status != 0)
                throw new InvalidOperationException($"xlCanReceive failed: XL status {status}.");

            // TX_OK/TX_REQUEST are echoes; TxScheduler.FrameSent already reports transmission.
            if (xlEvent.Tag != VectorXlNative.CanFdEventTagReceiveOk) continue;
            if (xlEvent.Dlc > 15)
            {
                ErrorOccurred?.Invoke(this, new(CanErrorKind.Driver, $"xlCanReceive returned invalid DLC {xlEvent.Dlc}."));
                continue;
            }
            _frames.Writer.TryWrite(ToCanFrame(xlEvent, _timeBaseUtc));
        }
    }

    internal static VectorXlNative.XLevent ToClassicMessage(in CanFrame frame)
    {
        ValidateFrame(frame, allowFd: false);
        var message = new VectorXlNative.XLevent { Tag = VectorXlNative.EventTagTransmitMsg };
        var id = frame.Id | (frame.Flags.HasFlag(CanFrameFlags.Extended) ? VectorXlNative.ExtendedMessageIdFlag : 0);
        var flags = frame.Flags.HasFlag(CanFrameFlags.RemoteTransmissionRequest) ? VectorXlNative.CanMsgFlagRemoteFrame : (ushort)0;
        VectorXlNative.WriteCanMsg(ref message, id, flags, frame.PayloadLength, frame.PayloadSpan);
        return message;
    }

    internal static unsafe VectorXlNative.XLcanTxEvent ToFdMessage(in CanFrame frame)
    {
        ValidateFrame(frame, allowFd: true);
        var message = new VectorXlNative.XLcanTxEvent
        {
            Tag = VectorXlNative.CanFdEventTagTransmitMsg,
            CanId = frame.Id | (frame.Flags.HasFlag(CanFrameFlags.Extended) ? VectorXlNative.ExtendedMessageIdFlag : 0),
            MsgFlags = (frame.Flags.HasFlag(CanFrameFlags.Fd) ? VectorXlNative.CanFdMsgFlagEdl : 0)
                | (frame.Flags.HasFlag(CanFrameFlags.BitRateSwitch) ? VectorXlNative.CanFdMsgFlagBrs : 0)
                | (frame.Flags.HasFlag(CanFrameFlags.RemoteTransmissionRequest) ? VectorXlNative.CanFdMsgFlagRtr : 0),
            Dlc = CanFrame.GetDlc(frame.PayloadLength)
        };
        frame.PayloadSpan.CopyTo(new Span<byte>(message.Data, 64));
        return message;
    }

    internal static unsafe CanFrame ToCanFrame(in VectorXlNative.XLcanRxEvent message, DateTime timeBaseUtc)
    {
        if (message.Dlc > 15) throw new ArgumentOutOfRangeException(nameof(message), "Vector DLC must be 0-15.");
        var flags = CanFrameFlags.None;
        if ((message.CanId & VectorXlNative.ExtendedMessageIdFlag) != 0) flags |= CanFrameFlags.Extended;
        if ((message.MsgFlags & VectorXlNative.CanFdMsgFlagEdl) != 0) flags |= CanFrameFlags.Fd;
        if ((message.MsgFlags & VectorXlNative.CanFdMsgFlagBrs) != 0) flags |= CanFrameFlags.BitRateSwitch;
        if ((message.MsgFlags & VectorXlNative.CanFdMsgFlagRtr) != 0) flags |= CanFrameFlags.RemoteTransmissionRequest;
        if ((message.MsgFlags & VectorXlNative.CanFdRxMsgFlagErrorFrame) != 0) flags |= CanFrameFlags.ErrorFrame;
        var length = flags.HasFlag(CanFrameFlags.Fd)
            ? message.Dlc switch { <= 8 => message.Dlc, 9 => 12, 10 => 16, 11 => 20, 12 => 24, 13 => 32, 14 => 48, _ => 64 }
            : Math.Min(message.Dlc, (byte)8);
        fixed (byte* data = message.Data)
            return CanFrame.Create(message.CanId & ~VectorXlNative.ExtendedMessageIdFlag,
                new ReadOnlySpan<byte>(data, length), flags, message.TimeStamp / 1000,
                timeBaseUtc.AddTicks(checked((long)(message.TimeStamp / 100))));
    }

    private static void ValidateFrame(in CanFrame frame, bool allowFd)
    {
        var fd = frame.Flags.HasFlag(CanFrameFlags.Fd);
        var rtr = frame.Flags.HasFlag(CanFrameFlags.RemoteTransmissionRequest);
        var brs = frame.Flags.HasFlag(CanFrameFlags.BitRateSwitch);
        if ((!allowFd && fd) || frame.PayloadLength > 64 || (!fd && frame.PayloadLength > 8)
            || (fd && rtr) || (brs && !fd) || frame.Flags.HasFlag(CanFrameFlags.ErrorFrame))
            throw new ArgumentException("Invalid payload or flags for the selected Vector CAN mode.", nameof(frame));
        if (frame.Id > (frame.Flags.HasFlag(CanFrameFlags.Extended) ? 0x1FFF_FFFFu : 0x7FFu))
            throw new ArgumentException("CAN ID exceeds the selected frame format.", nameof(frame));
    }

    private void DrainReceiveQueue()
    {
        while (true)
        {
            var eventCount = 1u;
            var status = VectorXlNative.Receive(_portHandle, ref eventCount, out var xlEvent);
            if (status != 0)
            {
                return;
            }

            if (xlEvent.Tag != VectorXlNative.EventTagReceiveMsg)
            {
                continue;
            }

            var (id, flags, dlc, data) = VectorXlNative.ReadCanMsg(xlEvent);
            if ((flags & VectorXlNative.CanMsgFlagTxRequest) != 0)
            {
                // Echo of our own transmit; TxScheduler.FrameSent already covers TX observability.
                continue;
            }

            var canFlags = CanFrameFlags.None;
            if ((id & VectorXlNative.ExtendedMessageIdFlag) != 0) canFlags |= CanFrameFlags.Extended;
            if ((flags & VectorXlNative.CanMsgFlagRemoteFrame) != 0) canFlags |= CanFrameFlags.RemoteTransmissionRequest;
            if ((flags & VectorXlNative.CanMsgFlagErrorFrame) != 0) canFlags |= CanFrameFlags.ErrorFrame;

            var length = Math.Min(dlc, (ushort)8);
            var systemTime = _timeBaseUtc.AddTicks((long)(xlEvent.TimeStamp / 100UL)); // 100 ns per tick
            var frame = CanFrame.Create(id & ~VectorXlNative.ExtendedMessageIdFlag, data.AsSpan(0, length), canFlags, systemTime: systemTime);
            _frames.Writer.TryWrite(frame);
        }
    }

    private void EnsureOpen()
    {
        if (Status != CanDeviceStatus.Open)
        {
            throw new InvalidOperationException("The Vector CAN device is not open.");
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
