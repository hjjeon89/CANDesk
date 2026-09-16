using CANDesk.Core.Logging;
using CANDesk.Hal;
using Xunit;

namespace CANDesk.Core.Tests;

public sealed class AscTraceFormatTests
{
    [Fact]
    public async Task WriteThenRead_RoundTripsClassicAndCanFdFrames()
    {
        var start = new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);
        var frames = new[]
        {
            new CanTraceFrame(start, 1, "RX", CanFrame.Create(0x123, [0x01, 0x02], systemTime: start)),
            new CanTraceFrame(start.AddMilliseconds(5), 1, "TX",
                CanFrame.Create(0x18DAF110, [0x10, 0x14, 0xAA, 0x55, 0x01, 0x02, 0x03, 0x04],
                    CanFrameFlags.Extended | CanFrameFlags.Fd | CanFrameFlags.BitRateSwitch,
                    systemTime: start.AddMilliseconds(5))),
        };

        await using var stream = new MemoryStream();
        await new AscTraceWriter().WriteAsync(frames, stream);

        stream.Position = 0;
        var parsed = await new AscTraceReader().ReadAsync(stream);

        Assert.Equal(2, parsed.Count);
        Assert.Equal((uint)0x123, parsed[0].Frame.Id);
        Assert.Equal("RX", parsed[0].Direction);
        Assert.Equal([0x01, 0x02], parsed[0].Frame.PayloadSpan.ToArray());
        Assert.Equal((uint)0x18DAF110, parsed[1].Frame.Id);
        Assert.Equal("TX", parsed[1].Direction);
        Assert.True(parsed[1].Frame.Flags.HasFlag(CanFrameFlags.Extended));
        Assert.True(parsed[1].Frame.Flags.HasFlag(CanFrameFlags.Fd));
        Assert.True(parsed[1].Frame.Flags.HasFlag(CanFrameFlags.BitRateSwitch));
    }
}
