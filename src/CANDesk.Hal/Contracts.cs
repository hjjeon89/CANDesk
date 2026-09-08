namespace CANDesk.Hal;

public enum CanDeviceStatus { Closed, Opening, Open, BusOff, Faulted }
public enum CanErrorKind { BusOff, ErrorWarning, ErrorPassive, Crc, Bit, ReceiveOverflow, TransmitFailure, Driver }
public sealed record CanDeviceDescriptor(string Vendor, string DeviceId, string DisplayName, IReadOnlyList<string> Channels,
    bool SupportsCanFd, int? ClockFrequencyHz = null);
public enum CanBusMode { Classic, Fd }

/// <summary>Specifies whether a timing phase is configured from a bitrate preset or controller register segments.</summary>
public enum BitTimingInputMode { Preset, RawSegments }

/// <summary>Timing for a single CAN arbitration or CAN-FD data phase.</summary>
public sealed record BitTimingSetting(int BitrateKbps, double SamplePointPercent, bool IsCustom = false)
{
    public override string ToString() => $"{BitrateKbps:N0} kbps / {SamplePointPercent:0.#}%";
}

/// <summary>Controller-level timing values for fine tuning a nominal or CAN-FD data phase.</summary>
public sealed record RawBitTimingSegments(int Prescaler, int Tseg1, int Tseg2, int Sjw)
{
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(Prescaler);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(Tseg1);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(Tseg2);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(Sjw);
        if (Sjw > Tseg2)
        {
            throw new ArgumentOutOfRangeException(nameof(Sjw), "SJW cannot exceed TSeg2.");
        }
    }
}

/// <summary>Timing input for one bus phase. Exactly one representation is active based on <see cref="InputMode"/>.</summary>
public sealed record BitTimingConfig
{
    public BitTimingInputMode InputMode { get; init; } = BitTimingInputMode.Preset;
    public BitTimingSetting? Preset { get; init; } = new(500, 87.5);
    public RawBitTimingSegments? RawSegments { get; init; }

    public void Validate()
    {
        if (InputMode == BitTimingInputMode.Preset)
        {
            if (Preset is null) throw new InvalidOperationException("Preset timing requires a bitrate and sample point.");
            if (Preset.BitrateKbps <= 0 || Preset.SamplePointPercent is <= 0 or > 100)
                throw new ArgumentOutOfRangeException(nameof(Preset), "Preset timing is outside the supported range.");
            return;
        }

        if (RawSegments is null) throw new InvalidOperationException("Raw segment timing requires prescaler, TSeg1, TSeg2, and SJW.");
        RawSegments.Validate();
    }
}

/// <summary>
/// Bus mode and timing are intentionally separated: classic CAN has only Nominal,
/// whereas CAN-FD also requires an independent Data phase timing.
/// </summary>
public sealed record CanBusConfig
{
    public CanBusMode Mode { get; init; } = CanBusMode.Classic;
    public BitTimingSetting Nominal { get; init; } = new(500, 87.5);
    public BitTimingSetting? Data { get; init; }
    public BitTimingConfig NominalTiming { get; init; } = new();
    public BitTimingConfig? DataTiming { get; init; }
    public bool IsCanFd => Mode == CanBusMode.Fd;

    public CanBusConfig() { }
    public CanBusConfig(uint nominalBitRate, uint? dataBitRate = null, double? nominalSamplePoint = null,
        double? dataSamplePoint = null, bool enableCanFd = false)
    {
        Mode = enableCanFd || dataBitRate.HasValue ? CanBusMode.Fd : CanBusMode.Classic;
        Nominal = new((int)(nominalBitRate / 1_000), nominalSamplePoint ?? 87.5);
        Data = Mode == CanBusMode.Fd && dataBitRate.HasValue
            ? new((int)(dataBitRate.Value / 1_000), dataSamplePoint ?? 80.0)
            : null;
        NominalTiming = new BitTimingConfig { Preset = Nominal };
        DataTiming = Data is null ? null : new BitTimingConfig { Preset = Data };
    }

    /// <summary>Reject incomplete or contradictory bus timing before a vendor driver opens a channel.</summary>
    public void Validate()
    {
        ValidateTiming(Nominal, nameof(Nominal));
        if (Mode == CanBusMode.Fd && Data is null)
            throw new InvalidOperationException("CAN-FD requires a data phase bit timing setting.");
        if (Mode == CanBusMode.Classic && Data is not null)
            throw new InvalidOperationException("Classic CAN must not define CAN-FD data phase timing.");
        if (Data is not null) ValidateTiming(Data, nameof(Data));
        NominalTiming.Validate();
        if (Mode == CanBusMode.Classic && DataTiming is not null)
            throw new InvalidOperationException("Classic CAN must not define a CAN-FD data timing configuration.");
        DataTiming?.Validate();
    }

    private static void ValidateTiming(BitTimingSetting timing, string propertyName)
    {
        if (timing.BitrateKbps <= 0)
            throw new ArgumentOutOfRangeException(propertyName, "Bitrate must be positive.");
        if (timing.SamplePointPercent is <= 0 or > 100)
            throw new ArgumentOutOfRangeException(propertyName, "Sample point must be greater than 0 and no more than 100 percent.");
    }
}

public interface IBitrateTableProvider
{
    IReadOnlyList<BitTimingSetting> GetNominalPresets();
    IReadOnlyList<BitTimingSetting> GetFdDataPhasePresets();
    void RegisterCustomPreset(BitTimingSetting setting);
}

