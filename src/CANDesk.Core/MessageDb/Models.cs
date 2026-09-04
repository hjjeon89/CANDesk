namespace CANDesk.Core.MessageDb;

public enum ByteOrder { Intel, Motorola }
public enum MultiplexorRole { None, Switch, Value }
public enum MessageDbFormat { Dbc, CandeskXml, CustomerCsv }

public sealed record DbcSignal(string Name, int StartBit, int BitLength, ByteOrder ByteOrder, bool IsSigned,
    double Factor = 1, double Offset = 0, double? Minimum = null, double? Maximum = null, string? Unit = null,
    MultiplexorRole MultiplexorRole = MultiplexorRole.None, int? MultiplexValue = null);
public sealed record DbcMessage(uint CanId, string Name, byte Dlc, IReadOnlyList<DbcSignal> Signals, bool IsExtended = false);
public sealed record DecodedSignal(uint CanId, string MessageName, string SignalName, double RawValue, double PhysicalValue,
    string? Unit, DateTime SystemTime);

public interface IMessageDatabase
{
    IReadOnlyCollection<DbcMessage> Messages { get; }
    bool TryGetMessage(uint canId, out DbcMessage message);
}

public sealed class MessageDatabase(IEnumerable<DbcMessage> messages) : IMessageDatabase
{
    private readonly Dictionary<uint, DbcMessage> _byId = messages.ToDictionary(message => message.CanId);
    public IReadOnlyCollection<DbcMessage> Messages => _byId.Values;
    public bool TryGetMessage(uint canId, out DbcMessage message) => _byId.TryGetValue(canId, out message!);
}

public interface IMessageDatabaseParser
{
    MessageDbFormat Format { get; }
    Task<IMessageDatabase> ParseAsync(Stream input, CancellationToken cancellationToken = default);
}

public interface IMessageDatabaseParserFactory { IMessageDatabaseParser GetParser(MessageDbFormat format); }
public sealed class MessageDatabaseParserFactory(IEnumerable<IMessageDatabaseParser> parsers) : IMessageDatabaseParserFactory
{
    private readonly IReadOnlyDictionary<MessageDbFormat, IMessageDatabaseParser> _parsers = parsers.ToDictionary(parser => parser.Format);
    public IMessageDatabaseParser GetParser(MessageDbFormat format) => _parsers.TryGetValue(format, out var parser)
        ? parser : throw new NotSupportedException($"No parser is registered for {format}.");
}
