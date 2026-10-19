using CANDesk.App;
using CANDesk.Hal;
using CANDesk.Hal.Mock;
using Xunit;

namespace CANDesk.Core.Tests;

public sealed class DeviceConnectionServiceTests
{
    [Theory]
    [InlineData("Channel 1", "51")]
    [InlineData("Channel 2", "52")]
    [InlineData("PCAN-USB 2", "52")]
    public async Task ConnectByVendorAsync_SelectsRequestedPeakChannel(string selection, string expectedId)
    {
        var factory = new RecordingPeakFactory([CreateDescriptor(1), CreateDescriptor(2)]);
        await using var service = new DeviceConnectionService([factory]);
        var configuration = new CanBusConfig(500000, 1000000, 80, 80);

        await service.ConnectByVendorAsync("PEAK", selection, configuration);

        Assert.Equal(expectedId, factory.CreatedId);
        Assert.Equal(configuration, service.CurrentConfiguration);
        Assert.Equal(CanDeviceStatus.Open, service.CurrentDevice!.Status);
        Assert.Equal($"PCAN-USB {expectedId[^1]}", service.CurrentDevice.ChannelName);
    }

    [Fact]
    public async Task ConnectByVendorAsync_MissingPeakChannelDoesNotOpenAnotherBus()
    {
        var factory = new RecordingPeakFactory([CreateDescriptor(1)]);
        await using var service = new DeviceConnectionService([factory]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ConnectByVendorAsync("PEAK", "Channel 2", new CanBusConfig(500000)));

        Assert.Contains("Channel 2", exception.Message);
        Assert.Null(factory.CreatedId);
        Assert.Null(service.CurrentDevice);
    }

    [Fact]
    public async Task ConnectByVendorAsync_ChannelTwoDoesNotRequireChannelOneToBePresent()
    {
        var factory = new RecordingPeakFactory([CreateDescriptor(2)]);
        await using var service = new DeviceConnectionService([factory]);

        await service.ConnectByVendorAsync("PEAK", "Channel 2", new CanBusConfig(500000));

        Assert.Equal("52", factory.CreatedId);
    }

    private static CanDeviceDescriptor CreateDescriptor(int number) => new("PEAK", (0x50 + number).ToString("X"),
        $"PCAN-USB {number}", [$"Channel {number}", $"PCAN-USB {number}"], SupportsCanFd: true);

    private sealed class RecordingPeakFactory(IReadOnlyList<CanDeviceDescriptor> descriptors) : ICanDeviceFactory
    {
        public string Vendor => "PEAK";
        public string? CreatedId { get; private set; }
        public Task<IReadOnlyList<CanDeviceDescriptor>> EnumerateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(descriptors);

        public Task<ICanDevice> CreateAsync(string deviceId, string channelName, CancellationToken cancellationToken = default)
        {
            CreatedId = deviceId;
            return Task.FromResult<ICanDevice>(new MockCanDevice(channelName));
        }
    }
}
