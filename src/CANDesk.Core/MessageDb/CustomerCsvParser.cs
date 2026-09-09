using System.Globalization;
using System.Text;

namespace CANDesk.Core.MessageDb;

/// <summary>Thrown by <see cref="CustomerCsvParser.ParseAsync"/> when header-based auto-detection
/// (<see cref="CsvSchemaProfileDetector"/>) could not confidently map every mandatory
/// <see cref="CsvField"/> to a column. Carries the best-effort partial profile and the raw header
/// row so a caller (a manual mapping screen, once built) can pre-fill the rest instead of starting
/// from scratch — see <c>Project_Architecture.md</c> §7.1.</summary>
public sealed class CsvMappingRequiredException(CsvSchemaProfile detectedProfile, IReadOnlyList<string> header)
    : Exception($"Could not auto-detect required CSV columns: {string.Join(", ", detectedProfile.MissingMandatoryFields)}. " +
                "Use CustomerCsvParser.ParseWithProfileAsync with a manually completed CsvSchemaProfile instead.")
{
    public CsvSchemaProfile DetectedProfile { get; } = detectedProfile;
    public IReadOnlyList<string> Header { get; } = header;
}

/// <summary>
/// Parses customer-specific CSV signal sheets via a <see cref="CsvSchemaProfile"/> column mapping.
/// Unlike DBC/CandeskXml, customer CSVs have no fixed layout — every customer's sheet is
/// column-mapped independently. One row is one signal; rows sharing the same CAN ID are grouped
/// into a single <see cref="DbcMessage"/>. See <c>Project_Architecture.md</c> §7.1 for the overall
/// "detect once, save profile, reuse" design this implements the parsing half of — the manual
/// mapping/save-profile GUI is a separate, not-yet-built follow-up (no real customer CSV sample
/// exists in the repo yet to validate the UI against).
/// </summary>
public sealed class CustomerCsvParser : IMessageDatabaseParser
{
    public MessageDbFormat Format => MessageDbFormat.CustomerCsv;

    /// <summary>The <see cref="IMessageDatabaseParser"/> contract: no profile is supplied, so this
    /// only succeeds when <see cref="CsvSchemaProfileDetector"/> can auto-detect every mandatory
    /// column from the header row of a comma-delimited sheet. Throws
    /// <see cref="CsvMappingRequiredException"/> otherwise — callers with a saved or manually built
    /// profile (including a non-comma delimiter) should call <see cref="ParseWithProfileAsync"/>
    /// directly instead.</summary>
    public async Task<IMessageDatabase> ParseAsync(Stream input, CancellationToken cancellationToken = default)
    {
        var (header, rows) = await ReadRowsAsync(input, cancellationToken).ConfigureAwait(false);
        var profile = CsvSchemaProfileDetector.Detect("auto-detected", header);
        if (profile.MissingMandatoryFields.Count > 0)
        {
            throw new CsvMappingRequiredException(profile, header);
        }

        return Build(profile, header, rows);
    }

    /// <summary>Parses using an explicit, already-complete profile — the path a manual mapping
    /// screen or a previously-saved per-customer profile takes once the columns are known, rather
    /// than re-running auto-detection on every import.</summary>
    public async Task<IMessageDatabase> ParseWithProfileAsync(Stream input, CsvSchemaProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.MissingMandatoryFields.Count > 0)
        {
            throw new InvalidOperationException(
                $"CSV profile '{profile.Name}' is missing required column mappings: {string.Join(", ", profile.MissingMandatoryFields)}.");
        }

