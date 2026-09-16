using CANDesk.Hal;

namespace CANDesk.Hal.Mock;

public static class MockTracePlayback
{
    public static IReadOnlyList<MockTraceFrame> FromTimestampedFrames(IEnumerable<(CanFrame Frame, DateTime Timestamp)> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);

        var ordered = frames.OrderBy(frame => frame.Timestamp).ToArray();
        var playback = new List<MockTraceFrame>(ordered.Length);
        DateTime? previous = null;
        foreach (var (frame, timestamp) in ordered)
        {
            var delay = previous is null ? TimeSpan.Zero : timestamp - previous.Value;
            playback.Add(new MockTraceFrame(frame, delay < TimeSpan.Zero ? TimeSpan.Zero : delay));
            previous = timestamp;
        }

        return playback;
    }
}
