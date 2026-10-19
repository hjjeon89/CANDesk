using System.Globalization;
using System.Text;
using CANDesk.Core.Dispatch;

namespace CANDesk.Core.MessageDb;

/// <summary>Reads the BEVS converter's BCAN binary schemas 1–3.</summary>
public sealed class BevCanParser : IMessageDatabaseParser
{
    public MessageDbFormat Format => MessageDbFormat.BevCan;

    public async Task<IMessageDatabase> ParseAsync(Stream input, CancellationToken cancellationToken = default) =>
        (await ParseDocumentAsync(input, cancellationToken).ConfigureAwait(false)).Database;

    public async Task<CandeskXmlDocument> ParseDocumentAsync(Stream input, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        await input.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        buffer.Position = 0;
        using var reader = new BinaryReader(buffer, new UTF8Encoding(false, true), leaveOpen: true);
        if (reader.ReadUInt32() != 0x4E41_4342u)
            throw new InvalidDataException("Expected BEVCAN (BCAN) file header.");
        var version = reader.ReadUInt16();
        if (version is < 1 or > 3)
            throw new InvalidDataException($"Unsupported BEVCAN schema version: {version}.");

        reader.ReadInt64(); // Creation time.
        for (var index = 0; index < 3; index++) reader.ReadString(); // Source path/hash, version.
        var busType = reader.ReadString();
        var baudrate = reader.ReadInt32();
        var busConfiguration = MessageDbBusConfiguration.Create(baudrate, busType);
        if (version >= 3)
        {
            var protocol = reader.ReadByte() switch
            {
                0 => "CAN",
                1 => "CAN FD",
                var value => throw new InvalidDataException($"Invalid BEVCAN bus protocol: {value}.")
            };
            reader.ReadString(); // Preset name.
            var nominal = ReadTiming(reader);
            var data = ReadTiming(reader);
            busConfiguration = MessageDbBusConfiguration.Create(nominal.Bitrate, protocol,
                protocol == "CAN FD" ? data.Bitrate : 0, nominal.SamplePoint, data.SamplePoint);
        }

        var nodes = new List<CanNode>();
        var nodeTx = new Dictionary<uint, HashSet<string>>();
        var nodeRx = new Dictionary<uint, HashSet<string>>();
        var nodeCount = ReadCount(reader);
        for (var index = 0; index < nodeCount; index++)
        {
            var name = reader.ReadString();
            nodes.Add(new CanNode(name));
            if (version < 2) continue;
            var txCount = ReadCount(reader);
            for (var txIndex = 0; txIndex < txCount; txIndex++)
            {
                var storedId = reader.ReadUInt32();
                if (!nodeTx.TryGetValue(storedId, out var transmitters))
                    nodeTx[storedId] = transmitters = new HashSet<string>(StringComparer.Ordinal);
                transmitters.Add(name);
            }
            var rxCount = ReadCount(reader);
            for (var rxIndex = 0; rxIndex < rxCount; rxIndex++)
            {
                var storedId = reader.ReadUInt32();
                if (!nodeRx.TryGetValue(storedId, out var receivers))
                    nodeRx[storedId] = receivers = new HashSet<string>(StringComparer.Ordinal);
                receivers.Add(name);
            }
        }

        var messages = new List<DbcMessage>();
        var assignments = new Dictionary<uint, MessageNodeAssignment>();
        var messageCount = ReadCount(reader);
        for (var index = 0; index < messageCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var storedId = reader.ReadUInt32();
            var canId = reader.ReadUInt32();
            var extended = reader.ReadBoolean();
            var name = reader.ReadString();
            var dlc = reader.ReadByte();
            var transmitter = reader.ReadString();
            reader.ReadInt32(); // Cycle time; loading a database does not start periodic transmission.
            if (canId > (extended ? 0x1FFF_FFFFu : 0x7FFu) || dlc > 64)
                throw new InvalidDataException($"Invalid BEVCAN message '{name}' ID or payload length.");

            var receivers = nodeRx.GetValueOrDefault(storedId) ?? new HashSet<string>(StringComparer.Ordinal);
            var signals = new List<DbcSignal>();
            var signalCount = ReadCount(reader);
            for (var signalIndex = 0; signalIndex < signalCount; signalIndex++)
                signals.Add(ReadSignal(reader, dlc, receivers));
            messages.Add(new DbcMessage(canId, name, dlc, signals, extended));
            // The converter updates Transmitter on reassignment but can retain earlier node TX entries.
            if ((string.IsNullOrWhiteSpace(transmitter) || transmitter == "Vector__XXX")
                && nodeTx.TryGetValue(storedId, out var transmitters))
            {
                if (transmitters.Count != 1)
                    throw new InvalidDataException($"Ambiguous transmitter for BEVCAN message 0x{storedId:X}.");
                transmitter = transmitters.Single();
            }
            assignments.Add(canId, new MessageNodeAssignment(string.IsNullOrWhiteSpace(transmitter) ? null : transmitter, receivers));
        }

        // shortcut: value labels are not modeled; consume them to detect truncated files, add labels when the UI supports them.
        var tableCount = ReadCount(reader);
        for (var index = 0; index < tableCount; index++)
        {
            reader.ReadUInt32();
            reader.ReadString();
            var valueCount = ReadCount(reader);
            for (var valueIndex = 0; valueIndex < valueCount; valueIndex++)
            {
                reader.ReadInt64();
                reader.ReadString();
            }
        }
        if (buffer.Position != buffer.Length)
            throw new InvalidDataException("Unexpected trailing BEVCAN data.");
        return new CandeskXmlDocument(new MessageDatabase(messages, busConfiguration), nodes, assignments);
    }

