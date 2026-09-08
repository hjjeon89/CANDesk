using System.Runtime.Versioning;
using System.Threading.Channels;
using CANDesk.Hal;
using Microsoft.Win32.SafeHandles;

namespace CANDesk.Hal.Vector;

/// <summary>
/// <see cref="ICanDevice"/> adapter over one Vector XL channel (identified by its channel mask,
/// resolved by <see cref="VectorCanDeviceFactory"/> via <c>xlGetApplConfig</c>/
/// <c>xlGetChannelMask</c>). Classic CAN only; see <see cref="VectorXlNative"/> remarks.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class VectorCanDevice(ulong channelMask, string channelName) : ICanDevice
{
    private static readonly TimeSpan NotificationTimeout = TimeSpan.FromMilliseconds(50);

    private readonly Channel<CanFrame> _frames = Channel.CreateBounded<CanFrame>(
        new BoundedChannelOptions(32_768) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _sessionCancellation;
    private IDisposable? _driverScope;
    private AutoResetEvent? _notification;
    private Task? _readLoop;
    private int _portHandle;
    private int _disposeState;

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
            throw new NotSupportedException("Vector CAN-FD support is not implemented yet (Classic CAN only in this release).");
        }

        SetStatus(CanDeviceStatus.Opening);
        var driverScope = VectorDriverScope.Acquire();
        try
        {
            var openStatus = VectorXlNative.OpenPort(out var portHandle, VectorXlNative.ApplicationName, channelMask,
                out var permissionMask, rxQueueSize: 8192, VectorXlNative.InterfaceVersionV3, VectorXlNative.BusTypeCan);
            if (openStatus != 0)
            {
                throw new InvalidOperationException($"xlOpenPort failed for channel mask 0x{channelMask:X}: XL status {openStatus}.");
            }

            _portHandle = portHandle;

            // Only a port with init access (its bits set in permissionMask) may configure the bitrate;
            // when another application already owns and configured this channel, skip rather than fail.
            var initAccessMask = permissionMask & channelMask;
            if (initAccessMask != 0)
            {
                var bitrateStatus = VectorXlNative.CanSetChannelBitrate(portHandle, initAccessMask, (uint)config.Nominal.BitrateKbps * 1000u);
                if (bitrateStatus != 0)
                {
                    ErrorOccurred?.Invoke(this, new(CanErrorKind.Driver, $"xlCanSetChannelBitrate returned XL status {bitrateStatus}."));
                }
            }

            var activateStatus = VectorXlNative.ActivateChannel(portHandle, channelMask, VectorXlNative.BusTypeCan, 0);
            if (activateStatus != 0)
            {
                throw new InvalidOperationException($"xlActivateChannel failed for channel mask 0x{channelMask:X}: XL status {activateStatus}.");
            }

            var notifyStatus = VectorXlNative.SetNotification(portHandle, out var xlHandle, queueLevel: 1);
            if (notifyStatus == 0 && xlHandle != 0)
            {
                _notification = new AutoResetEvent(false) { SafeWaitHandle = new SafeWaitHandle(xlHandle, ownsHandle: false) };
            }
            // else: fall back to plain polling in the read loop below.

            _driverScope = driverScope;
        }
        catch
        {
            driverScope.Dispose();
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

        VectorXlNative.DeactivateChannel(_portHandle, channelMask);
        VectorXlNative.ClosePort(_portHandle);
        _notification?.Dispose();
        _notification = null;
        _driverScope?.Dispose();
        _driverScope = null;
    }

    public ValueTask SendAsync(CanFrame frame, CancellationToken cancellationToken = default)
    {
        EnsureOpen();

        var id = frame.Id | (frame.Flags.HasFlag(CanFrameFlags.Extended) ? VectorXlNative.ExtendedMessageIdFlag : 0);
        var flags = frame.Flags.HasFlag(CanFrameFlags.RemoteTransmissionRequest) ? VectorXlNative.CanMsgFlagRemoteFrame : (ushort)0;
        var xlEvent = new VectorXlNative.XLevent { Tag = VectorXlNative.EventTagTransmitMsg };
        VectorXlNative.WriteCanMsg(ref xlEvent, id, flags, frame.PayloadLength, frame.PayloadSpan);

        var messageCount = 1u;
        var status = VectorXlNative.CanTransmit(_portHandle, channelMask, ref messageCount, ref xlEvent);
        if (status != 0)
        {
            throw new InvalidOperationException($"xlCanTransmit failed for channel mask 0x{channelMask:X}: XL status {status}.");
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
        VectorXlNative.DeactivateChannel(_portHandle, channelMask);
        var status = VectorXlNative.ActivateChannel(_portHandle, channelMask, VectorXlNative.BusTypeCan, 0);
        if (status != 0)
        {
            throw new InvalidOperationException($"xlActivateChannel (reset) failed for channel mask 0x{channelMask:X}: XL status {status}.");
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

                DrainReceiveQueue();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
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
            var frame = CanFrame.Create(id & ~VectorXlNative.ExtendedMessageIdFlag, data.AsSpan(0, length), canFlags);
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
