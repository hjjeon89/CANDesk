using CANDesk.Hal;

namespace CANDesk.Core.Scheduling;

public sealed class TriggeredTxRule : ITriggerCondition
{
    private readonly byte[] _payloadPattern;

    public TriggeredTxRule(
        uint triggerId,
        uint triggerMask,
        ReadOnlySpan<byte> triggerPayloadPattern,
        CanFrame responseFrame,
        TimeSpan responseDelay,
        int? repeatCount = null)
    {
        if (repeatCount is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(repeatCount), "Repeat count must be positive when specified.");
        }

        TriggerId = triggerId;
        TriggerMask = triggerMask;
        _payloadPattern = triggerPayloadPattern.ToArray();
        ResponseFrame = responseFrame;
        ResponseDelay = responseDelay < TimeSpan.Zero ? throw new ArgumentOutOfRangeException(nameof(responseDelay)) : responseDelay;
        RepeatCount = repeatCount;
    }

    public uint TriggerId { get; }
    public uint TriggerMask { get; }
    public ReadOnlySpan<byte> TriggerPayloadPattern => _payloadPattern;
    public CanFrame ResponseFrame { get; }
    public TimeSpan ResponseDelay { get; }
    public int? RepeatCount { get; }

    public bool IsSatisfied(in CanFrame receivedFrame)
    {
        if ((receivedFrame.Id & TriggerMask) != (TriggerId & TriggerMask))
        {
            return false;
        }

        var payload = receivedFrame.PayloadSpan;
        return _payloadPattern.Length <= payload.Length && payload.StartsWith(_payloadPattern);
    }
}