    private static (int Bitrate, double SamplePoint) ReadTiming(BinaryReader reader)
    {
        var bitrate = reader.ReadInt32();
        var samplePoint = reader.ReadDouble();
        // BEVCAN has no controller clock/BRP, so hardware timing is calculated from bitrate/SP.
        reader.ReadInt32(); // SJW.
        reader.ReadInt32(); // TSEG1.
        reader.ReadInt32(); // TSEG2.
        return (bitrate, samplePoint);
    }

    private static DbcSignal ReadSignal(BinaryReader reader, byte dlc, HashSet<string> receivers)
    {
        var name = reader.ReadString();
        var startBit = reader.ReadInt32();
        var length = reader.ReadInt32();
        var order = reader.ReadByte() switch
        {
            0 => ByteOrder.Motorola,
            1 => ByteOrder.Intel,
            var value => throw new InvalidDataException($"Invalid BEVCAN byte order: {value}.")
        };
        var signed = reader.ReadBoolean();
        var factor = reader.ReadDouble();
        var offset = reader.ReadDouble();
        double? minimum = reader.ReadDouble();
        double? maximum = reader.ReadDouble();
        if (minimum == 0 && maximum == 0) minimum = maximum = null;
        var unit = reader.ReadString();
        var mux = reader.ReadString();
        var role = mux == "M" ? MultiplexorRole.Switch : mux.StartsWith('m') ? MultiplexorRole.Value : MultiplexorRole.None;
        int? muxValue = role == MultiplexorRole.Value ? int.Parse(mux[1..], CultureInfo.InvariantCulture) : null;
        if (mux.Length > 0 && role == MultiplexorRole.None)
            throw new InvalidDataException($"Unsupported BEVCAN multiplexing indicator: {mux}.");
        var receiverCount = ReadCount(reader);
        for (var index = 0; index < receiverCount; index++) receivers.Add(reader.ReadString());
        if (startBit < 0 || startBit >= dlc * 8 || length is < 1 or > 64)
            throw new InvalidDataException($"Invalid BEVCAN signal '{name}' bit position or length.");
        var lastBit = order == ByteOrder.Intel ? startBit + length - 1 : SignalDecoder.MotorolaBit(startBit, length - 1);
        if (lastBit / 8 >= dlc)
            throw new InvalidDataException($"BEVCAN signal '{name}' exceeds frame payload.");
        return new DbcSignal(name, startBit, length, order, signed, factor, offset, minimum, maximum, unit, role, muxValue);
    }

    private static int ReadCount(BinaryReader reader)
    {
        var count = reader.ReadInt32();
        if (count < 0 || count > reader.BaseStream.Length - reader.BaseStream.Position)
            throw new InvalidDataException($"Invalid BEVCAN collection count: {count}.");
        return count;
    }
}