/// <summary>Converts a bitrate/sample-point request for legacy SDKs that require controller register values.</summary>
public interface IBitTimingCalculator
{
    RawBitTimingSegments Calculate(BitTimingSetting setting, int deviceClockHz);
    (int BitrateKbps, double SamplePointPercent) Describe(RawBitTimingSegments segments, int deviceClockHz);
}

/// <summary>Generic CAN controller timing calculator for SDKs that expose BRP/TSEG/SJW registers.</summary>
public sealed class BitTimingCalculator : IBitTimingCalculator
{
    public RawBitTimingSegments Calculate(BitTimingSetting setting, int deviceClockHz)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(deviceClockHz);
        if (setting.BitrateKbps <= 0 || setting.SamplePointPercent is <= 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(setting));

        var requestedBitrate = setting.BitrateKbps * 1_000d;
        var bestError = double.MaxValue;
        RawBitTimingSegments? best = null;
        for (var totalTq = 8; totalTq <= 25; totalTq++)
        {
            var prescaler = (int)Math.Round(deviceClockHz / (requestedBitrate * totalTq));
            if (prescaler <= 0) continue;

            var tseg1 = (int)Math.Round(totalTq * setting.SamplePointPercent / 100d) - 1;
            var tseg2 = totalTq - 1 - tseg1;
            if (tseg1 <= 0 || tseg2 <= 0) continue;

            var bitrate = deviceClockHz / (double)(prescaler * totalTq);
            var samplePoint = (1d + tseg1) / totalTq * 100d;
            var error = Math.Abs(bitrate - requestedBitrate) / requestedBitrate
                + Math.Abs(samplePoint - setting.SamplePointPercent) / 10_000d;
            if (error >= bestError) continue;

            bestError = error;
            best = new(prescaler, tseg1, tseg2, Math.Min(4, tseg2));
        }

        if (best is null)
            throw new InvalidOperationException("The requested timing cannot be represented by the controller clock.");
        return best;
    }

    public (int BitrateKbps, double SamplePointPercent) Describe(RawBitTimingSegments segments, int deviceClockHz)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(deviceClockHz);
        segments.Validate();
        var totalTq = 1 + segments.Tseg1 + segments.Tseg2;
        var bitrateKbps = (int)Math.Round(deviceClockHz / (double)(segments.Prescaler * totalTq) / 1_000d);
        var samplePoint = (1d + segments.Tseg1) / totalTq * 100d;
        return (bitrateKbps, samplePoint);
    }
}

public sealed class BitrateTableProvider : IBitrateTableProvider
{
    private readonly List<BitTimingSetting> _custom = [];
    private static readonly IReadOnlyList<BitTimingSetting> NominalPresets =
        [new(10, 75), new(20, 80), new(50, 80), new(100, 80), new(125, 87.5), new(250, 87.5), new(500, 87.5), new(800, 80), new(1_000, 75)];
    private static readonly IReadOnlyList<BitTimingSetting> DataPresets =
        [new(500, 80), new(1_000, 80), new(2_000, 80), new(4_000, 80), new(5_000, 75), new(8_000, 70)];
    public IReadOnlyList<BitTimingSetting> GetNominalPresets() => [.. NominalPresets, .. _custom];
    public IReadOnlyList<BitTimingSetting> GetFdDataPhasePresets() => [.. DataPresets, .. _custom];
    public void RegisterCustomPreset(BitTimingSetting setting)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(setting.BitrateKbps);
        if (setting.SamplePointPercent is <= 0 or > 100) throw new ArgumentOutOfRangeException(nameof(setting));
        if (!_custom.Contains(setting)) _custom.Add(setting with { IsCustom = true });
    }
}
public sealed class CanErrorEventArgs(CanErrorKind kind, string message, Exception? exception = null) : EventArgs
{
    public CanErrorKind Kind { get; } = kind;
    public string Message { get; } = message;
    public Exception? Exception { get; } = exception;
}
public sealed class CanDeviceStatusChangedEventArgs(CanDeviceStatus status, string? message = null) : EventArgs
{
    public CanDeviceStatus Status { get; } = status;
    public string? Message { get; } = message;
}

public interface ICanDevice : IAsyncDisposable
{
    string ChannelName { get; }
    CanDeviceStatus Status { get; }
    event EventHandler<CanErrorEventArgs>? ErrorOccurred;
    event EventHandler<CanDeviceStatusChangedEventArgs>? StatusChanged;
    Task OpenAsync(CanBusConfig config, CancellationToken cancellationToken = default);
    Task CloseAsync(CancellationToken cancellationToken = default);
    ValueTask SendAsync(CanFrame frame, CancellationToken cancellationToken = default);
    IAsyncEnumerable<CanFrame> ReadFramesAsync(CancellationToken cancellationToken = default);
    Task ResetBusAsync(CancellationToken cancellationToken = default);
}

public interface ICanDeviceFactory
{
    string Vendor { get; }
    Task<IReadOnlyList<CanDeviceDescriptor>> EnumerateAsync(CancellationToken cancellationToken = default);
    Task<ICanDevice> CreateAsync(string deviceId, string channelName, CancellationToken cancellationToken = default);
}

/// <summary>Optional companion to <see cref="ICanDeviceFactory"/> for factories whose enumeration
/// goes through several vendor SDK calls that can each fail independently (e.g. Vector's
/// app-channel lookup). When <see cref="EnumerateAsync"/> finds nothing, callers can check for this
/// interface and surface <see cref="LastEnumerationDiagnostics"/> to explain which specific call
/// returned what, instead of a bare "no channel found".</summary>
public interface ICanDeviceFactoryDiagnostics
{
    string LastEnumerationDiagnostics { get; }
}
