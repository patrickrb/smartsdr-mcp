namespace SmartSdrMcp.Qso;

public enum QsoStage
{
    Idle,
    CqDetected,
    Replied,
    ExchangingReports,
    Conversation,
    Closing,
    Complete
}

public record QsoState(
    string? TheirCallsign,
    QsoStage Stage,
    CwMessage? LastReceived,
    string? LastSent,
    string? TheirName,
    string? TheirQth,
    string? TheirRst,
    DateTime? StartTime)
{
    public static QsoState Empty => new(null, QsoStage.Idle, null, null, null, null, null, null);
}
