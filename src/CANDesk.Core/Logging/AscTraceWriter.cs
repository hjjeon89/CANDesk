using System.Globalization;
using System.Text;
using CANDesk.Hal;

namespace CANDesk.Core.Logging;

public sealed class AscTraceWriter
{
    public async Task WriteAsync(IEnumerable<CanTraceFrame> frames, Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(stream);

        var snapshot = frames.OrderBy(frame => frame.Timestamp).ToArray();
        var start = snapshot.Length > 0 ? snapshot[0].Timestamp : DateTime.UtcNow;
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 1024, leaveOpen: true);

        await writer.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"date {start.ToLocalTime():ddd MMM dd HH:mm:ss.fff yyyy}"));
        await writer.WriteLineAsync("base hex  timestamps absolute");
        await writer.WriteLineAsync("internal events logged");
        await writer.WriteLineAsync("// CANDesk Vector ASCII trace export");
        await writer.WriteLineAsync("Begin Triggerblock");

        foreach (var traceFrame in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var timestamp = Math.Max(0, (traceFrame.Timestamp - start).TotalSeconds);
            var frame = traceFrame.Frame;
            var direction = NormalizeDirection(traceFrame.Direction);
            if (frame.Flags.HasFlag(CanFrameFlags.Fd))
            {
                await writer.WriteLineAsync(FormatCanFd(timestamp, traceFrame.Channel, direction, frame));
            }
            else
            {
                await writer.WriteLineAsync(FormatClassic(timestamp, traceFrame.Channel, direction, frame));
            }
        }

        await writer.WriteLineAsync("End Triggerblock");
    }

    private static string FormatClassic(double timestamp, int channel, string direction, CanFrame frame)
    {
        var id = FormatId(frame);
        var bytes = FormatBytes(frame.PayloadSpan);
        return string.Create(CultureInfo.InvariantCulture,
            $"{timestamp,12:0.000000} {channel} {id} {direction} d {frame.PayloadLength} {bytes}");
    }

    private static string FormatCanFd(double timestamp, int channel, string direction, CanFrame frame)
    {
        var id = FormatId(frame);
        var brs = frame.Flags.HasFlag(CanFrameFlags.BitRateSwitch) ? 1 : 0;
        var bytes = FormatBytes(frame.PayloadSpan);
        return string.Create(CultureInfo.InvariantCulture,
            $"{timestamp,12:0.000000} CANFD {channel} {direction} {id} {brs} 0 {frame.Dlc} {frame.PayloadLength} {bytes}");
    }

    private static string NormalizeDirection(string direction) =>
        string.Equals(direction, "TX", StringComparison.OrdinalIgnoreCase) ? "Tx" : "Rx";

    private static string FormatId(in CanFrame frame)
    {
        var id = frame.Flags.HasFlag(CanFrameFlags.Extended) ? frame.Id.ToString("X8", CultureInfo.InvariantCulture) + "x" : frame.Id.ToString("X3", CultureInfo.InvariantCulture);
        return id;
    }

    private static string FormatBytes(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty)
        {
            return string.Empty;
        }

        return string.Join(' ', payload.ToArray().Select(static value => value.ToString("X2", CultureInfo.InvariantCulture)));
    }
}
