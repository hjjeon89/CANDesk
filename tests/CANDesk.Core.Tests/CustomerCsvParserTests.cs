using System.Text;
using CANDesk.Core.MessageDb;
using Xunit;

namespace CANDesk.Core.Tests;

public sealed class CustomerCsvParserTests
{
    [Fact]
    public async Task ParseAsync_AutoDetectsCommonHeaderAndGroupsSignalsByCanId()
    {
        const string csv = """
            Message Name,CAN ID,DLC,Signal Name,Start Bit,Bit Length,Byte Order,Signed,Factor,Offset,Min,Max,Unit
            EngineStatus,0x100,8,EngineSpeed,0,16,Intel,false,0.25,0,0,16383.75,rpm
            EngineStatus,0x100,8,CoolantTemp,16,8,Intel,true,1,-40,-40,215,C
            """;
        var parser = new CustomerCsvParser();
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes(csv));

        var database = await parser.ParseAsync(input);

        var message = Assert.Single(database.Messages);
        Assert.Equal(0x100u, message.CanId);
        Assert.Equal("EngineStatus", message.Name);
        Assert.Equal((byte)8, message.Dlc);
        Assert.Collection(message.Signals,
            signal =>
            {
                Assert.Equal("EngineSpeed", signal.Name);
                Assert.Equal(0, signal.StartBit);
                Assert.Equal(16, signal.BitLength);
                Assert.Equal(ByteOrder.Intel, signal.ByteOrder);
                Assert.False(signal.IsSigned);
                Assert.Equal(0.25, signal.Factor);
                Assert.Equal("rpm", signal.Unit);
            },
            signal =>
            {
                Assert.Equal("CoolantTemp", signal.Name);
                Assert.True(signal.IsSigned);
                Assert.Equal(-40, signal.Offset);
                Assert.Equal("C", signal.Unit);
            });
    }

    [Fact]
    public async Task ParseAsync_ThrowsCsvMappingRequiredWhenMandatoryColumnsCannotBeDetected()
    {
        const string csv = """
            Foo,Bar
            1,2
            """;
        var parser = new CustomerCsvParser();
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes(csv));

        var exception = await Assert.ThrowsAsync<CsvMappingRequiredException>(() => parser.ParseAsync(input));

        Assert.NotEmpty(exception.DetectedProfile.MissingMandatoryFields);
        Assert.Equal(["Foo", "Bar"], exception.Header);
    }

    [Fact]
    public async Task ParseWithProfileAsync_UsesExplicitMappingAndDefaultsMissingOptionalFields()
    {
        const string csv = """
            id;sig;start;len
            256;Locked;3;1
            """;
        var profile = new CsvSchemaProfile(
            "custom-semicolon",
            new Dictionary<CsvField, string>
            {
                [CsvField.CanId] = "id",
                [CsvField.SignalName] = "sig",
                [CsvField.StartBit] = "start",
                [CsvField.BitLength] = "len",
            },
            Delimiter: ';');
        var parser = new CustomerCsvParser();
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes(csv));

        var database = await parser.ParseWithProfileAsync(input, profile);

        var message = Assert.Single(database.Messages);
        Assert.Equal(256u, message.CanId);
        Assert.Equal("MSG_100", message.Name); // no MessageName column mapped -> hex-id fallback
        Assert.Equal((byte)8, message.Dlc);    // no Dlc column mapped -> Classic CAN default
        var signal = Assert.Single(message.Signals);
        Assert.Equal("Locked", signal.Name);
        Assert.Equal(3, signal.StartBit);
        Assert.Equal(1, signal.BitLength);
        Assert.Equal(1, signal.Factor); // default factor
        Assert.Equal(0, signal.Offset); // default offset
    }

    [Fact]
    public async Task ParseWithProfileAsync_ThrowsWhenProfileIsMissingAMandatoryColumn()
    {
        var incompleteProfile = new CsvSchemaProfile(
            "incomplete",
            new Dictionary<CsvField, string> { [CsvField.CanId] = "id" });
        var parser = new CustomerCsvParser();
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes("id\n1\n"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => parser.ParseWithProfileAsync(input, incompleteProfile));
    }
}
