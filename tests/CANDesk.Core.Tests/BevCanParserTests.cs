using System.Text;
using CANDesk.Core.Dispatch;
using CANDesk.Core.MessageDb;
using CANDesk.Hal;
using Xunit;

namespace CANDesk.Core.Tests;

public sealed class BevCanParserTests
{
    [Theory]
    [InlineData("G-CANFD.bevcan", 18, 109, 5)]
    [InlineData("LifeCycle_rev0p3_CAN_DB - 배포.bevcan", 79, 527, 2)]
    [InlineData("VEH_CAN.bevcan", 24, 200, 2)]
    public async Task ParseDocumentAsync_LoadsSamples(string filename, int messageCount, int signalCount, int nodeCount)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "sample")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        await using var input = File.OpenRead(Path.Combine(directory.FullName, "sample", filename));

        var document = await new BevCanParser().ParseDocumentAsync(input);

        Assert.Equal(messageCount, document.Database.Messages.Count);
        Assert.Equal(signalCount, document.Database.Messages.Sum(message => message.Signals.Count));
        Assert.Equal(nodeCount, document.Nodes.Count);
        var bus = Assert.IsType<CanBusConfig>(document.Database.BusConfiguration);
        Assert.Equal(filename == "G-CANFD.bevcan" ? CanBusMode.Fd : CanBusMode.Classic, bus.Mode);
        Assert.Equal(filename == "G-CANFD.bevcan" ? 500 : 250, bus.Nominal.BitrateKbps);
        Assert.Equal(filename == "G-CANFD.bevcan" ? 80 : 87.5, bus.Nominal.SamplePointPercent);
        if (bus.IsCanFd) Assert.Equal(new BitTimingSetting(1000, 80), bus.Data);
        else Assert.Null(bus.Data);
        var editable = new EditableMessageDatabase(document.Database, document.Nodes, document.Assignments);
        if (filename == "G-CANFD.bevcan")
        {
            Assert.True(document.Database.TryGetMessage(0x45, out var message));
            Assert.Equal((byte)24, message.Dlc);
            Assert.False(message.IsExtended);
            Assert.Equal("CGW", editable.GetNodeAssignment(0x45).TransmitterNode);
            Assert.Equal("tester", editable.GetNodeAssignment(0x1C5).TransmitterNode);
            Assert.All(message.Signals, signal => Assert.Null(signal.Minimum));
        }
        else if (filename == "VEH_CAN.bevcan")
        {
            Assert.True(document.Database.TryGetMessage(0x0CFF72F3, out var message));
            Assert.True(message.IsExtended);
            var assignment = editable.GetNodeAssignment(message.CanId);
            Assert.Equal("BMU", assignment.TransmitterNode);
            Assert.Contains("HPCU", assignment.ReceiverNodes);
            var current = Assert.Single(message.Signals, signal => signal.Name == "actCurrent");
            Assert.Equal(0.05, current.Factor);
            Assert.Equal(-1600, current.Offset);
            Assert.Equal(-1600, current.Minimum);
            Assert.Equal("A", current.Unit);
        }
        else
        {
            Assert.True(document.Database.TryGetMessage(0x1F010005, out var message));
            Assert.Equal("tester", editable.GetNodeAssignment(message.CanId).TransmitterNode);
            var temperature = Assert.Single(message.Signals, signal => signal.Name == "Cell_T_Avg");
            Assert.True(temperature.IsSigned);
            Assert.Equal("℃", temperature.Unit);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ParseDocumentAsync_ReadsVersionsByteOrderMultiplexingAndAssignments(ushort version)
    {
        await using var input = new MemoryStream(CreateDatabase(version));
        var document = await new BevCanParser().ParseDocumentAsync(input);

        var bus = Assert.IsType<CanBusConfig>(document.Database.BusConfiguration);
        Assert.Equal(CanBusMode.Fd, bus.Mode);
        Assert.Equal(new BitTimingSetting(500, version >= 3 ? 80 : 87.5), bus.Nominal);
        Assert.Equal(version >= 3 ? new BitTimingSetting(1000, 80) : null, bus.Data);
        var message = Assert.Single(document.Database.Messages);
        Assert.Equal(0x123u, message.CanId);
        Assert.True(message.IsExtended);
        Assert.Equal(new DbcSignal("Temperature", 15, 16, ByteOrder.Motorola, true, 0.1, -40, -50, 150, "℃",
            MultiplexorRole.Value, 2), message.Signals[1]);
        Assert.Equal(MultiplexorRole.Switch, message.Signals[0].MultiplexorRole);
        Assert.Null(message.Signals[2].Minimum);
        Assert.Null(message.Signals[2].Maximum);
        Assert.Equal(ByteOrder.Intel, message.Signals[2].ByteOrder);
        var payload = new byte[8];
        SignalEncoder.Encode(payload, message.Signals[1], -41);
        Assert.Equal(-41, SignalDecoder.DecodeValue(payload, message.Signals[1]));
        Assert.Equal(version == 1 ? "Original" : "Sender", document.Assignments[0x123].TransmitterNode);
        Assert.Contains("Receiver", document.Assignments[0x123].ReceiverNodes);

        await using var saved = new MemoryStream();
        await new CandeskXmlWriter().WriteAsync(document.Nodes, document.Database,
            id => document.Assignments[id], saved);
        saved.Position = 0;
        var reopened = await new CandeskXmlParser().ParseDocumentAsync(saved);
        Assert.Equal(document.Database.BusConfiguration, reopened.Database.BusConfiguration);
        Assert.Equal(document.Database.BusConfiguration, new EditableMessageDatabase(document.Database).Snapshot.BusConfiguration);
        Assert.Equal(message.Signals, Assert.Single(reopened.Database.Messages).Signals);
        Assert.Equal(document.Assignments[0x123].TransmitterNode, reopened.Assignments[0x123].TransmitterNode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public async Task ParseAsync_RejectsUnknownVersions(ushort version)
    {
        await using var input = new MemoryStream(CreateDatabase(version));
        await Assert.ThrowsAsync<InvalidDataException>(() => new BevCanParser().ParseAsync(input));
    }

    [Fact]
    public async Task ParseAsync_RejectsInvalidHeader()
    {
        var data = CreateDatabase(3);
        data[0] = 0;
        await using var input = new MemoryStream(data);
        await Assert.ThrowsAsync<InvalidDataException>(() => new BevCanParser().ParseAsync(input));
    }

    [Fact]
    public async Task ParseAsync_RejectsTruncatedValueTables()
    {
        await using var input = new MemoryStream(CreateDatabase(3)[..^1]);
        await Assert.ThrowsAsync<EndOfStreamException>(() => new BevCanParser().ParseAsync(input));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public async Task ParseAsync_RejectsInvalidCounts(int count)
    {
        var data = CreateDatabase(3);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(data.Length - 4), count);
        await using var input = new MemoryStream(data);
        await Assert.ThrowsAsync<InvalidDataException>(() => new BevCanParser().ParseAsync(input));
    }

    [Fact]
    public async Task ParseDocumentAsync_PrefersFinalTransmitterOverEarlierNodeAssignments()
    {
        await using var input = new MemoryStream(CreateDatabase(3, transmitter: "Receiver", duplicateTx: true));
        var document = await new BevCanParser().ParseDocumentAsync(input);
        Assert.Equal("Receiver", document.Assignments[0x123].TransmitterNode);
    }

    [Fact]
    public async Task ParseDocumentAsync_RejectsAmbiguousTransmitterWithoutFinalAssignment()
    {
        await using var input = new MemoryStream(CreateDatabase(3, duplicateTx: true));
        await Assert.ThrowsAsync<InvalidDataException>(() => new BevCanParser().ParseDocumentAsync(input));
    }

    [Fact]
    public async Task ParseAsync_RejectsUnknownByteOrder()
    {
        await using var input = new MemoryStream(CreateDatabase(3, 2));
        await Assert.ThrowsAsync<InvalidDataException>(() => new BevCanParser().ParseAsync(input));
    }

    [Theory]
    [InlineData(-1000, 80, 1)]
    [InlineData(500001, 80, 1)]
    [InlineData(500000, 0, 1)]
    [InlineData(500000, 100, 1)]
    [InlineData(500000, double.NaN, 1)]
    [InlineData(500000, double.PositiveInfinity, 1)]
    [InlineData(500000, 80, 2)]
    public async Task ParseAsync_RejectsInvalidBusSettings(int bitrate, double samplePoint, byte protocol)
    {
        await using var input = new MemoryStream(CreateDatabase(3, nominalBitrate: bitrate,
            nominalSamplePoint: samplePoint, protocol: protocol));
        var exception = await Record.ExceptionAsync(() => new BevCanParser().ParseAsync(input));
        Assert.True(exception is FormatException or InvalidDataException);
    }

    private static byte[] CreateDatabase(ushort version, byte byteOrder = 0, string transmitter = "", bool duplicateTx = false,
        int nominalBitrate = 500000, double nominalSamplePoint = 80, byte protocol = 1)
    {
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true);
        writer.Write(0x4E41_4342u);
        writer.Write(version);
        writer.Write(0L);
        foreach (var value in new[] { "original.dbc", "hash", "1", "CAN FD" }) writer.Write(value);
        writer.Write(500000);
        if (version >= 3)
        {
            writer.Write(protocol);
            writer.Write("CAN FD 500K/1M");
            for (var index = 0; index < 2; index++)
            {
                writer.Write(index == 0 ? nominalBitrate : 1000000);
                writer.Write(index == 0 ? nominalSamplePoint : 80d);
                writer.Write(1);
                writer.Write(15);
                writer.Write(4);
            }
        }
        writer.Write(2);
        foreach (var node in new[] { "Sender", "Receiver" })
        {
            writer.Write(node);
            if (version < 2) continue;
            writer.Write(node == "Sender" || duplicateTx ? 1 : 0);
            if (node == "Sender" || duplicateTx) writer.Write(0x8000_0123u);
            writer.Write(node == "Receiver" ? 1 : 0);
            if (node == "Receiver") writer.Write(0x8000_0123u);
        }
        writer.Write(1);
        writer.Write(0x8000_0123u);
        writer.Write(0x123u);
        writer.Write(true);
        writer.Write("Status");
        writer.Write((byte)8);
        writer.Write(version == 1 ? "Original" : transmitter);
        writer.Write(100);
        writer.Write(3);
        foreach (var name in new[] { "Mode", "Temperature", "Counter" })
        {
            var temperature = name == "Temperature";
            writer.Write(name);
            writer.Write(temperature ? 15 : name == "Mode" ? 0 : 24);
            writer.Write(temperature ? 16 : 4);
            writer.Write(temperature ? byteOrder : (byte)1);
            writer.Write(temperature);
            writer.Write(temperature ? 0.1 : 1d);
            writer.Write(temperature ? -40d : 0d);
            writer.Write(temperature ? -50d : 0d);
            writer.Write(temperature ? 150d : 0d);
            writer.Write(temperature ? "℃" : "");
            writer.Write(temperature ? "m2" : name == "Mode" ? "M" : "");
            writer.Write(1);
            writer.Write("Receiver");
        }
        writer.Write(0);
        return buffer.ToArray();
    }
}
