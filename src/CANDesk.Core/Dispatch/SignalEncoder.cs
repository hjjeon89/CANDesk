using CANDesk.Core.MessageDb;

namespace CANDesk.Core.Dispatch;

/// <summary>
/// Writes a physical signal value back into a frame payload — the inverse of <see cref="SignalDecoder"/>.
/// Bit placement mirrors <see cref="SignalDecoder"/> exactly (via the shared <c>MotorolaBit</c> helper)
/// so a value that round-trips through Decode then Encode reproduces the same raw bits.
/// </summary>
public static class SignalEncoder
{
    public static void Encode(Span<byte> payload, DbcSignal signal, double physicalValue)
    {
        if (signal.StartBit < 0 || signal.BitLength is < 1 or > 64) throw new InvalidOperationException($"Invalid signal '{signal.Name}'.");

        if (signal.Minimum.HasValue) physicalValue = Math.Max(physicalValue, signal.Minimum.Value);
        if (signal.Maximum.HasValue) physicalValue = Math.Min(physicalValue, signal.Maximum.Value);

        var raw = (long)Math.Round((physicalValue - signal.Offset) / signal.Factor, MidpointRounding.AwayFromZero);
        var masked = signal.BitLength == 64 ? unchecked((ulong)raw) : (ulong)raw & ((1UL << signal.BitLength) - 1);

        for (var bit = 0; bit < signal.BitLength; bit++)
        {
            var source = signal.ByteOrder == ByteOrder.Intel ? signal.StartBit + bit : SignalDecoder.MotorolaBit(signal.StartBit, bit);
            if (source < 0 || source / 8 >= payload.Length) throw new InvalidOperationException($"Signal '{signal.Name}' exceeds frame payload.");

            var bitValue = signal.ByteOrder == ByteOrder.Intel
                ? (int)((masked >> bit) & 1)
                : (int)((masked >> (signal.BitLength - 1 - bit)) & 1);

            var byteIndex = source / 8;
            var bitIndex = source % 8;
            if (bitValue == 1) payload[byteIndex] |= (byte)(1 << bitIndex);
            else payload[byteIndex] &= (byte)~(1 << bitIndex);
        }
    }
}
