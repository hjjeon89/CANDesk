namespace CANDesk.Core.Scheduling;

/// <summary>CRC-8/AUTOSAR (poly 0x2F, init 0xFF, no input/output reflection, XOR-out 0xFF) — the
/// checksum AUTOSAR E2E Profile 1 uses. Not reflected, so bits are processed MSB-first.</summary>
public static class Crc8Autosar
{
    private const byte Polynomial = 0x2F;
    private const byte InitialValue = 0xFF;
    private const byte XorOut = 0xFF;

    public static byte Compute(ReadOnlySpan<byte> data)
    {
        var crc = InitialValue;
        foreach (var b in data)
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (byte)((crc & 0x80) != 0 ? (crc << 1) ^ Polynomial : crc << 1);
            }
        }

        return (byte)(crc ^ XorOut);
    }
}
