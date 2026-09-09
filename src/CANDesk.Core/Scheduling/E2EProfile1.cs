namespace CANDesk.Core.Scheduling;

/// <summary>
/// Builds a <see cref="PreSendModifier"/> implementing (a subset of) AUTOSAR E2E Profile 1: an
/// optional 4-bit rolling counter (0-15) in the low nibble of the last payload byte, and/or an
/// optional CRC-8/AUTOSAR checksum written to the first byte, covering every other byte. The
/// counter is applied first since the CRC must cover its new value.
///
/// Each tick's frame is a fresh copy of the job's stored template (see
/// <see cref="TxScheduler.SendJobAsync"/> — the modifier never sees its own previous output), so
/// the counter is computed directly from <c>sendCount</c> rather than by reading back the last
/// written value.
/// </summary>
public static class E2EProfile1
{
    public static PreSendModifier? CreateModifier(bool includeRollingCounter, bool includeCrc8)
    {
        if (!includeRollingCounter && !includeCrc8)
        {
            return null;
        }

        return (payload, sendCount) =>
        {
            if (includeRollingCounter && payload.Length >= 1)
            {
                var lastIndex = payload.Length - 1;
                var counter = (byte)(sendCount % 16);
                payload[lastIndex] = (byte)((payload[lastIndex] & 0xF0) | counter);
            }

            if (includeCrc8 && payload.Length >= 2)
            {
                payload[0] = Crc8Autosar.Compute(payload[1..]);
            }
        };
    }
}
