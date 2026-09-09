using System.Text;
using CANDesk.Core.Scheduling;
using Xunit;

namespace CANDesk.Core.Tests;

public sealed class Crc8AutosarTests
{
    [Fact]
    public void Compute_MatchesTheStandardCheckValue()
    {
        // The CRC catalogue's standard check value for CRC-8/AUTOSAR (poly 0x2F, init 0xFF,
        // no reflection, xorout 0xFF) over the ASCII bytes "123456789" is 0xDF.
        var check = Encoding.ASCII.GetBytes("123456789");
        Assert.Equal(0xDF, Crc8Autosar.Compute(check));
    }
}

public sealed class E2EProfile1Tests
{
    [Fact]
    public void CreateModifier_ReturnsNull_WhenNeitherOptionIsEnabled()
    {
        Assert.Null(E2EProfile1.CreateModifier(includeRollingCounter: false, includeCrc8: false));
    }

    [Fact]
    public void RollingCounter_WrapsAt16_AndPreservesTheHighNibble()
    {
        var modifier = E2EProfile1.CreateModifier(includeRollingCounter: true, includeCrc8: false)!;
        Span<byte> payload = [0x00, 0xF0];

        modifier(payload, sendCount: 3);
        Assert.Equal(0xF3, payload[1]);

        modifier(payload, sendCount: 16);
        Assert.Equal(0xF0, payload[1]);

        modifier(payload, sendCount: 17);
        Assert.Equal(0xF1, payload[1]);
    }

    [Fact]
    public void Crc8_IsWrittenToTheFirstByte_CoveringEveryOtherByte()
    {
        var modifier = E2EProfile1.CreateModifier(includeRollingCounter: false, includeCrc8: true)!;
        Span<byte> payload = [0x00, 0x11, 0x22, 0x33];

        modifier(payload, sendCount: 0);

        Assert.Equal(Crc8Autosar.Compute([0x11, 0x22, 0x33]), payload[0]);
    }

    [Fact]
    public void BothEnabled_ComputesCrcOverTheAlreadyUpdatedCounter()
    {
        // The counter must be applied before the CRC, since Profile 1's CRC covers the counter's
        // new value, not its previous one.
        var modifier = E2EProfile1.CreateModifier(includeRollingCounter: true, includeCrc8: true)!;
        Span<byte> payload = [0x00, 0x11, 0x22, 0x00];

        modifier(payload, sendCount: 5);

        Assert.Equal(0x05, payload[3]);
        Assert.Equal(Crc8Autosar.Compute([0x11, 0x22, 0x05]), payload[0]);
    }
}
