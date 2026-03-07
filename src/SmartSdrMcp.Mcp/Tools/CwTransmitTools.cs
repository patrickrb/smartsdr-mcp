using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using SmartSdrMcp.Ai;
using SmartSdrMcp.Qso;
using SmartSdrMcp.Tx;

namespace SmartSdrMcp.Mcp.Tools;

[McpServerToolType]
public class CwTransmitTools
{
    private readonly TransmitController _transmitController;
    private readonly ReplyGenerator _replyGenerator;
    private readonly QsoTracker _qsoTracker;

    public CwTransmitTools(
        TransmitController transmitController,
        ReplyGenerator replyGenerator,
        QsoTracker qsoTracker)
    {
        _transmitController = transmitController;
        _replyGenerator = replyGenerator;
        _qsoTracker = qsoTracker;
    }

    [McpServerTool, Description("Generate an AI-suggested CW reply based on current QSO state. Returns a proposal that must be approved before sending.")]
    public string CwGenerateReply()
    {
        var qsoState = _qsoTracker.CurrentState;
        var proposal = _replyGenerator.GenerateReply(qsoState, qsoState.LastReceived);

        if (proposal == null)
            return "No reply suggestion available for current QSO state.";

        _transmitController.ProposeTransmission(proposal);

        return JsonSerializer.Serialize(new
        {
            proposal.Id,
            proposal.SuggestedText,
            proposal.Reason,
            proposal.EstimatedWpm,
            EstimatedDurationSeconds = proposal.EstimatedDuration.TotalSeconds,
            Message = "Proposal queued. Use cw_send_text with this ID to approve and transmit."
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool, Description("Get all pending reply proposals awaiting approval.")]
    public string CwGetPendingReplies()
    {
        var proposals = _transmitController.GetPendingProposals();
        if (proposals.Count == 0)
            return "No pending proposals.";

        return JsonSerializer.Serialize(proposals.Select(p => new
        {
            p.Id,
            p.SuggestedText,
            p.Reason,
            p.EstimatedWpm,
            EstimatedDurationSeconds = p.EstimatedDuration.TotalSeconds
        }), new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool, Description("Send CW text via the radio. Either approve a pending proposal by ID, or send custom text directly. Requires radio connection. IMPORTANT: This transmits on the air.")]
    public string CwSendText(string? proposalId = null, string? customText = null, int wpm = 20)
    {
        if (proposalId != null)
        {
            var (success, message) = _transmitController.ApproveAndSend(proposalId);
            if (success)
                _qsoTracker.NotifySent(message);
            return message;
        }

        if (customText != null)
        {
            var (success, message) = _transmitController.SendText(customText, wpm);
            if (success)
                _qsoTracker.NotifySent(customText);
            return message;
        }

        return "Provide either proposalId or customText.";
    }

    [McpServerTool, Description("Emergency abort: immediately stop any CW transmission in progress.")]
    public string CwAbort()
    {
        var (_, message) = _transmitController.Abort();
        return message;
    }
}
