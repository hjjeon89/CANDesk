using System.Runtime.InteropServices;

namespace CANDesk.Hal;

[Flags]
public enum CanFrameFlags : byte
{
    None = 0,
    Extended = 1 << 0,
    Fd = 1 << 1,
    BitRateSwitch = 1 << 2,
    RemoteTransmissionRequest = 1 << 3,
    ErrorFrame = 1 << 4
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public unsafe struct CanFrame : IEquatable<CanFrame>
{
    private fixed byte _data[64];

    public uint Id { get; init; }
    public CanFrameFlags Flags { get; init; }
    public byte Dlc { get; init; }
    public byte PayloadLength { get; init; }
    public ulong TimestampUs { get; init; }
    public DateTime SystemTime { get; init; }

    public readonly ReadOnlySpan<byte> PayloadSpan
    {
        get { fixed (byte* data = _data) return new ReadOnlySpan<byte>(data, PayloadLength); }
    }

    public Span<byte> PayloadSpanWritable
    {
        get { fixed (byte* data = _data) return new Span<byte>(data, PayloadLength); }
    }

    public static CanFrame Create(uint id, ReadOnlySpan<byte> payload, CanFrameFlags flags = CanFrameFlags.None,
        ulong timestampUs = 0, DateTime? systemTime = null)
    {
        if (payload.Length > 64) throw new ArgumentOutOfRangeException(nameof(payload));
        var frame = new CanFrame { Id = id, Flags = flags, Dlc = GetDlc(payload.Length), PayloadLength = (byte)payload.Length,
            TimestampUs = timestampUs, SystemTime = systemTime ?? DateTime.UtcNow };
        payload.CopyTo(frame.PayloadSpanWritable);
        return frame;
    }

    public CanFrame WithPayload(ReadOnlySpan<byte> payload) => Create(Id, payload, Flags, TimestampUs, SystemTime);

    public readonly bool Equals(CanFrame other) => Id == other.Id && Flags == other.Flags && Dlc == other.Dlc &&
        PayloadLength == other.PayloadLength && PayloadSpan.SequenceEqual(other.PayloadSpan);
    public override readonly bool Equals(object? obj) => obj is CanFrame other && Equals(other);
    public override readonly int GetHashCode() => HashCode.Combine(Id, Flags, Dlc, PayloadLength);

    public static byte GetDlc(int length) => length switch { <= 8 => (byte)length, <= 12 => 9, <= 16 => 10, <= 20 => 11,
        <= 24 => 12, <= 32 => 13, <= 48 => 14, <= 64 => 15, _ => throw new ArgumentOutOfRangeException(nameof(length)) };
}
