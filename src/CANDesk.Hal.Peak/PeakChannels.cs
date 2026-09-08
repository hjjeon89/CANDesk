using CANDesk.Hal;

namespace CANDesk.Hal.Peak;

/// <summary>Known PCAN-USB channel handles (<c>TPCANHandle</c> values from PCANBasic.h). Only the
/// USB range is wired up for v1; PCI/ISA/LAN channels are a documented extension point (§9).</summary>
internal static class PeakChannels
{
    public static readonly IReadOnlyList<(ushort Handle, string Name)> UsbChannels =
    [
        (0x51, "PCAN-USB 1"), (0x52, "PCAN-USB 2"), (0x53, "PCAN-USB 3"), (0x54, "PCAN-USB 4"),
        (0x55, "PCAN-USB 5"), (0x56, "PCAN-USB 6"), (0x57, "PCAN-USB 7"), (0x58, "PCAN-USB 8"),
    ];
}

/// <summary>
/// Maps CANDesk's <see cref="BitTimingSetting"/>/<see cref="RawBitTimingSegments"/> to the SJA1000
/// BTR0BTR1 register pair that <c>CAN_Initialize</c> expects.
/// </summary>
internal static class PeakBitTiming
{
    // Standard TPCANBaudrate values (BTR0BTR1) for a 16 MHz SJA1000 clock, per PCANBasic.h.
    private static readonly IReadOnlyDictionary<int, ushort> StandardBtr0Btr1 = new Dictionary<int, ushort>
    {
        [1_000] = 0x0014,
        [800] = 0x0016,
        [500] = 0x001C,
        [250] = 0x011C,
        [125] = 0x031C,
        [100] = 0x432F,
        [95] = 0xC34E,
        [83] = 0x852B,
        [50] = 0x472F,
        [47] = 0x1414,
        [33] = 0x8B2F,
        [20] = 0x532F,
        [10] = 0x672F,
        [5] = 0x7F7F,
    };

    /// <summary>Resolves a preset bitrate to its standard BTR0BTR1 value. PCAN-Basic's legacy
    /// <c>CAN_Initialize</c> only accepts these fixed SJA1000 presets; a non-standard bitrate must
    /// go through <see cref="FromRawSegments"/> (Advanced/RawSegments mode) instead.</summary>
    public static ushort FromPreset(BitTimingSetting setting)
    {
        if (StandardBtr0Btr1.TryGetValue(setting.BitrateKbps, out var value))
        {
            return value;
        }

        throw new NotSupportedException(
            $"PEAK Classic CAN only supports standard bitrates ({string.Join(", ", StandardBtr0Btr1.Keys.OrderDescending())} kbps) " +
            $"via a preset; {setting.BitrateKbps} kbps requires Advanced (TSeg1/TSeg2/SJW) timing input instead.");
    }

    /// <summary>Converts explicit BRP/TSeg1/TSeg2/SJW into the SJA1000 BTR0BTR1 register layout
    /// (BTR0 = SJW-1 &lt;&lt; 6 | BRP-1; BTR1 = SAM(0) &lt;&lt; 7 | TSeg2-1 &lt;&lt; 4 | TSeg1-1).</summary>
    public static ushort FromRawSegments(RawBitTimingSegments segments)
    {
        segments.Validate();
        if (segments.Prescaler is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(segments), "SJA1000 BRP must be 1-64.");
        if (segments.Tseg1 is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(segments), "SJA1000 TSeg1 must be 1-16.");
        if (segments.Tseg2 is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(segments), "SJA1000 TSeg2 must be 1-8.");
        if (segments.Sjw is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(segments), "SJA1000 SJW must be 1-4.");

        var btr0 = (byte)(((segments.Sjw - 1) << 6) | (segments.Prescaler - 1));
        var btr1 = (byte)(((segments.Tseg2 - 1) << 4) | (segments.Tseg1 - 1));
        return (ushort)((btr0 << 8) | btr1);
    }
}
