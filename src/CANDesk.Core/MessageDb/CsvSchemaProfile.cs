namespace CANDesk.Core.MessageDb;

/// <summary>One field a customer CSV row can carry. <see cref="CanId"/>, <see cref="SignalName"/>,
/// <see cref="StartBit"/>, and <see cref="BitLength"/> are mandatory — every other field has a
/// sensible default when the column is absent from a given customer's sheet (see
/// <see cref="CustomerCsvParser"/>).</summary>
public enum CsvField
{
    MessageName,
    CanId,
    Dlc,
    SignalName,
    StartBit,
    BitLength,
    ByteOrder,
    Signed,
    Factor,
    Offset,
    Minimum,
    Maximum,
    Unit,
}

/// <summary>Maps a customer's CSV column layout onto <see cref="CsvField"/>s so
/// <see cref="CustomerCsvParser"/> can read arbitrary, non-standardized sheets. One profile is
/// meant to be saved and reused per customer/template rather than re-derived on every import —
/// see <c>Project_Architecture.md</c> §7.1 for the "detect once, save, reuse" flow this backs.</summary>
/// <param name="Name">Human-readable label for this profile (e.g. the customer or template name),
/// used only for the save/reuse UI — parsing itself never inspects it.</param>
/// <param name="ColumnByField">Field → CSV header text (exact match against the header row, case-
/// insensitive). Fields absent from this dictionary fall back to <see cref="CustomerCsvParser"/>'s
/// defaults if optional, or make the profile unusable if mandatory — see
/// <see cref="CsvSchemaProfile.MissingMandatoryFields"/>.</param>
/// <param name="Delimiter">Column delimiter. Comma covers the vast majority of exports; some
/// regions/tools emit semicolon-delimited CSV (notably Excel under a comma-as-decimal locale).</param>
public sealed record CsvSchemaProfile(string Name, IReadOnlyDictionary<CsvField, string> ColumnByField, char Delimiter = ',')
{
    public static readonly IReadOnlyList<CsvField> MandatoryFields =
        [CsvField.CanId, CsvField.SignalName, CsvField.StartBit, CsvField.BitLength];

    /// <summary>Mandatory fields this profile has no column mapped for. Non-empty means the
    /// profile cannot parse anything yet and needs either better auto-detection input or a manual
    /// mapping fix before <see cref="CustomerCsvParser.ParseWithProfileAsync"/> is called.</summary>
    public IReadOnlyList<CsvField> MissingMandatoryFields =>
        MandatoryFields.Where(csvField => !ColumnByField.ContainsKey(csvField)).ToArray();
}
