using System.Globalization;
using System.Xml.Linq;

namespace CANDesk.Core.MessageDb;

/// <summary>Full parse result for the CANDesk XML format, including node and TX/RX metadata that
/// <see cref="IMessageDatabase"/> alone cannot carry. <see cref="CandeskXmlParser.ParseAsync"/>
/// (the <see cref="IMessageDatabaseParser"/> contract) discards that extra metadata; callers that
/// need a lossless round trip — e.g. reopening a saved Message DB Editor session — should call
/// <see cref="CandeskXmlParser.ParseDocumentAsync"/> instead.</summary>
public sealed record CandeskXmlDocument(
    IMessageDatabase Database,
    IReadOnlyCollection<CanNode> Nodes,
    IReadOnlyDictionary<uint, MessageNodeAssignment> Assignments);

public sealed class CandeskXmlParser : IMessageDatabaseParser
{
    public MessageDbFormat Format => MessageDbFormat.CandeskXml;

    public async Task<IMessageDatabase> ParseAsync(Stream input, CancellationToken cancellationToken = default) =>
        (await ParseDocumentAsync(input, cancellationToken).ConfigureAwait(false)).Database;

    /// <summary>Parses messages, signals, nodes, and per-message TX/RX node assignments. See
    /// <c>CANDeskXml_Schema_Spec.md</c> for the schema this mirrors.</summary>
    public async Task<CandeskXmlDocument> ParseDocumentAsync(Stream input, CancellationToken cancellationToken = default)
    {
        var document = await XDocument.LoadAsync(input, LoadOptions.None, cancellationToken).ConfigureAwait(false);
        var root = document.Root ?? throw new FormatException("Missing <candesk> root element.");

        var nodes = root.Element("nodes")?.Elements("node")
            .Select(node => new CanNode(Require(node.Attribute("name")?.Value, "node name")))
            .ToArray() ?? [];

        var messages = new List<DbcMessage>();
        var assignments = new Dictionary<uint, MessageNodeAssignment>();
        foreach (var messageElement in root.Elements("message"))
        {
            var canId = ParseUInt(messageElement.Attribute("id")?.Value);
            var signals = messageElement.Elements("signal").Select(ParseSignal).ToArray();
            messages.Add(new DbcMessage(
                canId,
                Require(messageElement.Attribute("name")?.Value, "message name"),
                byte.Parse(Require(messageElement.Attribute("dlc")?.Value, "message dlc"), CultureInfo.InvariantCulture),
                signals,
                bool.Parse(messageElement.Attribute("extended")?.Value ?? "false")));

            var transmitter = messageElement.Attribute("tx")?.Value;
            var receivers = messageElement.Element("rx")?.Elements("node")
                .Select(node => Require(node.Attribute("name")?.Value, "rx node name"))
                .ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
            if (!string.IsNullOrWhiteSpace(transmitter) || receivers.Count > 0)
            {
                assignments[canId] = new MessageNodeAssignment(string.IsNullOrWhiteSpace(transmitter) ? null : transmitter, receivers);
            }
        }

        return new CandeskXmlDocument(new MessageDatabase(messages), nodes, assignments);
    }

    private static DbcSignal ParseSignal(XElement signal) => new(
        Require(signal.Attribute("name")?.Value, "signal name"),
        int.Parse(Require(signal.Attribute("startBit")?.Value, "signal startBit"), CultureInfo.InvariantCulture),
        int.Parse(Require(signal.Attribute("length")?.Value, "signal length"), CultureInfo.InvariantCulture),
        Enum.Parse<ByteOrder>(signal.Attribute("byteOrder")?.Value ?? nameof(ByteOrder.Intel), true),
        bool.Parse(signal.Attribute("signed")?.Value ?? "false"),
        double.Parse(signal.Attribute("factor")?.Value ?? "1", CultureInfo.InvariantCulture),
        double.Parse(signal.Attribute("offset")?.Value ?? "0", CultureInfo.InvariantCulture),
        ParseNullableDouble(signal.Attribute("min")?.Value),
        ParseNullableDouble(signal.Attribute("max")?.Value),
        signal.Attribute("unit")?.Value,
        Enum.Parse<MultiplexorRole>(signal.Attribute("muxRole")?.Value ?? nameof(MultiplexorRole.None), true),
        ParseNullableInt(signal.Attribute("muxValue")?.Value));

