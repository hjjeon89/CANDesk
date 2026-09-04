using System.Globalization;
using System.Text.RegularExpressions;

namespace CANDesk.Core.MessageDb;

/// <summary>Parses the portable DBC subset used by CANDesk: BO_, SG_, and multiplexing markers.</summary>
public sealed partial class DbcParser : IMessageDatabaseParser
{
    private sealed class PendingMessage(uint id, string name, byte dlc) { public uint Id = id; public string Name = name; public byte Dlc = dlc; public List<DbcSignal> Signals = []; }
    public MessageDbFormat Format => MessageDbFormat.Dbc;
    public async Task<IMessageDatabase> ParseAsync(Stream input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input); var messages = new List<DbcMessage>(); PendingMessage? current = null;
        using var reader = new StreamReader(input, leaveOpen: true);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            var messageMatch = MessagePattern().Match(line);
            if (messageMatch.Success)
            {
                if (current is not null) messages.Add(new(current.Id, current.Name, current.Dlc, current.Signals));
                var rawId = uint.Parse(messageMatch.Groups["id"].Value, CultureInfo.InvariantCulture);
                current = new PendingMessage(rawId & 0x1FFF_FFFF, messageMatch.Groups["name"].Value, byte.Parse(messageMatch.Groups["dlc"].Value, CultureInfo.InvariantCulture));
                continue;
            }
            var signalMatch = SignalPattern().Match(line);
            if (signalMatch.Success && current is not null) current.Signals.Add(ParseSignal(signalMatch));
        }
        if (current is not null) messages.Add(new(current.Id, current.Name, current.Dlc, current.Signals));
        return new MessageDatabase(messages);
    }
    private static DbcSignal ParseSignal(Match match)
    {
        var mux = match.Groups["mux"].Value;
        var role = mux == "M" ? MultiplexorRole.Switch : mux.StartsWith('m') ? MultiplexorRole.Value : MultiplexorRole.None;
        int? muxValue = role == MultiplexorRole.Value ? int.Parse(mux[1..], CultureInfo.InvariantCulture) : null;
        return new(match.Groups["name"].Value, int.Parse(match.Groups["start"].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups["length"].Value, CultureInfo.InvariantCulture),
            match.Groups["order"].Value == "1" ? ByteOrder.Intel : ByteOrder.Motorola, match.Groups["sign"].Value == "-",
            double.Parse(match.Groups["factor"].Value, CultureInfo.InvariantCulture), double.Parse(match.Groups["offset"].Value, CultureInfo.InvariantCulture),
            double.Parse(match.Groups["min"].Value, CultureInfo.InvariantCulture), double.Parse(match.Groups["max"].Value, CultureInfo.InvariantCulture), match.Groups["unit"].Value, role, muxValue);
    }
    [GeneratedRegex("^\\s*BO_\\s+(?<id>\\d+)\\s+(?<name>[^:]+):\\s*(?<dlc>\\d+)", RegexOptions.CultureInvariant)] private static partial Regex MessagePattern();
    [GeneratedRegex("^\\s*SG_\\s+(?<name>\\S+)(?:\\s+(?<mux>M|m\\d+))?\\s*:\\s*(?<start>\\d+)\\|(?<length>\\d+)@(?<order>[01])(?<sign>[+-])\\s*\\((?<factor>[^,]+),(?<offset>[^)]+)\\)\\s*\\[(?<min>[^|]+)\\|(?<max>[^]]+)\\]\\s*\\\"(?<unit>[^\"]*)\\\"", RegexOptions.CultureInvariant)] private static partial Regex SignalPattern();
}