        var (header, rows) = await ReadRowsAsync(input, cancellationToken, profile.Delimiter).ConfigureAwait(false);
        return Build(profile, header, rows);
    }

    private static IMessageDatabase Build(CsvSchemaProfile profile, string[] header, List<string[]> rows)
    {
        var columnIndex = new Dictionary<CsvField, int>();
        foreach (var (field, columnName) in profile.ColumnByField)
        {
            var index = Array.FindIndex(header, h => string.Equals(h.Trim(), columnName, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                columnIndex[field] = index;
            }
        }

        var byMessage = new Dictionary<uint, (string? Name, byte? Dlc, List<DbcSignal> Signals)>();
        foreach (var row in rows)
        {
            if (row.Length == 0 || Array.TrueForAll(row, string.IsNullOrWhiteSpace))
            {
                continue; // blank line
            }

            var canId = ParseUInt(Get(row, columnIndex, CsvField.CanId))
                ?? throw new FormatException("A CSV row is missing its CAN ID.");
            var signal = new DbcSignal(
                Name: Get(row, columnIndex, CsvField.SignalName) ?? throw new FormatException("A CSV row is missing its signal name."),
                StartBit: ParseInt(Get(row, columnIndex, CsvField.StartBit)) ?? throw new FormatException("A CSV row is missing its start bit."),
                BitLength: ParseInt(Get(row, columnIndex, CsvField.BitLength)) ?? throw new FormatException("A CSV row is missing its bit length."),
                ByteOrder: ParseByteOrder(Get(row, columnIndex, CsvField.ByteOrder)),
                IsSigned: ParseBool(Get(row, columnIndex, CsvField.Signed)),
                Factor: ParseDouble(Get(row, columnIndex, CsvField.Factor)) ?? 1,
                Offset: ParseDouble(Get(row, columnIndex, CsvField.Offset)) ?? 0,
                Minimum: ParseDouble(Get(row, columnIndex, CsvField.Minimum)),
                Maximum: ParseDouble(Get(row, columnIndex, CsvField.Maximum)),
                Unit: Get(row, columnIndex, CsvField.Unit));

            if (!byMessage.TryGetValue(canId, out var entry))
            {
                entry = (Get(row, columnIndex, CsvField.MessageName), ParseByte(Get(row, columnIndex, CsvField.Dlc)), []);
            }
            else if (entry.Dlc is null)
            {
                entry.Dlc = ParseByte(Get(row, columnIndex, CsvField.Dlc));
            }

            entry.Signals.Add(signal);
            byMessage[canId] = entry;
        }

        // DLC defaults to the Classic CAN max (8) when the sheet has no DLC column — it's metadata
        // for display/TX-buffer sizing, not something SignalDecoder relies on to place bits.
        var messages = byMessage.Select(pair => new DbcMessage(
            pair.Key,
            pair.Value.Name ?? $"MSG_{pair.Key:X}",
            pair.Value.Dlc ?? 8,
            pair.Value.Signals)).ToArray();

        return new MessageDatabase(messages);
    }

    private static string? Get(string[] row, IReadOnlyDictionary<CsvField, int> columnIndex, CsvField field)
    {
        if (!columnIndex.TryGetValue(field, out var index) || index >= row.Length)
        {
            return null;
        }

        var value = row[index].Trim();
        return value.Length == 0 ? null : value;
    }

    private static uint? ParseUInt(string? value)
    {
        if (value is null)
        {
            return null;
        }

        return value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.Parse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : uint.Parse(value, CultureInfo.InvariantCulture);
    }

    private static int? ParseInt(string? value) =>
        value is null ? null : int.Parse(value, CultureInfo.InvariantCulture);

    private static double? ParseDouble(string? value) =>
        value is null ? null : double.Parse(value, CultureInfo.InvariantCulture);

    private static byte? ParseByte(string? value) =>
        value is null ? null : byte.Parse(value, CultureInfo.InvariantCulture);

    private static ByteOrder ParseByteOrder(string? value)
    {
        if (value is null)
        {
            return ByteOrder.Intel;
        }

        var normalized = value.Trim().ToLowerInvariant();
        return normalized.Contains("motorola", StringComparison.Ordinal)
            || normalized.Contains("big", StringComparison.Ordinal)
            || normalized is "msb"
            ? ByteOrder.Motorola
            : ByteOrder.Intel;
    }

    private static bool ParseBool(string? value) =>
        value is not null && value.Trim().ToLowerInvariant() is "1" or "true" or "signed" or "yes";

    private static async Task<(string[] Header, List<string[]> Rows)> ReadRowsAsync(Stream input, CancellationToken cancellationToken, char delimiter = ',')
    {
        ArgumentNullException.ThrowIfNull(input);
        using var reader = new StreamReader(input, leaveOpen: true);
        var headerLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) ?? string.Empty;
        var header = SplitLine(headerLine, delimiter);

        var rows = new List<string[]>();
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }

            rows.Add(SplitLine(line, delimiter));
        }

        return (header, rows);
    }

    /// <summary>Minimal RFC 4180-style split: honors double-quoted fields (so a quoted field may
    /// contain the delimiter or embedded/escaped quotes) without pulling in a full CSV library for
    /// what is, at MVP scope, a simple flat sheet.</summary>
    private static string[] SplitLine(string line, char delimiter)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"')
            {
                inQuotes = true;
            }
            else if (c == delimiter)
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        fields.Add(current.ToString());
        return fields.ToArray();
    }
}
