using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CANDesk.Core.MessageDb;

/// <summary>Parses the portable DBC subset used by CANDesk: BO_, SG_, and multiplexing markers.</summary>
public sealed partial class DbcParser : IMessageDatabaseParser
{
    private sealed class PendingMessage(uint id, string name, byte dlc) { public uint Id = id; public string Name = name; public byte Dlc = dlc; public List<DbcSignal> Signals = []; }

    private static readonly Encoding Utf8Strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly Encoding Cp949Strict;

    static DbcParser()
    {
        // .NET Core ships only UTF-8/UTF-16/ASCII/Latin-1 out of the box; CP949 (Korean EUC-KR,
        // codepage 949) needs this provider registered before Encoding.GetEncoding(949) resolves.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Cp949Strict = Encoding.GetEncoding(949, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    public MessageDbFormat Format => MessageDbFormat.Dbc;
    public async Task<IMessageDatabase> ParseAsync(Stream input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input); var messages = new List<DbcMessage>(); PendingMessage? current = null;
        using var memory = new MemoryStream();
        await input.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
        using var reader = new StringReader(DecodeText(memory.ToArray()));
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            var messageMatch = MessagePattern().Match(line);
            if (messageMatch.Success)
            {
                if (current is not null && !IsUnassignableMessage(current)) messages.Add(new(current.Id, current.Name, current.Dlc, current.Signals));
                var rawId = uint.Parse(messageMatch.Groups["id"].Value, CultureInfo.InvariantCulture);
                current = new PendingMessage(rawId & 0x1FFF_FFFF, messageMatch.Groups["name"].Value, byte.Parse(messageMatch.Groups["dlc"].Value, CultureInfo.InvariantCulture));
                continue;
            }
            var signalMatch = SignalPattern().Match(line);
            if (signalMatch.Success && current is not null) current.Signals.Add(ParseSignal(signalMatch));
        }
        if (current is not null && !IsUnassignableMessage(current)) messages.Add(new(current.Id, current.Name, current.Dlc, current.Signals));
        return new MessageDatabase(messages);
    }

    /// <summary>DBC has no mandated text encoding, and real-world exports save unit strings with
    /// special characters in whatever codepage the exporting tool/OS locale used. Tried in order:
    /// UTF-8 (self-describing, increasingly the default); then CP949/Korean EUC-KR, since this
    /// project's DBCs are authored on Korean-locale Windows and CP949 is that locale's legacy ANSI
    /// codepage — it's a multi-byte encoding, so single-byte fallbacks like Latin-1 can't represent
    /// its characters (e.g. "℃", U+2103, encoded as one CP949 double-byte pair) at all, only mangle
    /// them differently; finally Latin-1 (Windows-1252-ish, e.g. some Vector exports' plain "°" sign)
    /// as a last resort, since decoding every byte 0-255 to the matching code point never throws. A
    /// leading UTF-8 BOM, if present, is stripped either way.</summary>
    private static string DecodeText(byte[] bytes)
    {
        string text;
        try
        {
            text = Utf8Strict.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            try
            {
                text = Cp949Strict.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                text = Encoding.Latin1.GetString(bytes);
            }
        }

        const char byteOrderMark = (char)0xFEFF;
        return text.Length > 0 && text[0] == byteOrderMark ? text[1..] : text;
    }

    /// <summary>Vector's DBC convention for signals not assigned to any real message — typically
    /// <c>BO_ 0 VECTOR_INDEPENDENT_SIG_MSG: 0 Vector__XXX</c> — is a container, not an actual CAN
    /// message any node sends, so it shouldn't show up in the message tree or be TX-able. None of
    /// its telltale traits (id 0x000, a name Vector-prefixed however the exporter cased/spaced it,
    /// DLC 0) is a real CAN message either on its own, so any one of them is enough to exclude it.</summary>
    private static bool IsUnassignableMessage(PendingMessage message) =>
        message.Id == 0 || message.Dlc == 0 || message.Name.Trim().StartsWith("VECTOR", StringComparison.OrdinalIgnoreCase);
    private static DbcSignal ParseSignal(Match match)
    {
        var mux = match.Groups["mux"].Value;
        var role = mux == "M" ? MultiplexorRole.Switch : mux.StartsWith('m') ? MultiplexorRole.Value : MultiplexorRole.None;
        int? muxValue = role == MultiplexorRole.Value ? int.Parse(mux[1..], CultureInfo.InvariantCulture) : null;
        var min = double.Parse(match.Groups["min"].Value, CultureInfo.InvariantCulture);
        var max = double.Parse(match.Groups["max"].Value, CultureInfo.InvariantCulture);
        // DBC convention (CANdb++ and most other exporters): a signal with no authored range is
        // written literally as "[0|0]" rather than omitted. DbcWriter already round-trips a null
        // Minimum/Maximum out as "0" for exactly this reason (see its `signal.Minimum ?? 0`) — so
        // reading [0|0] back as a real 0..0 range here, instead of "no limit", was clamping every
        // TX-encoded value for that signal to exactly 0 in SignalEncoder.Encode, regardless of what
        // was typed. Only the literal 0/0 pair is treated as unset; an authored "[0|5]" etc. still
        // clamps normally.
        double? minimum = min == 0 && max == 0 ? null : min;
        double? maximum = min == 0 && max == 0 ? null : max;
        return new(match.Groups["name"].Value, int.Parse(match.Groups["start"].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups["length"].Value, CultureInfo.InvariantCulture),
            match.Groups["order"].Value == "1" ? ByteOrder.Intel : ByteOrder.Motorola, match.Groups["sign"].Value == "-",
            double.Parse(match.Groups["factor"].Value, CultureInfo.InvariantCulture), double.Parse(match.Groups["offset"].Value, CultureInfo.InvariantCulture),
            minimum, maximum, match.Groups["unit"].Value, role, muxValue);
    }
    [GeneratedRegex("^\\s*BO_\\s+(?<id>\\d+)\\s+(?<name>[^:]+):\\s*(?<dlc>\\d+)", RegexOptions.CultureInvariant)] private static partial Regex MessagePattern();
    [GeneratedRegex("^\\s*SG_\\s+(?<name>\\S+)(?:\\s+(?<mux>M|m\\d+))?\\s*:\\s*(?<start>\\d+)\\|(?<length>\\d+)@(?<order>[01])(?<sign>[+-])\\s*\\((?<factor>[^,]+),(?<offset>[^)]+)\\)\\s*\\[(?<min>[^|]+)\\|(?<max>[^]]+)\\]\\s*\\\"(?<unit>[^\"]*)\\\"", RegexOptions.CultureInvariant)] private static partial Regex SignalPattern();
}
