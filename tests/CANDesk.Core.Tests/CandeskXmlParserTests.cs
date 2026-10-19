using System.Text;
using CANDesk.Core.Dispatch;
using CANDesk.Core.MessageDb;
using CANDesk.Hal;
using Xunit;

namespace CANDesk.Core.Tests;

public sealed class CandeskXmlParserTests
{
    [Theory]
    [InlineData("", null, null)]
    [InlineData("<dbcInfomation baudrate='250000' CANTYPE='CAN' CANFDBitrate='' />", 250, null)]
    [InlineData("<dbcInfomation baudrate='500000' CANTYPE='CAN FD' CANFDBitrate='2000000' />", 500, 2000)]
    public async Task ParseAsync_ReadsLegacyBusSettings(string metadata, int? nominal, int? data)
    {
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes($"<DBCData>{metadata}</DBCData>"));
        var database = await new CandeskXmlParser().ParseAsync(input);
        Assert.Equal(nominal, database.BusConfiguration?.Nominal.BitrateKbps);
        Assert.Equal(data, database.BusConfiguration?.Data?.BitrateKbps);
        if (nominal.HasValue)
            Assert.Equal(data.HasValue ? CanBusMode.Fd : CanBusMode.Classic, database.BusConfiguration!.Mode);
    }

    [Theory]
    [InlineData("aa.xml", 1, 4)]
    [InlineData("LifeCycle_rev0p3_CAN_DB - 배포_full.xml", 20, 90)]
    [InlineData("VolvoCan_4.xml", 18, 163)]
    public async Task ParseAsync_LoadsSampleDatabases(string filename, int messageCount, int signalCount)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "sample")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        await using var input = File.OpenRead(Path.Combine(directory.FullName, "sample", filename));

        var database = await new CandeskXmlParser().ParseAsync(input);

        Assert.Equal(messageCount, database.Messages.Count);
        Assert.Equal(signalCount, database.Messages.Sum(message => message.Signals.Count));
        Assert.All(database.Messages, message =>
        {
            Assert.InRange(message.CanId, 0u, 0x1FFF_FFFFu);
            Assert.True(message.IsExtended);
        });
        if (filename.StartsWith("LifeCycle", StringComparison.Ordinal))
        {
            Assert.True(database.TryGetMessage(0x1F010002, out var message));
            var current = Assert.Single(message.Signals, signal => signal.Name == "Pack_A");
            Assert.True(current.IsSigned);
            Assert.Equal(0.1, current.Factor);
            Assert.Equal(-200, current.Minimum);
            Assert.Equal(200, current.Maximum);
            Assert.Equal(-1, SignalDecoder.DecodeValue(new byte[] { 0, 0, 0, 0, 0, 0, 0xF6, 0xFF }, current));
            Assert.True(database.TryGetMessage(0x1F010005, out var temperature));
            Assert.Equal("℃", temperature.Signals[0].Unit);
        }
    }

    [Theory]
    [InlineData("0x123", 0x123u, false)]
    [InlineData("291", 0x123u, false)]
    [InlineData("0x80000123", 0x123u, true)]
    [InlineData("0x1F020000", 0x1F020000u, true)]
    public async Task ParseDocumentAsync_ReadsDbcDataIdsAndSignalAttributes(string id, uint expectedId, bool extended)
    {
        var xml = $"""
            <DBCData>
              <CanFrame bmsid="{id}" name="Status" DLC="8">
                <signal name="Temperature" startbit="7" length="16" byteorder="Motorola"
                        valuetype="Signed" factor="0.1" offset="-40" min="-50" max="150" unit="C" />
                <signal name="Counter" startbit="16" length="8" min="0" max="0" />
              </CanFrame>
              <sequence><CanFrame bmsid="{id}" name="Repeated" DLC="8" /></sequence>
            </DBCData>
            """;
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes(xml));

        var document = await new CandeskXmlParser().ParseDocumentAsync(input);

        var message = Assert.Single(document.Database.Messages);
        Assert.Equal(expectedId, message.CanId);
        Assert.Equal(extended, message.IsExtended);
        Assert.Equal((byte)8, message.Dlc);
        Assert.Equal(new DbcSignal("Temperature", 7, 16, ByteOrder.Motorola, true, 0.1, -40, -50, 150, "C"), message.Signals[0]);
        Assert.Equal(new DbcSignal("Counter", 16, 8, ByteOrder.Intel, false), message.Signals[1]);
        Assert.Empty(document.Nodes);
        Assert.Empty(document.Assignments);
    }

    [Theory]
    [InlineData("<unrelated />")]
    [InlineData("<DBCData><CanFrame bmsid='0x60000123' name='Bad' DLC='8' /></DBCData>")]
    [InlineData("<DBCData><CanFrame name='Bad' DLC='8' /></DBCData>")]
    [InlineData("<DBCData><CanFrame bmsid='0x123' name='Bad' DLC='8'><signal name='Bad' startbit='0' length='8' valuetype='Float' /></CanFrame></DBCData>")]
    public async Task ParseAsync_RejectsUnsupportedOrIncompleteXml(string xml)
    {
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes(xml));

        await Assert.ThrowsAsync<FormatException>(() => new CandeskXmlParser().ParseAsync(input));
    }

    [Fact]
    public async Task WriteThenParseDocument_RoundTripsMessagesSignalsNodesAndAssignments()
    {
        var database = new MessageDatabase([
            new DbcMessage(0x301, "VCU_Control", 8,
            [
                new("TorqueRequest", 0, 16, ByteOrder.Intel, true, 1, 0, Unit: "Nm"),
                new("GearSelector", 24, 4, ByteOrder.Intel, false, MultiplexorRole: MultiplexorRole.Switch),
                new("GearRatio", 28, 8, ByteOrder.Intel, false, 0.1, 0, Minimum: 0, Maximum: 10, MultiplexorRole: MultiplexorRole.Value, MultiplexValue: 1)
            ])
        ]);
        var nodes = new[] { new CanNode("VCU"), new CanNode("BMS") };
        var assignments = new Dictionary<uint, MessageNodeAssignment>
        {
            [0x301] = new("VCU", new HashSet<string> { "BMS" })
        };

        await using var stream = new MemoryStream();
        await new CandeskXmlWriter().WriteAsync(nodes, database, canId => assignments[canId], stream);
        stream.Position = 0;

        var document = await new CandeskXmlParser().ParseDocumentAsync(stream);

        Assert.Equal(["BMS", "VCU"], document.Nodes.Select(node => node.Name).OrderBy(name => name, StringComparer.Ordinal));
        var message = Assert.Single(document.Database.Messages);
        Assert.Equal(0x301u, message.CanId);
        Assert.Equal("VCU_Control", message.Name);
        Assert.Equal((byte)8, message.Dlc);
        Assert.False(message.IsExtended);

        var assignment = document.Assignments[0x301];
        Assert.Equal("VCU", assignment.TransmitterNode);
        Assert.Equal(["BMS"], assignment.ReceiverNodes);

        Assert.Collection(message.Signals,
            signal =>
            {
                Assert.Equal("TorqueRequest", signal.Name);
                Assert.True(signal.IsSigned);
                Assert.Equal("Nm", signal.Unit);
            },
            signal =>
            {
                Assert.Equal(MultiplexorRole.Switch, signal.MultiplexorRole);
            },
            signal =>
            {
                Assert.Equal(MultiplexorRole.Value, signal.MultiplexorRole);
                Assert.Equal(1, signal.MultiplexValue);
                Assert.Equal(0, signal.Minimum);
                Assert.Equal(10, signal.Maximum);
            });
    }

    [Fact]
    public async Task ParseAsync_IgnoresNodeAndAssignmentMetadata()
    {
        var database = new MessageDatabase([new DbcMessage(0x100, "EngineStatus", 8, [])]);
        var nodes = new[] { new CanNode("ECU") };
        await using var stream = new MemoryStream();
        await new CandeskXmlWriter().WriteAsync(nodes, database, _ => new MessageNodeAssignment("ECU", new HashSet<string>()), stream);
        stream.Position = 0;

        var parsedDatabase = await new CandeskXmlParser().ParseAsync(stream);

        var message = Assert.Single(parsedDatabase.Messages);
        Assert.Equal(0x100u, message.CanId);
    }

    [Fact]
    public async Task Save_ThenReopen_SeedsEditableDatabaseWithNodesAndAssignments()
    {
        var database = new MessageDatabase([new DbcMessage(0x200, "BatteryPackStatus", 8, [])]);
        var editable = new EditableMessageDatabase(database);
        editable.AddNode("BMS");
        editable.SetTransmitterNode(0x200, "BMS");

        await using var stream = new MemoryStream();
        await new CandeskXmlWriter().WriteAsync(editable.Nodes, editable.Snapshot, editable.GetNodeAssignment, stream);
        stream.Position = 0;

        var document = await new CandeskXmlParser().ParseDocumentAsync(stream);
        var reopened = new EditableMessageDatabase(document.Database, document.Nodes, document.Assignments);

        Assert.Equal("BMS", Assert.Single(reopened.Nodes).Name);
        Assert.Equal("BMS", reopened.GetNodeAssignment(0x200).TransmitterNode);
    }
}
