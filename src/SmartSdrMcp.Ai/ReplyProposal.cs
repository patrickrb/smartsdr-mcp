namespace SmartSdrMcp.Ai;

public record ReplyProposal(
    string SuggestedText,
    string Reason,
    int EstimatedWpm,
    TimeSpan EstimatedDuration,
    DateTime ProposedAt,
    bool RequiresApproval = true)
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public bool Approved { get; set; }
    public bool Sent { get; set; }
}
