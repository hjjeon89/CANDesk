using System.Globalization;
using System.Xml.Linq;

namespace CANDesk.Core.MessageDb;

public sealed class CandeskXmlParser : IMessageDatabaseParser
{
    public MessageDbFormat Format => MessageDbFormat.CandeskXml;
    public async Task<IMessageDatabase> ParseAsync(Stream input, CancellationToken cancellationToken = default)
    {
        var document = await XDocument.LoadAsync(input, LoadOptions.None, cancellationToken).ConfigureAwait(false);
        var messages = document.Root?.Elements("message").Select(message => new DbcMessage(
            ParseUInt(message.Attribute("id")?.Value), Require(message.Attribute("name")?.Value, "message name"), byte.Parse(Require(message.Attribute("dlc")?.Value, "message dlc"), CultureInfo.InvariantCulture),
            message.Elements("signal").Select(signal => new DbcSignal(Require(signal.Attribute("name")?.Value, "signal name"), int.Parse(Require(signal.Attribute("startBit")?.Value, "signal startBit"), CultureInfo.InvariantCulture), int.Parse(Require(signal.Attribute("length")?.Value, "signal length"), CultureInfo.InvariantCulture),
                Enum.Parse<ByteOrder>(signal.Attribute("byteOrder")?.Value ?? nameof(ByteOrder.Intel), true), bool.Parse(signal.Attribute("signed")?.Value ?? "false"),
                double.Parse(signal.Attribute("factor")?.Value ?? "1", CultureInfo.InvariantCulture), double.Parse(signal.Attribute("offset")?.Value ?? "0", CultureInfo.InvariantCulture), Unit: signal.Attribute("unit")?.Value)).ToArray(), bool.Parse(message.Attribute("extended")?.Value ?? "false"))) ?? [];
        return new MessageDatabase(messages);
    }
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
    public Task WriteAsync(IMessageDatabase database, Stream output, CancellationToken cancellationToken = default)
    {
        var root = new XElement("candesk", database.Messages.Select(message => new XElement("message", new XAttribute("id", $"0x{message.CanId:X}"), new XAttribute("name", message.Name), new XAttribute("dlc", message.Dlc), new XAttribute("extended", message.IsExtended), message.Signals.Select(signal => new XElement("signal", new XAttribute("name", signal.Name), new XAttribute("startBit", signal.StartBit), new XAttribute("length", signal.BitLength), new XAttribute("byteOrder", signal.ByteOrder), new XAttribute("signed", signal.IsSigned), new XAttribute("factor", signal.Factor.ToString(CultureInfo.InvariantCulture)), new XAttribute("offset", signal.Offset.ToString(CultureInfo.InvariantCulture)), signal.Unit is null ? null : new XAttribute("unit", signal.Unit))))));
        return new XDocument(root).SaveAsync(output, SaveOptions.None, cancellationToken);
    }
}
