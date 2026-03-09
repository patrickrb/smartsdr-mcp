using SmartSdrMcp.Tx;

namespace SmartSdrMcp.CqCaller;

public enum CqCallerStage
{
    Idle,
    CallingCq,
    Listening,
    NoCaller,
    SingleCaller,
    SendingExchange,
    ReceivingExchange,
    Confirming,
    Pileup,
    SendingPartial,
    ListeningForPartial
}

public enum CqCallerMode { Voice, Cw }

public record CqCallerState(
    CqCallerStage Stage,
    bool IsRunning,
    string MyCallsign,
    int QsosCompleted,
    int CqsSent,
    string? CurrentCaller,
    string? PartialCallsign,
    int PileupAttempt,
    string? LastDecodedText,
    string? LastSentText,
    string? LastError,
    CqCallerMode Mode,
    LicenseClass? LicenseClass,
    List<string> StatusLog);

public record CqCallerQso(
    string TheirCallsign,
    string OurRst,
    string TheirRst,
    string? TheirName,
    string? TheirQth,
    double FrequencyMHz,
    string Band,
    string Mode,
    DateTime StartedUtc,
    DateTime CompletedUtc);
