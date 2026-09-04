using CANDesk.Hal;
using Xunit;

namespace CANDesk.Core.Tests;

public sealed class BitTimingTests
{
    [Fact]
    public void RawSegments_DescribeReturnsEffectiveBitrateAndSamplePoint()
    {
        var calculator = new BitTimingCalculator();

        var effective = calculator.Describe(new RawBitTimingSegments(10, 13, 2, 2), 80_000_000);

        Assert.Equal(500, effective.BitrateKbps);
        Assert.Equal(87.5, effective.SamplePointPercent);
    }

    [Fact]
    public void RawSegments_RejectsSjwGreaterThanTseg2()
    {
        var segments = new RawBitTimingSegments(1, 13, 2, 3);

        Assert.Throws<ArgumentOutOfRangeException>(segments.Validate);
    }

    [Fact]
    public void ClassicConfiguration_HasNominalTimingOnly()
    {
        var configuration = new CanBusConfig { Mode = CanBusMode.Classic, Nominal = new(500, 87.5) };

        Assert.False(configuration.IsCanFd);
        Assert.Null(configuration.Data);
        Assert.Equal(500, configuration.Nominal.BitrateKbps);
    }

    [Fact]
    public void FdConfiguration_KeepsIndependentNominalAndDataTiming()
    {
        var configuration = new CanBusConfig { Mode = CanBusMode.Fd, Nominal = new(500, 87.5), Data = new(2_000, 80) };

        Assert.True(configuration.IsCanFd);
        Assert.Equal(500, configuration.Nominal.BitrateKbps);
        Assert.Equal(2_000, configuration.Data!.BitrateKbps);
        Assert.Equal(80, configuration.Data.SamplePointPercent);
    }

    [Fact]
    public void Validate_RejectsFdConfigurationWithoutDataPhaseTiming()
    {
        var configuration = new CanBusConfig { Mode = CanBusMode.Fd, Nominal = new(500, 87.5) };

        Assert.Throws<InvalidOperationException>(configuration.Validate);
    }

    [Fact]
    public void Validate_RejectsClassicConfigurationWithDataPhaseTiming()
    {
        var configuration = new CanBusConfig { Mode = CanBusMode.Classic, Nominal = new(500, 87.5), Data = new(2_000, 80) };

        Assert.Throws<InvalidOperationException>(configuration.Validate);
    }

    [Fact]
    public void BitrateTable_ContainsSeparateNominalAndFdDataPresets()
    {
        var table = new BitrateTableProvider();

        Assert.Contains(table.GetNominalPresets(), setting => setting.BitrateKbps == 500 && setting.SamplePointPercent == 87.5);
        Assert.Contains(table.GetFdDataPhasePresets(), setting => setting.BitrateKbps == 8_000);
    }
}
