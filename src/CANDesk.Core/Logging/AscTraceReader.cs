using System.Globalization;
using System.Text;
using CANDesk.Hal;

namespace CANDesk.Core.Logging;

public sealed class AscTraceReader
{
    public async Task<IReadOnlyList<CanTraceFrame>> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true);
        var frames = new List<CanTraceFrame>();
        var start = DateTime.UnixEpoch;
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            line = line.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.StartsWith("date ", StringComparison.OrdinalIgnoreCase))
            {
                start = ParseDate(line[5..]) ?? start;
                continue;
            }

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0 || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var timestampSeconds))
            {
                continue;
            }

            var timestamp = start.AddSeconds(timestampSeconds);
            if (parts.Length > 1 && string.Equals(parts[1], "CANFD", StringComparison.OrdinalIgnoreCase))
            {
                var frame = ParseCanFd(parts, timestamp);
                if (frame is not null)
                {
                    frames.Add(frame);
                }
            }
            else
            {
                var frame = ParseClassic(parts, timestamp);
                if (frame is not null)
                {
                    frames.Add(frame);
                }
            }
        }

        return frames;
    }

    private static CanTraceFrame? ParseClassic(IReadOnlyList<string> parts, DateTime timestamp)
    {
        if (parts.Count < 6 || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var channel))
        {
            return null;
        }

        if (!TryParseId(parts[2], out var id, out var flags))
        {
            return null;
        }

        var direction = NormalizeDirection(parts[3]);
        var payloadStart = 6;
        if (!int.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var payloadLength))
        {
            return null;
        }

        var payload = ParsePayload(parts, payloadStart, payloadLength);
        if (payload is null)
        {
            return null;
        }

        return new CanTraceFrame(timestamp, channel, direction, CanFrame.Create(id, payload, flags, systemTime: timestamp.ToUniversalTime()));
    }

    private static CanTraceFrame? ParseCanFd(IReadOnlyList<string> parts, DateTime timestamp)
    {
        if (parts.Count < 10 || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var channel))
        {
            return null;
        }

        var direction = NormalizeDirection(parts[3]);
        if (!TryParseId(parts[4], out var id, out var flags))
        {
            return null;
        }

        flags |= CanFrameFlags.Fd;
        if (parts[5] != "0")
        {
            flags |= CanFrameFlags.BitRateSwitch;
        }

        if (!int.TryParse(parts[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out var payloadLength))
        {
            return null;
        }

        var payload = ParsePayload(parts, 9, payloadLength);
        if (payload is null)
        {
            return null;
        }

        return new CanTraceFrame(timestamp, channel, direction, CanFrame.Create(id, payload, flags, systemTime: timestamp.ToUniversalTime()));
    }

    private static byte[]? ParsePayload(IReadOnlyList<string> parts, int start, int length)
    {
        if (length < 0 || parts.Count < start + length)
        {
            return null;
        }

        var payload = new byte[length];
        for (var i = 0; i < length; i++)
        {
            if (!byte.TryParse(parts[start + i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out payload[i]))
            {
                return null;
            }
        }

        return payload;
    }

    private static bool TryParseId(string text, out uint id, out CanFrameFlags flags)
    {
        flags = CanFrameFlags.None;
        if (text.EndsWith('x') || text.EndsWith('X'))
        {
            flags |= CanFrameFlags.Extended;
            text = text[..^1];
        }

        return uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id);
    }

    private static DateTime? ParseDate(string text)
    {
        return DateTime.TryParseExact(text, "ddd MMM dd HH:mm:ss.fff yyyy", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal, out var parsed)
            ? parsed.ToUniversalTime()
            : null;
    }

    private static string NormalizeDirection(string direction) =>
        string.Equals(direction, "Tx", StringComparison.OrdinalIgnoreCase) ? "TX" : "RX";
}
