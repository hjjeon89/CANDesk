using CANDesk.App;
using CANDesk.Hal;
using Xunit;

namespace CANDesk.Core.Tests;

public sealed class DeviceConnectionViewModelTests
{
    [Theory]
    [InlineData(250000, null, 87.5, 80, false, false)]
    [InlineData(500000, 1000000, 80, 80, true, false)]
    [InlineData(333000, 3000000, 82.5, 72.5, true, true)]
    public void ApplyBusConfiguration_UpdatesModeRatesAndSamplePoints(int nominal, int? data,
        double nominalSamplePoint, double dataSamplePoint, bool fd, bool customNominal)
    {
        var viewModel = CreateViewModel();
        viewModel.SelectedVendor = "CANable";
        viewModel.SelectedChannel = "Channel 2";
        viewModel.UseRawNominalTiming = true;
        viewModel.UseRawDataTiming = true;
        var configuration = new CanBusConfig((uint)nominal, data.HasValue ? (uint)data.Value : null,
            nominalSamplePoint, dataSamplePoint, fd);

        viewModel.ApplyBusConfiguration(configuration);

        var applied = viewModel.BuildConfiguration();
        applied.Validate();
        Assert.Equal(configuration.Mode, applied.Mode);
        Assert.Equal(nominal / 1000, applied.Nominal.BitrateKbps);
        Assert.Equal(nominalSamplePoint, applied.Nominal.SamplePointPercent);
        Assert.Equal(data / 1000, applied.Data?.BitrateKbps);
        if (fd) Assert.Equal(dataSamplePoint, applied.Data!.SamplePointPercent);
        Assert.False(viewModel.UseRawNominalTiming);
        if (fd) Assert.False(viewModel.UseRawDataTiming);
        Assert.Equal(customNominal, viewModel.UseCustomNominalTiming);
        if (!customNominal) Assert.Equal(configuration.Nominal, viewModel.SelectedNominalTiming);
        Assert.Equal("CANable", viewModel.SelectedVendor);
        Assert.Equal("Channel 2", viewModel.SelectedChannel);
    }

    [Fact]
    public void ApplyBusConfiguration_WithoutMetadataKeepsCurrentSettings()
    {
        var viewModel = CreateViewModel();
        viewModel.UseCustomNominalTiming = true;
        viewModel.CustomNominalBitrateKbps = "333";
        viewModel.CustomNominalSamplePoint = "82.5";
        var before = viewModel.BuildConfiguration();

        viewModel.ApplyBusConfiguration(null);

        Assert.Equal(before, viewModel.BuildConfiguration());
    }

    [Fact]
    public void ApplyBusConfiguration_MissingFdDataRateKeepsDataSetting()
    {
        var viewModel = CreateViewModel();
        viewModel.SelectedDataTiming = viewModel.DataPresets.Single(setting => setting.BitrateKbps == 4000);

        viewModel.ApplyBusConfiguration(new CanBusConfig(250000, enableCanFd: true));

        Assert.True(viewModel.IsFdMode);
        Assert.Equal(250, viewModel.BuildConfiguration().Nominal.BitrateKbps);
        Assert.Equal(4000, viewModel.BuildConfiguration().Data!.BitrateKbps);
    }

    [Fact]
    public void ApplyBusConfiguration_SwitchingToClassicClearsCustomNominalOverride()
    {
        var viewModel = CreateViewModel();
        viewModel.ApplyBusConfiguration(new CanBusConfig(333000, 1000000, 82.5, 80));
        Assert.True(viewModel.UseCustomNominalTiming);

        viewModel.ApplyBusConfiguration(new CanBusConfig(250000));

        Assert.True(viewModel.IsClassicMode);
        Assert.False(viewModel.UseCustomNominalTiming);
        Assert.Equal(250, viewModel.SelectedNominalTiming.BitrateKbps);
        Assert.Null(viewModel.BuildConfiguration().Data);
    }

    private static DeviceConnectionViewModel CreateViewModel() => new(new BitrateTableProvider(), new BitTimingCalculator());

    [Theory]
    [InlineData(40, 1, 63, 16, 16, 1, 31, 8, 8)]
    [InlineData(80, 1, 127, 32, 32, 8, 7, 2, 1)]
    public void BuildConfiguration_PreservesPcanViewRawTimingAndClock(int clockMHz,
        int nominalBrp, int nominalTseg1, int nominalTseg2, int nominalSjw,
        int dataBrp, int dataTseg1, int dataTseg2, int dataSjw)
    {
        var viewModel = CreateViewModel();
        viewModel.Mode = CanBusMode.Fd;
        viewModel.UseRawNominalTiming = true;
        viewModel.UseRawDataTiming = true;
        viewModel.NominalPrescaler = nominalBrp.ToString();
        viewModel.NominalTseg1 = nominalTseg1.ToString();
        viewModel.NominalTseg2 = nominalTseg2.ToString();
        viewModel.NominalSjw = nominalSjw.ToString();
        viewModel.DataPrescaler = dataBrp.ToString();
        viewModel.DataTseg1 = dataTseg1.ToString();
        viewModel.DataTseg2 = dataTseg2.ToString();
        viewModel.DataSjw = dataSjw.ToString();
        viewModel.ControllerClockMHz = clockMHz;
        viewModel.SelectedNominalTiming = viewModel.NominalPresets.Single(setting => setting.BitrateKbps == 250);
        viewModel.SelectedDataTiming = viewModel.DataPresets.Single(setting => setting.BitrateKbps == 4000);
        viewModel.UseCustomNominalTiming = true;
        viewModel.CustomNominalBitrateKbps = "125";
        viewModel.UseCustomDataTiming = true;
        viewModel.CustomDataBitrateKbps = "5000";

        var configuration = viewModel.BuildConfiguration();

        Assert.Equal(clockMHz * 1_000_000, configuration.ClockFrequencyHz);
        Assert.Equal(new RawBitTimingSegments(nominalBrp, nominalTseg1, nominalTseg2, nominalSjw), configuration.NominalTiming.RawSegments);
        Assert.Equal(new RawBitTimingSegments(dataBrp, dataTseg1, dataTseg2, dataSjw), configuration.DataTiming!.RawSegments);
        Assert.Equal(500, configuration.Nominal.BitrateKbps);
        Assert.Equal(1000, configuration.Data!.BitrateKbps);
        Assert.Contains("500 kbps / 80%", viewModel.NominalRawDescription);
    }

    [Theory]
    [InlineData(80)]
    [InlineData(60)]
    [InlineData(40)]
    [InlineData(30)]
    [InlineData(24)]
    [InlineData(20)]
    public void BuildConfiguration_UsesSelectedPeakFdClockForPresetPreview(int clockMHz)
    {
        var viewModel = CreateViewModel();
        viewModel.Mode = CanBusMode.Fd;
        viewModel.ControllerClockMHz = clockMHz;
        var configuration = viewModel.BuildConfiguration();
        var expected = new BitTimingCalculator().Calculate(configuration.Nominal, clockMHz * 1_000_000);
        Assert.Equal(clockMHz * 1_000_000, configuration.ClockFrequencyHz);
        Assert.Equal(expected.Prescaler.ToString(), viewModel.NominalPrescaler);
        Assert.Equal(expected.Tseg1.ToString(), viewModel.NominalTseg1);
    }
}
