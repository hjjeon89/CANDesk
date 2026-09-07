using CANDesk.Core.MessageDb;
using CANDesk.Hal;

namespace CANDesk.Core.Dispatch;

public sealed class SignalDecoder(IMessageDatabase database)
{
    public IReadOnlyList<DecodedSignal> Decode(in CanFrame frame)
    {
        if (!database.TryGetMessage(frame.Id, out var message)) return [];
        var decoded = new List<DecodedSignal>(message.Signals.Count);
        int? muxValue = null;
        foreach (var signal in message.Signals.Where(signal => signal.MultiplexorRole == MultiplexorRole.Switch))
            muxValue = (int)ReadRaw(frame.PayloadSpan, signal);
        foreach (var signal in message.Signals)
        {
            if (signal.MultiplexorRole == MultiplexorRole.Value && signal.MultiplexValue != muxValue) continue;
            var raw = ReadRaw(frame.PayloadSpan, signal);
            if (signal.IsSigned) raw = SignExtend(raw, signal.BitLength);
            decoded.Add(new(frame.Id, message.Name, signal.Name, raw, raw * signal.Factor + signal.Offset, signal.Unit, frame.SystemTime));
        }
        return decoded;
    }

    /// <summary>Decodes a single signal's physical value from a payload, without a message lookup.
    /// The inverse of <see cref="SignalEncoder.Encode"/>.</summary>
    public static double DecodeValue(ReadOnlySpan<byte> payload, DbcSignal signal)
    {
        var raw = ReadRaw(payload, signal);
        if (signal.IsSigned) raw = SignExtend(raw, signal.BitLength);
        return raw * signal.Factor + signal.Offset;
    }

    private static long ReadRaw(ReadOnlySpan<byte> data, DbcSignal signal)
    {
        if (signal.StartBit < 0 || signal.BitLength is < 1 or > 64) throw new InvalidOperationException($"Invalid signal '{signal.Name}'.");
        ulong value = 0;
        for (var bit = 0; bit < signal.BitLength; bit++)
        {
            var source = signal.ByteOrder == ByteOrder.Intel ? signal.StartBit + bit : MotorolaBit(signal.StartBit, bit);
            if (source < 0 || source / 8 >= data.Length) throw new InvalidOperationException($"Signal '{signal.Name}' exceeds frame payload.");
            var bitValue = (data[source / 8] >> (source % 8)) & 1;
            if (signal.ByteOrder == ByteOrder.Intel) value |= (ulong)(uint)bitValue << bit;
            else value = (value << 1) | (ulong)(uint)bitValue;
        }
        return unchecked((long)value);
    }

    // DBC Motorola numbering walks down within a byte, then continues at the next byte's MSB.
    // Shared with SignalEncoder so encode/decode bit placement can never drift apart.
    internal static int MotorolaBit(int startBit, int offset)
    {
        var bit = startBit;
        for (var index = 0; index < offset; index++) bit = bit % 8 == 0 ? bit + 15 : bit - 1;
        return bit;
    }
    private static long SignExtend(long value, int bits) => bits == 64 ? value : (value & (1L << (bits - 1))) != 0 ? value | (-1L << bits) : value;
}
