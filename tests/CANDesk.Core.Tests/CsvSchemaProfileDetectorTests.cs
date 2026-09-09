using CANDesk.Core.MessageDb;
using Xunit;

namespace CANDesk.Core.Tests;

public sealed class CsvSchemaProfileDetectorTests
{
    [Fact]
    public void Detect_MapsAllMandatoryAndOptionalFieldsFromCommonHeaderNames()
    {
        string[] header =
        [
            "Message Name", "CAN ID", "DLC", "Signal Name", "Start Bit", "Bit Length",
            "Byte Order", "Signed", "Factor", "Offset", "Min", "Max", "Unit",
        ];

        var profile = CsvSchemaProfileDetector.Detect("test", header);

        Assert.Empty(profile.MissingMandatoryFields);
        Assert.Equal("Message Name", profile.ColumnByField[CsvField.MessageName]);
        Assert.Equal("CAN ID", profile.ColumnByField[CsvField.CanId]);
        Assert.Equal("Signal Name", profile.ColumnByField[CsvField.SignalName]);
        Assert.Equal("Start Bit", profile.ColumnByField[CsvField.StartBit]);
        Assert.Equal("Bit Length", profile.ColumnByField[CsvField.BitLength]);
        Assert.Equal("Unit", profile.ColumnByField[CsvField.Unit]);
    }

    [Fact]
    public void Detect_LeavesUnrecognizedColumnsUnmapped_ReportedAsMissingMandatory()
    {
        string[] header = ["Weird Column A", "Weird Column B"];

        var profile = CsvSchemaProfileDetector.Detect("test", header);

        Assert.Empty(profile.ColumnByField);
        Assert.Equal(CsvSchemaProfile.MandatoryFields, profile.MissingMandatoryFields);
    }

    [Fact]
    public void Detect_DoesNotLetTwoFieldsClaimTheSameColumn()
    {
        // "Name" alone should resolve to SignalName, not also be claimed by MessageName.
        string[] header = ["CAN ID", "Start Bit", "Bit Length", "Name"];

        var profile = CsvSchemaProfileDetector.Detect("test", header);

        Assert.Equal("Name", profile.ColumnByField[CsvField.SignalName]);
        Assert.False(profile.ColumnByField.ContainsKey(CsvField.MessageName));
    }
}
