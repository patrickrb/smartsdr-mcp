namespace SmartSdrMcp.Contest;

public enum ContestStage
{
    Stopped,
    Monitoring,
    ReadyToCall,
    CallingStation,
    RepeatCall,
    ExchangingReports,
    Completing,
    QsoComplete
}

public enum PromptUrgency
{
    Info,
    Ready,
    Now,
    Repeat
}

public record ContestState(
    ContestStage Stage,
    string? RunningStation,
    double RunningStationConfidence,
    string? LastHeardText,
    ContestPrompt? PendingPrompt,
    DateTime StageEnteredAt,
    int QsosCompleted,
    string? LastError,
    string? MyCallsign,
    string? ClusterStation,
    List<string> StatusLog);

public record ContestPrompt(
    string Text,
    string Instruction,
    PromptUrgency Urgency,
    DateTime CreatedAt,
    bool Acknowledged);

public record ContestQsoLog(
    string? TheirCallsign,
    string? TheirReport,
    string? TheirExchange,
    string OurReport,
    string OurExchange,
    double FrequencyMHz,
    DateTime StartedUtc,
    DateTime CompletedUtc);

public record SituationAnalysis(
    string Situation,
    string? Callsign,
    double Confidence,
    string? SignalReport,
    string? Exchange,
    bool MentionsUs,
    bool IsPartialCall,
    string? Reasoning);
