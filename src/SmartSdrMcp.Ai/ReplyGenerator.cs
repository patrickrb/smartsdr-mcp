using SmartSdrMcp.Qso;

namespace SmartSdrMcp.Ai;

public class ReplyGenerator
{
    private readonly string _myCallsign;
    private readonly string _myName;
    private readonly string _myQth;
    private readonly int _defaultWpm;

    public event Action<ReplyProposal>? ProposalGenerated;

    public ReplyGenerator(string myCallsign, string myName = "PATRICK", string myQth = "", int defaultWpm = 20)
    {
        _myCallsign = myCallsign.ToUpper();
        _myName = myName.ToUpper();
        _myQth = myQth.ToUpper();
        _defaultWpm = defaultWpm;
    }

    public ReplyProposal? GenerateReply(QsoState qsoState, CwMessage? latestMessage)
    {
        if (latestMessage == null) return null;

        string? reply = qsoState.Stage switch
        {
            QsoStage.CqDetected => GenerateCqResponse(qsoState),
            QsoStage.Replied => null, // Waiting for them
            QsoStage.ExchangingReports => GenerateReportExchange(qsoState),
            QsoStage.Conversation => GenerateConversation(qsoState),
            QsoStage.Closing => GenerateClosing(qsoState),
            _ => null
        };

        if (reply == null) return null;

        var proposal = new ReplyProposal(
            SuggestedText: reply,
            Reason: $"QSO stage: {qsoState.Stage}",
            EstimatedWpm: _defaultWpm,
            EstimatedDuration: EstimateDuration(reply, _defaultWpm),
            ProposedAt: DateTime.UtcNow);

        ProposalGenerated?.Invoke(proposal);
        return proposal;
    }

    private string GenerateCqResponse(QsoState state)
    {
        var their = state.TheirCallsign ?? "???";
        return $"{their} DE {_myCallsign} {_myCallsign} K";
    }

    private string GenerateReportExchange(QsoState state)
    {
        var their = state.TheirCallsign ?? "???";
        var parts = new List<string> { $"{their} DE {_myCallsign}" };
        parts.Add("R R UR 599");
        if (!string.IsNullOrEmpty(_myName))
            parts.Add($"NAME {_myName}");
        if (!string.IsNullOrEmpty(_myQth))
            parts.Add($"QTH {_myQth}");
        parts.Add("HW? K");
        return string.Join(" ", parts);
    }

    private string GenerateConversation(QsoState state)
    {
        var their = state.TheirCallsign ?? "???";
        var parts = new List<string> { $"{their} DE {_myCallsign}" };

        // Acknowledge their info
        if (state.TheirName != null)
            parts.Add($"R R TNX {state.TheirName}");
        else
            parts.Add("R R");

        parts.Add("FB");
        parts.Add("K");

        return string.Join(" ", parts);
    }

    private string GenerateClosing(QsoState state)
    {
        var their = state.TheirCallsign ?? "???";
        return $"{their} TU 73 DE {_myCallsign} SK";
    }

    private static TimeSpan EstimateDuration(string text, int wpm)
    {
        // PARIS standard: 50 dit-units per word
        double ditMs = 1200.0 / wpm;
        // Rough estimate: each character averages ~8 dit-units
        double totalDits = text.Length * 8;
        return TimeSpan.FromMilliseconds(totalDits * ditMs);
    }
}
