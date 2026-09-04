namespace CANDesk.Hal;

public enum CanDeviceStatus { Closed, Opening, Open, BusOff, Faulted }
public enum CanErrorKind { BusOff, ErrorWarning, ErrorPassive, Crc, Bit, ReceiveOverflow, TransmitFailure, Driver }
public sealed record CanDeviceDescriptor(string Vendor, string DeviceId, string DisplayName, IReadOnlyList<string> Channels, bool SupportsCanFd);
public enum CanBusMode { Classic, Fd }

/// <summary>Timing for a single CAN arbitration or CAN-FD data phase.</summary>
public sealed record BitTimingSetting(int BitrateKbps, double SamplePointPercent, bool IsCustom = false)
{
    public override string ToString() => $"{BitrateKbps:N0} kbps / {SamplePointPercent:0.#}%";
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
    }
}

public interface IBitrateTableProvider
{
    IReadOnlyList<BitTimingSetting> GetNominalPresets();
    IReadOnlyList<BitTimingSetting> GetFdDataPhasePresets();
    void RegisterCustomPreset(BitTimingSetting setting);
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
