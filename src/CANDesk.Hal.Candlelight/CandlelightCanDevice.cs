using System.Buffers.Binary;
using System.Runtime.Versioning;
using System.Threading.Channels;
using CANDesk.Hal;

namespace CANDesk.Hal.Candlelight;

/// <summary>
/// CANable/candleLight adapter over WinUSB. Classic CAN can use the legacy gs_usb 20-byte frame;
/// CAN-FD uses the ElmueSoft variable-length protocol implemented by the reference checker.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class CandlelightCanDevice : ICanDevice
{
    private const int ChannelIndex = 0;
    private const uint CanEffFlag = 0x8000_0000;
    private const uint CanRtrFlag = 0x4000_0000;
    private const uint CanErrFlag = 0x2000_0000;
    private const uint CanSffMask = 0x0000_07FF;
    private const uint CanEffMask = 0x1FFF_FFFF;
    private const uint RxEchoId = 0xFFFF_FFFF;
    private const int HostFrameLength = 20;
    private const byte RequestHostFormat = 0;
    private const byte RequestBitTiming = 1;
    private const byte RequestMode = 2;
    private const byte RequestBtConst = 4;
    private const byte RequestDeviceConfig = 5;
    private const byte RequestBitTimingFd = 10;
    private const byte RequestBtConstFd = 11;
    private const uint ModeReset = 0;
    private const uint ModeStart = 1;
    private const uint DeviceFlagCanFd = 0x0000_0100;
    private const uint DeviceFlagBitTimingFd = 0x0000_0400;
    private const uint DeviceFlagProtocolElmue = 0x0000_4000;
    private const byte ElmueMessageTxFrame = 10;
    private const byte ElmueMessageTxEcho = 11;
    private const byte ElmueMessageRxFrame = 12;
    private const byte ElmueMessageError = 13;
    private const byte ElmueFrameFlagFd = 0x02;
    private const byte ElmueFrameFlagBitRateSwitch = 0x04;
    private const int ElmueTxFrameHeaderLength = 8;
    private const int ElmueRxFrameHeaderLength = 7;
    private const int ElmueErrorFrameLength = 14;
    private static readonly TimeSpan ElmueErrorRepeatWindow = TimeSpan.FromSeconds(1);
    private const uint ErrorBusOff = 0x0000_0040;
    private const uint ErrorNoAckReceived = 0x0000_0020;
    private const uint ErrorCrc = 0x0000_0200;
    private const byte ErrorRxWarning = 0x04;
    private const byte ErrorTxWarning = 0x08;
    private const byte ErrorRxPassive = 0x10;
    private const byte ErrorTxPassive = 0x20;
    private const byte ErrorBackActive = 0x40;
    private const byte ErrorBitStuffing = 0x04;
    private const byte ErrorFrameFormat = 0x02;
    private const byte ErrorDominantBit = 0x08;
    private const byte ErrorRecessiveBit = 0x10;
    private const byte ErrorAppRxFailed = 0x01;
    private const byte ErrorAppTxFailed = 0x02;
    private const byte ErrorAppCanTxOverflow = 0x04;
    private const byte ErrorAppUsbInOverflow = 0x08;
    private const byte ErrorAppTxTimeout = 0x10;

    private readonly string _devicePath;
    private readonly Channel<CanFrame> _frames;
    private readonly CancellationTokenSource _lifetime = new();
    private WinUsbDevice? _device;
    private CancellationTokenSource? _sessionCancellation;
    private Task? _readLoop;
    private uint _nextEchoId;
    private long _dropped;
    private int _disposeState;
    private bool _useElmueProtocol;
    private string? _lastElmueError;
    private DateTime _lastElmueErrorAtUtc;
    private int _suppressedElmueErrorCount;

    public CandlelightCanDevice(string devicePath, string channelName)
    {
        _devicePath = devicePath;
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

        SetStatus(CanDeviceStatus.Opening);
        WinUsbDevice? device = null;
        try
        {
            device = WinUsbDevice.Open(_devicePath);
            SendHostFormat(device);
            _ = ReadDeviceConfig(device);
            SetMode(device, ModeReset, DeviceFlagProtocolElmue);
            var capability = ReadBitTimingConstants(device);
            var useElmueProtocol = config.Mode == CanBusMode.Fd;
            if (useElmueProtocol && !capability.SupportsCanFd)
            {
                throw new NotSupportedException("The CANable Candlelight firmware does not report CAN-FD + BitTimingFD support.");
            }

            SetBitTiming(device, RequestBitTiming, config.NominalTiming, capability.ClockFrequencyHz);
            if (config.Mode == CanBusMode.Fd)
            {
                var fdCapability = ReadFdBitTimingConstants(device);
                SetBitTiming(device, RequestBitTimingFd, config.DataTiming!, fdCapability.ClockFrequencyHz);
            }

            SetMode(device, ModeStart, useElmueProtocol ? DeviceFlagProtocolElmue : 0);
            _useElmueProtocol = useElmueProtocol;
            _device = device;
        }
        catch
        {
            device?.Dispose();
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
        _device?.AbortRead();
        if (_readLoop is not null)
        {
            await _readLoop.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        _readLoop = null;
        _sessionCancellation?.Dispose();
        _sessionCancellation = null;

        if (_device is not null)
        {
            try
            {
                SetMode(_device, ModeReset, DeviceFlagProtocolElmue);
            }
            catch (InvalidOperationException exception)
            {
                ErrorOccurred?.Invoke(this, new(CanErrorKind.Driver, exception.Message, exception));
            }

            _device.Dispose();
            _device = null;
        }
    }

    public ValueTask SendAsync(CanFrame frame, CancellationToken cancellationToken = default)
    {
        var device = EnsureOpen();
        if (_useElmueProtocol || frame.Flags.HasFlag(CanFrameFlags.Fd))
        {
            WriteElmueFrame(device, frame);
            return ValueTask.CompletedTask;
        }

        Span<byte> buffer = stackalloc byte[HostFrameLength];
        var echoId = Interlocked.Increment(ref _nextEchoId) % 10;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[0..4], echoId);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[4..8], ToGsCanId(frame));
        buffer[8] = frame.Dlc;
        buffer[9] = ChannelIndex;
        buffer[10] = 0;
        buffer[11] = 0;
        frame.PayloadSpan.CopyTo(buffer[12..]);
        device.WriteBulk(buffer);
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
        var device = EnsureOpen();
        var flags = _useElmueProtocol ? DeviceFlagProtocolElmue : 0;
        SetMode(device, ModeReset, flags);
        SetMode(device, ModeStart, flags);
        SetStatus(CanDeviceStatus.Open);
        return Task.CompletedTask;
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[128];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var device = _device;
                if (device is null)
                {
                    break;
                }

                var bytesRead = device.ReadBulk(buffer);
                if (bytesRead == 0)
                {
                    await Task.Delay(1, ct).ConfigureAwait(false);
                    continue;
                }

                var frame = _useElmueProtocol
                    ? TryParseElmueFrame(buffer.AsSpan(0, bytesRead))
                    : bytesRead >= HostFrameLength ? TryParseLegacyFrame(buffer) : null;
                if (frame is not null)
                {
                    _frames.Writer.TryWrite(frame.Value);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (InvalidOperationException exception)
        {
            if (!ct.IsCancellationRequested)
            {
                SetStatus(CanDeviceStatus.Faulted);
                ErrorOccurred?.Invoke(this, new(CanErrorKind.Driver, exception.Message, exception));
            }
        }
    }

    private CanFrame? TryParseLegacyFrame(ReadOnlySpan<byte> buffer)
    {
        var echoId = BinaryPrimitives.ReadUInt32LittleEndian(buffer[0..4]);
        if (echoId != RxEchoId)
        {
            return null;
        }

        var rawId = BinaryPrimitives.ReadUInt32LittleEndian(buffer[4..8]);
        var flags = CanFrameFlags.None;
        if ((rawId & CanEffFlag) != 0) flags |= CanFrameFlags.Extended;
        if ((rawId & CanRtrFlag) != 0) flags |= CanFrameFlags.RemoteTransmissionRequest;
        if ((rawId & CanErrFlag) != 0)
        {
            flags |= CanFrameFlags.ErrorFrame;
            ErrorOccurred?.Invoke(this, new(CanErrorKind.Driver, $"CANable Candlelight reported an error frame: 0x{rawId:X8}."));
            if ((rawId & 0x0000_0040) != 0)
            {
                SetStatus(CanDeviceStatus.BusOff);
            }
        }

        var length = Math.Min(buffer[8], (byte)8);
        var id = rawId & ((rawId & CanEffFlag) != 0 ? CanEffMask : CanSffMask);
        return CanFrame.Create(id, buffer.Slice(12, length), flags, systemTime: DateTime.UtcNow);
    }

    private CanFrame? TryParseElmueFrame(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 2 || buffer[0] > buffer.Length)
        {
            return null;
        }

        var size = buffer[0];
        var messageType = buffer[1];
        if (messageType == ElmueMessageTxEcho)
        {
            return null;
        }

        if (messageType == ElmueMessageError)
        {
            ReportElmueError(buffer[..size]);
            return null;
        }

        if (messageType != ElmueMessageRxFrame || size < ElmueRxFrameHeaderLength)
        {
            return null;
        }

        var frameFlags = buffer[2];
        var rawId = BinaryPrimitives.ReadUInt32LittleEndian(buffer[3..7]);
        var flags = CanFrameFlags.None;
        if ((rawId & CanEffFlag) != 0) flags |= CanFrameFlags.Extended;
        if ((rawId & CanRtrFlag) != 0) flags |= CanFrameFlags.RemoteTransmissionRequest;
        if ((frameFlags & ElmueFrameFlagFd) != 0) flags |= CanFrameFlags.Fd;
        if ((frameFlags & ElmueFrameFlagBitRateSwitch) != 0) flags |= CanFrameFlags.BitRateSwitch;

        var length = Math.Min(size - ElmueRxFrameHeaderLength, 64);
        var id = rawId & ((rawId & CanEffFlag) != 0 ? CanEffMask : CanSffMask);
        return CanFrame.Create(id, buffer.Slice(ElmueRxFrameHeaderLength, length), flags, systemTime: DateTime.UtcNow);
    }

    private void ReportElmueError(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < ElmueErrorFrameLength)
        {
            ErrorOccurred?.Invoke(this, new(CanErrorKind.Driver,
                $"CANable Candlelight reported a short ElmueSoft error frame ({buffer.Length} bytes)."));
            return;
        }

        var errorId = BinaryPrimitives.ReadUInt32LittleEndian(buffer[2..6]);
        var errorData = buffer[6..14];
        var message = FormatElmueError(errorId, errorData);
        var kind = ClassifyElmueError(errorId, errorData);
        if (kind == CanErrorKind.BusOff)
        {
            SetStatus(CanDeviceStatus.BusOff);
        }

        ReportDeduplicatedElmueError(kind,
            $"CANable Candlelight ElmueSoft error: {message} (ErrID=0x{errorId:X8}, Data={Convert.ToHexString(errorData)}).");
    }

    private void ReportDeduplicatedElmueError(CanErrorKind kind, string message)
    {
        var now = DateTime.UtcNow;
        if (string.Equals(message, _lastElmueError, StringComparison.Ordinal) &&
            now - _lastElmueErrorAtUtc < ElmueErrorRepeatWindow)
        {
            _suppressedElmueErrorCount++;
            return;
        }

        if (_suppressedElmueErrorCount > 0 && _lastElmueError is not null)
        {
            ErrorOccurred?.Invoke(this, new(kind, $"{_lastElmueError} Repeated {_suppressedElmueErrorCount:N0} more times."));
        }

        _lastElmueError = message;
        _lastElmueErrorAtUtc = now;
        _suppressedElmueErrorCount = 0;
        ErrorOccurred?.Invoke(this, new(kind, message));
    }

    private static string FormatElmueError(uint errorId, ReadOnlySpan<byte> errorData)
    {
        var parts = new List<string>();
        var busFlags = errorData[1];
        var protocolFlags = errorData[2];
        var appFlags = errorData[5];

        if ((errorId & ErrorBusOff) != 0) parts.Add("Bus Off");
        else if ((busFlags & (ErrorRxPassive | ErrorTxPassive)) != 0) parts.Add("Bus Passive");
        else if ((busFlags & (ErrorRxWarning | ErrorTxWarning)) != 0) parts.Add("Bus Warning");
        else if ((busFlags & ErrorBackActive) != 0) parts.Add("Back to Active");
        else parts.Add("Bus Active");

        if ((appFlags & ErrorAppRxFailed) != 0) parts.Add("Rx Failed");
        if ((appFlags & ErrorAppTxFailed) != 0) parts.Add("Tx Failed");
        if ((appFlags & ErrorAppTxTimeout) != 0) parts.Add("Tx Timeout");
        if ((appFlags & ErrorAppCanTxOverflow) != 0) parts.Add("CAN Tx Overflow");
        if ((appFlags & ErrorAppUsbInOverflow) != 0) parts.Add("USB IN Overflow");
        if ((errorId & ErrorNoAckReceived) != 0) parts.Add("No ACK received");
        if ((errorId & ErrorCrc) != 0) parts.Add("CRC Error");
        if ((protocolFlags & ErrorBitStuffing) != 0) parts.Add("Bit Stuffing Error");
        if ((protocolFlags & ErrorFrameFormat) != 0) parts.Add("Frame Format Error");
        if ((protocolFlags & ErrorDominantBit) != 0) parts.Add("Dominant Bit Error");
        if ((protocolFlags & ErrorRecessiveBit) != 0) parts.Add("Recessive Bit Error");
        if (errorData[6] > 0) parts.Add($"Tx Errors: {errorData[6]}");
        if (errorData[7] > 0) parts.Add($"Rx Errors: {errorData[7]}");

        return string.Join(", ", parts);
    }

    private static CanErrorKind ClassifyElmueError(uint errorId, ReadOnlySpan<byte> errorData)
    {
        if ((errorId & ErrorBusOff) != 0)
        {
            return CanErrorKind.BusOff;
        }

        if ((errorData[1] & (ErrorRxPassive | ErrorTxPassive)) != 0)
        {
            return CanErrorKind.ErrorPassive;
        }

        if ((errorData[1] & (ErrorRxWarning | ErrorTxWarning)) != 0)
        {
            return CanErrorKind.ErrorWarning;
        }

        if ((errorId & ErrorCrc) != 0)
        {
            return CanErrorKind.Crc;
        }

        if ((errorData[2] & (ErrorBitStuffing | ErrorFrameFormat | ErrorDominantBit | ErrorRecessiveBit)) != 0)
        {
            return CanErrorKind.Bit;
        }

        if ((errorData[5] & (ErrorAppTxFailed | ErrorAppTxTimeout | ErrorAppCanTxOverflow)) != 0)
        {
            return CanErrorKind.TransmitFailure;
        }

        if ((errorData[5] & ErrorAppUsbInOverflow) != 0)
        {
            return CanErrorKind.ReceiveOverflow;
        }

        return CanErrorKind.Driver;
    }

    private static void SendHostFormat(WinUsbDevice device)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, 0x0000_BEEF);
        device.ControlOut(RequestHostFormat, value: 1, buffer);
    }

    private static CandlelightDeviceConfig ReadDeviceConfig(WinUsbDevice device)
    {
        Span<byte> buffer = stackalloc byte[12];
        device.ControlIn(RequestDeviceConfig, value: 1, buffer);
        return new CandlelightDeviceConfig(buffer[3] + 1, BinaryPrimitives.ReadUInt32LittleEndian(buffer[4..8]),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer[8..12]));
    }

    private static CandlelightBitTimingConstants ReadBitTimingConstants(WinUsbDevice device)
    {
        Span<byte> buffer = stackalloc byte[40];
        device.ControlIn(RequestBtConst, ChannelIndex, buffer);
        var features = BinaryPrimitives.ReadUInt32LittleEndian(buffer[0..4]);
        return new CandlelightBitTimingConstants(BinaryPrimitives.ReadUInt32LittleEndian(buffer[4..8]),
            (features & DeviceFlagCanFd) != 0 && (features & DeviceFlagBitTimingFd) != 0);
    }

    private static CandlelightBitTimingConstants ReadFdBitTimingConstants(WinUsbDevice device)
    {
        Span<byte> buffer = stackalloc byte[72];
        device.ControlIn(RequestBtConstFd, ChannelIndex, buffer);
        return new CandlelightBitTimingConstants(BinaryPrimitives.ReadUInt32LittleEndian(buffer[4..8]), SupportsCanFd: true);
    }

    private static void SetBitTiming(WinUsbDevice device, byte request, BitTimingConfig timing, uint clockFrequencyHz)
    {
        var raw = timing.InputMode == BitTimingInputMode.RawSegments
            ? timing.RawSegments!
            : new BitTimingCalculator().Calculate(timing.Preset!, checked((int)clockFrequencyHz));

        Span<byte> buffer = stackalloc byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[0..4], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[4..8], (uint)raw.Tseg1);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[8..12], (uint)raw.Tseg2);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[12..16], (uint)raw.Sjw);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[16..20], (uint)raw.Prescaler);
        device.ControlOut(request, ChannelIndex, buffer);
    }

    private static void SetMode(WinUsbDevice device, uint mode, uint flags)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[0..4], mode);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[4..8], flags);
        device.ControlOut(RequestMode, ChannelIndex, buffer);
    }

    private void WriteElmueFrame(WinUsbDevice device, CanFrame frame)
    {
        var length = frame.PayloadLength;
        var isFdFrame = frame.Flags.HasFlag(CanFrameFlags.Fd) || _useElmueProtocol || length > 8;
        if (!isFdFrame && length > 8)
        {
            throw new InvalidOperationException("Classic CAN frames cannot carry more than 8 data bytes.");
        }

        Span<byte> buffer = stackalloc byte[ElmueTxFrameHeaderLength + 64];
        buffer[0] = (byte)(ElmueTxFrameHeaderLength + length);
        buffer[1] = ElmueMessageTxFrame;
        buffer[2] = ToElmueFrameFlags(frame, isFdFrame);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[3..7], ToGsCanId(frame));
        buffer[7] = (byte)Interlocked.Increment(ref _nextEchoId);
        frame.PayloadSpan.CopyTo(buffer[ElmueTxFrameHeaderLength..]);
        device.WriteBulk(buffer[..buffer[0]]);
    }

    private byte ToElmueFrameFlags(CanFrame frame, bool isFdFrame)
    {
        byte flags = 0;
        if (isFdFrame) flags |= ElmueFrameFlagFd;
        if (frame.Flags.HasFlag(CanFrameFlags.BitRateSwitch) || (_useElmueProtocol && isFdFrame))
        {
            flags |= ElmueFrameFlagBitRateSwitch;
        }

        return flags;
    }

    private static uint ToGsCanId(CanFrame frame)
    {
        var id = frame.Id;
        if (frame.Flags.HasFlag(CanFrameFlags.Extended)) id |= CanEffFlag;
        if (frame.Flags.HasFlag(CanFrameFlags.RemoteTransmissionRequest)) id |= CanRtrFlag;
        if (frame.Flags.HasFlag(CanFrameFlags.ErrorFrame)) id |= CanErrFlag;
        return id;
    }

    private WinUsbDevice EnsureOpen()
    {
        return Status == CanDeviceStatus.Open && _device is not null
            ? _device
            : throw new InvalidOperationException("The CANable Candlelight device is not open.");
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

    private readonly record struct CandlelightDeviceConfig(int ChannelCount, uint SoftwareVersion, uint HardwareVersion);
    private readonly record struct CandlelightBitTimingConstants(uint ClockFrequencyHz, bool SupportsCanFd);
}