    private static double? ParseNullableDouble(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : double.Parse(value, CultureInfo.InvariantCulture);

    private static int? ParseNullableInt(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : int.Parse(value, CultureInfo.InvariantCulture);

    private static string Require(string? value, string name) => !string.IsNullOrWhiteSpace(value) ? value : throw new FormatException($"Missing {name}.");

    private static uint ParseUInt(string? value)
    {
        var text = Require(value, "CAN ID");
        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.Parse(text[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)
            : uint.Parse(text, CultureInfo.InvariantCulture);
    }
}

public sealed class CandeskXmlWriter : IMessageDatabaseWriter
{
    public MessageDbFormat Format => MessageDbFormat.CandeskXml;

    /// <summary>Writes messages/signals only, satisfying the format-agnostic
    /// <see cref="IMessageDatabaseWriter"/> contract. Node and TX/RX metadata isn't part of
    /// <see cref="IMessageDatabase"/>; call the other <c>WriteAsync</c> overload directly for a
    /// lossless round trip of an <see cref="EditableMessageDatabase"/> session.</summary>
    public Task WriteAsync(IMessageDatabase database, Stream output, CancellationToken cancellationToken = default) =>
        WriteAsync([], database, static _ => new MessageNodeAssignment(null, new HashSet<string>(StringComparer.Ordinal)), output, cancellationToken);

    /// <summary>Writes messages, signals, nodes, and per-message TX/RX node assignments — the full
    /// CANDesk XML schema. See <c>CANDeskXml_Schema_Spec.md</c>.</summary>
    public Task WriteAsync(IReadOnlyCollection<CanNode> nodes, IMessageDatabase database,
        Func<uint, MessageNodeAssignment> getAssignment, Stream output, CancellationToken cancellationToken = default)
    {
        var root = new XElement("candesk",
            new XAttribute("version", "1"),
            nodes.Count == 0
                ? null
                : new XElement("nodes", nodes.Select(node => new XElement("node", new XAttribute("name", node.Name)))),
            database.Messages.OrderBy(message => message.CanId).Select(message => WriteMessage(message, getAssignment(message.CanId))));
        return new XDocument(root).SaveAsync(output, SaveOptions.None, cancellationToken);
    }

    private static XElement WriteMessage(DbcMessage message, MessageNodeAssignment assignment) => new("message",
        new XAttribute("id", $"0x{message.CanId:X}"),
        new XAttribute("name", message.Name),
        new XAttribute("dlc", message.Dlc),
        new XAttribute("extended", message.IsExtended),
        assignment.TransmitterNode is null ? null : new XAttribute("tx", assignment.TransmitterNode),
        assignment.ReceiverNodes.Count == 0
            ? null
            : new XElement("rx", assignment.ReceiverNodes.Select(name => new XElement("node", new XAttribute("name", name)))),
        message.Signals.Select(WriteSignal));

    private static XElement WriteSignal(DbcSignal signal) => new("signal",
        new XAttribute("name", signal.Name),
        new XAttribute("startBit", signal.StartBit),
        new XAttribute("length", signal.BitLength),
        new XAttribute("byteOrder", signal.ByteOrder),
        new XAttribute("signed", signal.IsSigned),
        new XAttribute("factor", signal.Factor.ToString(CultureInfo.InvariantCulture)),
        new XAttribute("offset", signal.Offset.ToString(CultureInfo.InvariantCulture)),
        signal.Minimum is null ? null : new XAttribute("min", signal.Minimum.Value.ToString(CultureInfo.InvariantCulture)),
        signal.Maximum is null ? null : new XAttribute("max", signal.Maximum.Value.ToString(CultureInfo.InvariantCulture)),
        signal.Unit is null ? null : new XAttribute("unit", signal.Unit),
        signal.MultiplexorRole == MultiplexorRole.None ? null : new XAttribute("muxRole", signal.MultiplexorRole),
        signal.MultiplexValue is null ? null : new XAttribute("muxValue", signal.MultiplexValue.Value));
}
