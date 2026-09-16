using CANDesk.Hal;

namespace CANDesk.Core.Logging;

public sealed record CanTraceFrame(DateTime Timestamp, int Channel, string Direction, CanFrame Frame);
