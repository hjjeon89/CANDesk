namespace CANDesk.Core.MessageDb;

/// <summary>Guesses a <see cref="CsvSchemaProfile"/> from a customer CSV's header row by matching
/// each column name against a synonym dictionary per <see cref="CsvField"/>. This is the "1차,
/// 규칙 기반" auto-detection step from <c>Project_Architecture.md</c> §7.1 — it is deliberately a
/// starting point, not a guarantee: ambiguous or unrecognized headers are left unmapped so the
/// caller (a future manual-mapping screen) can fill the rest in and save the result as a reusable
/// profile. The synonym lists grow over time as real customer CSV samples arrive; there are no
/// samples in the repo yet, so today's list is a reasonable best guess at common column names.</summary>
public static class CsvSchemaProfileDetector
{
    private static readonly IReadOnlyDictionary<CsvField, string[]> Synonyms = new Dictionary<CsvField, string[]>
    {
        [CsvField.MessageName] = ["message name", "message", "msg name", "msg", "frame name", "frame"],
        [CsvField.CanId] = ["can id", "canid", "id", "message id", "msg id", "frame id", "arbitration id"],
        [CsvField.Dlc] = ["dlc", "length (bytes)", "byte length", "message length"],
        [CsvField.SignalName] = ["signal name", "signal", "sig name", "name"],
        [CsvField.StartBit] = ["start bit", "startbit", "bit start", "bit position", "start"],
        [CsvField.BitLength] = ["bit length", "length", "signal length", "bits", "len"],
        [CsvField.ByteOrder] = ["byte order", "byteorder", "endian", "endianness"],
        [CsvField.Signed] = ["signed", "sign", "is signed", "data type"],
        [CsvField.Factor] = ["factor", "scale", "resolution", "gain"],
        [CsvField.Offset] = ["offset"],
        [CsvField.Minimum] = ["min", "minimum", "min value"],
        [CsvField.Maximum] = ["max", "maximum", "max value"],
        [CsvField.Unit] = ["unit", "units"],
    };

    /// <summary>Builds a best-effort profile from <paramref name="headerColumns"/>. Each header is
    /// matched (case/space/underscore-insensitive) against every field's synonym list; the first
    /// field with an unclaimed match wins the column, so put more specific synonyms first if two
    /// fields could plausibly claim the same header text (e.g. "Name" only matches SignalName
    /// because MessageName's synonym list uses more specific phrases first — see the dictionary
    /// above). Fields with no matching column are simply absent from the result; check
    /// <see cref="CsvSchemaProfile.MissingMandatoryFields"/> to see whether that leaves the profile
    /// usable as-is.</summary>
    public static CsvSchemaProfile Detect(string profileName, IReadOnlyList<string> headerColumns, char delimiter = ',')
    {
        var columnByField = new Dictionary<CsvField, string>();
        var claimedColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (field, synonyms) in Synonyms)
        {
            var match = headerColumns.FirstOrDefault(column =>
                !claimedColumns.Contains(column) && synonyms.Contains(Normalize(column)));
            if (match is null)
            {
                continue;
            }

            columnByField[field] = match;
            claimedColumns.Add(match);
        }

        return new CsvSchemaProfile(profileName, columnByField, delimiter);
    }

    private static string Normalize(string header) =>
        header.Trim().Replace('_', ' ').Replace('-', ' ').ToLowerInvariant();
}
