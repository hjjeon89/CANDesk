using System.Collections.ObjectModel;
using CANDesk.Hal;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CANDesk.App.ViewModels;

/// <summary>Maintains the latest frame per CAN ID; unlike Trace this never appends duplicate IDs.</summary>
public sealed partial class MessageMonitorViewModel : ObservableObject
{
    private readonly Dictionary<uint, MessageMonitorRow> _rowsById = [];
    public ObservableCollection<MessageMonitorRow> Rows { get; } = [];
    [ObservableProperty] private string _idFilter = string.Empty;
    [ObservableProperty] private bool _showRx = true;
    [ObservableProperty] private bool _showTx = true;
    [ObservableProperty] private bool _errorsOnly;

    public void ProcessFrames(IReadOnlyList<CanFrame> frames)
    {
        foreach (var frame in frames)
        {
            if (!Matches(frame)) continue;
            if (!_rowsById.TryGetValue(frame.Id, out var row))
            {
                row = new MessageMonitorRow(frame.Id);
                _rowsById.Add(frame.Id, row);
                Rows.Add(row);
            }
            row.Update(frame);
        }
    }

    public void Clear()
    {
        _rowsById.Clear();
        Rows.Clear();
    }

    private bool Matches(in CanFrame frame)
    {
        if (ErrorsOnly && !frame.Flags.HasFlag(CanFrameFlags.ErrorFrame)) return false;
        if (string.IsNullOrWhiteSpace(IdFilter)) return true;
        var text = IdFilter.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
        return uint.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out var id) && frame.Id == id;
    }
}

public sealed partial class MessageMonitorRow(uint canId) : ObservableObject
{
    public string Id { get; } = $"0x{canId:X3}";
    [ObservableProperty] private string _messageName = "Unmapped";
    [ObservableProperty] private string _payload = string.Empty;
    [ObservableProperty] private byte _dlc;
    [ObservableProperty] private long _receiveCount;
    [ObservableProperty] private double? _cycleTimeMs;
    [ObservableProperty] private DateTime _lastReceived;

    public void Update(in CanFrame frame)
    {
        if (LastReceived != default) CycleTimeMs = (frame.SystemTime - LastReceived).TotalMilliseconds;
        LastReceived = frame.SystemTime;
        Dlc = frame.Dlc;
        Payload = string.Join(" ", Convert.ToHexString(frame.PayloadSpan).Chunk(2).Select(hex => new string(hex)));
        ReceiveCount++;
    }
}
