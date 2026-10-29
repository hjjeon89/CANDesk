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

        await service.ConnectByVendorAsync("PEAK", expectedId, selection, configuration);

        Assert.Equal(expectedId, factory.CreatedId);
        Assert.Equal(configuration, service.CurrentConfiguration);
        Assert.Equal(CanDeviceStatus.Open, service.CurrentDevice!.Status);
        Assert.Equal($"PCAN-USB {expectedId[^1]} / {selection}", service.CurrentDevice.ChannelName);
    }

    [Fact]
    public async Task ConnectByVendorAsync_MissingPeakChannelDoesNotOpenAnotherBus()
    {
        var factory = new RecordingPeakFactory([CreateDescriptor(1)]);
        await using var service = new DeviceConnectionService([factory]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ConnectByVendorAsync("PEAK", "52", "Channel 2", new CanBusConfig(500000)));

        Assert.Contains("Channel 2", exception.Message);
        Assert.Null(factory.CreatedId);
        Assert.Null(service.CurrentDevice);
    }

    [Fact]
    public async Task ConnectByVendorAsync_ChannelTwoDoesNotRequireChannelOneToBePresent()
    {
        var factory = new RecordingPeakFactory([CreateDescriptor(2)]);
        await using var service = new DeviceConnectionService([factory]);

        await service.ConnectByVendorAsync("PEAK", "52", "Channel 2", new CanBusConfig(500000));

        Assert.Equal("52", factory.CreatedId);
    }

    [Theory]
    [InlineData("PEAK")]
    [InlineData("CANable")]
    public async Task ConnectByVendorAsync_SelectsExactDeviceWhenChannelsHaveSameName(string vendor)
    {
        var factory = new RecordingPeakFactory([
            new(vendor, "first", "Adapter 1", ["Channel 0"], true),
            new(vendor, "second", "Adapter 2", ["Channel 0"], true)], vendor);
        await using var service = new DeviceConnectionService([factory]);

        await service.ConnectByVendorAsync(vendor, "second", "Channel 0", new CanBusConfig(500000));

        Assert.Equal("second", factory.CreatedId);
        Assert.Equal("Adapter 2 / Channel 0", service.CurrentDevice!.ChannelName);
    }

    [Theory]
    [InlineData("missing", "Channel 0")]
    [InlineData("second", "Channel 9")]
    public async Task ConnectByVendorAsync_MissingSelectionKeepsExistingConnection(string deviceId, string channel)
    {
        var factory = new RecordingPeakFactory([
            new("CANable", "first", "Adapter 1", ["Channel 0"], true),
            new("CANable", "second", "Adapter 2", ["Channel 0"], true)], "CANable");
        await using var service = new DeviceConnectionService([factory]);
        await service.ConnectByVendorAsync("CANable", "first", "Channel 0", new CanBusConfig(500000));
        var connected = service.CurrentDevice;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ConnectByVendorAsync("CANable", deviceId, channel, new CanBusConfig(500000)));

        Assert.Same(connected, service.CurrentDevice);
        Assert.Equal(CanDeviceStatus.Open, connected!.Status);
        Assert.Equal("first", factory.CreatedId);
    }

    private static CanDeviceDescriptor CreateDescriptor(int number) => new("PEAK", (0x50 + number).ToString("X"),
        $"PCAN-USB {number}", [$"Channel {number}", $"PCAN-USB {number}"], SupportsCanFd: true);

    private sealed class RecordingPeakFactory(IReadOnlyList<CanDeviceDescriptor> descriptors, string vendor = "PEAK") : ICanDeviceFactory
    {
        public string Vendor => vendor;
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
