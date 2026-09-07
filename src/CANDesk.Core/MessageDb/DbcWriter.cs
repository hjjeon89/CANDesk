using System.Globalization;
using System.Text;

namespace CANDesk.Core.MessageDb;

/// <summary>Writes the DBC subset <see cref="DbcParser"/> reads (<c>VERSION</c>/<c>BU_</c>/<c>BO_</c>/
/// <c>SG_</c>), so a CANDesk message database can be re-exported to <c>.dbc</c> — both to round-trip
/// through this app and to open in standard DBC-aware tools (Vector CANdb++, CANoe, python-can, etc).
/// Value tables (<c>VAL_</c>), comments (<c>CM_</c>), and attribute definitions (<c>BA_</c>) aren't
/// modeled by CANDesk yet and are out of scope.</summary>
public sealed class DbcWriter : IMessageDatabaseWriter
{
    private const string NoNode = "Vector__XXX";

    public MessageDbFormat Format => MessageDbFormat.Dbc;

    /// <summary>Writes messages/signals only, satisfying the format-agnostic
    /// <see cref="IMessageDatabaseWriter"/> contract — every message's transmitter/receivers are
    /// written as the DBC placeholder node <c>Vector__XXX</c> since node/TX-RX metadata isn't part
    /// of <see cref="IMessageDatabase"/>. Call the other overload directly to preserve real node
    /// assignments (e.g. from an <see cref="EditableMessageDatabase"/> session).</summary>
    public Task WriteAsync(IMessageDatabase database, Stream output, CancellationToken cancellationToken = default) =>
        WriteAsync([], database, static _ => new MessageNodeAssignment(null, new HashSet<string>(StringComparer.Ordinal)), output, cancellationToken);

    /// <summary>Writes messages, signals, the node (<c>BU_</c>) list, and each message's
    /// transmitter/receiver nodes, matching how Vector DBC represents them.</summary>
    public async Task WriteAsync(IReadOnlyCollection<CanNode> nodes, IMessageDatabase database,
        Func<uint, MessageNodeAssignment> getAssignment, Stream output, CancellationToken cancellationToken = default)
    {
        // DBC is a plain-text format; tools in the wild expect CRLF line endings regardless of OS.
        await using var writer = new StreamWriter(output, Encoding.ASCII, leaveOpen: true) { NewLine = "\r\n" };

        await writer.WriteLineAsync("VERSION \"\"").ConfigureAwait(false);
        await writer.WriteLineAsync().ConfigureAwait(false);
        await writer.WriteLineAsync("NS_ :").ConfigureAwait(false);
        await writer.WriteLineAsync().ConfigureAwait(false);
        await writer.WriteLineAsync("BS_:").ConfigureAwait(false);
        await writer.WriteLineAsync().ConfigureAwait(false);
        await writer.WriteLineAsync($"BU_: {string.Join(' ', nodes.Select(node => node.Name))}").ConfigureAwait(false);
        await writer.WriteLineAsync().ConfigureAwait(false);

        foreach (var message in database.Messages.OrderBy(message => message.CanId))
        {
            var assignment = getAssignment(message.CanId);
            var rawId = message.IsExtended ? message.CanId | 0x8000_0000u : message.CanId;
            var transmitter = string.IsNullOrWhiteSpace(assignment.TransmitterNode) ? NoNode : assignment.TransmitterNode;
            var receivers = assignment.ReceiverNodes.Count == 0
                ? NoNode
                : string.Join(',', assignment.ReceiverNodes.OrderBy(name => name, StringComparer.Ordinal));

            await writer.WriteLineAsync($"BO_ {rawId} {message.Name}: {message.Dlc} {transmitter}").ConfigureAwait(false);
            foreach (var signal in message.Signals)
            {
                await writer.WriteLineAsync(FormatSignal(signal, receivers)).ConfigureAwait(false);
            }
            await writer.WriteLineAsync().ConfigureAwait(false);
        }

        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string FormatSignal(DbcSignal signal, string receivers)
    {
        var mux = signal.MultiplexorRole switch
        {
            MultiplexorRole.Switch => " M",
            MultiplexorRole.Value => $" m{signal.MultiplexValue}",
            _ => string.Empty
        };
        var byteOrder = signal.ByteOrder == ByteOrder.Intel ? '1' : '0';
        var sign = signal.IsSigned ? '-' : '+';
        var factor = signal.Factor.ToString(CultureInfo.InvariantCulture);
        var offset = signal.Offset.ToString(CultureInfo.InvariantCulture);
        var min = (signal.Minimum ?? 0).ToString(CultureInfo.InvariantCulture);
        var max = (signal.Maximum ?? 0).ToString(CultureInfo.InvariantCulture);
        return $" SG_ {signal.Name}{mux} : {signal.StartBit}|{signal.BitLength}@{byteOrder}{sign} ({factor},{offset}) [{min}|{max}] \"{signal.Unit}\" {receivers}";
    }
}
