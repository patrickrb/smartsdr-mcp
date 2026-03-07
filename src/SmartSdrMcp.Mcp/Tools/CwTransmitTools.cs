using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using SmartSdrMcp.Ai;
using SmartSdrMcp.Audio;
using SmartSdrMcp.Cw;
using SmartSdrMcp.Qso;
using SmartSdrMcp.Radio;
using SmartSdrMcp.Tx;

namespace SmartSdrMcp.Mcp.Tools;

[McpServerToolType]
public class CwTransmitTools
{
    private readonly TransmitController _transmitController;
    private readonly ReplyGenerator _replyGenerator;
    private readonly QsoTracker _qsoTracker;
    private readonly RadioManager _radioManager;
    private readonly AudioPipeline _audioPipeline;
    private readonly CwPipeline _cwPipeline;
    private readonly MessageSegmenter _messageSegmenter;

    public CwTransmitTools(
        TransmitController transmitController,
        ReplyGenerator replyGenerator,
        QsoTracker qsoTracker,
        RadioManager radioManager,
        AudioPipeline audioPipeline,
        CwPipeline cwPipeline,
        MessageSegmenter messageSegmenter)
    {
        _transmitController = transmitController;
        _replyGenerator = replyGenerator;
        _qsoTracker = qsoTracker;
        _radioManager = radioManager;
        _audioPipeline = audioPipeline;
        _cwPipeline = cwPipeline;
        _messageSegmenter = messageSegmenter;
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
            EstimatedDurationSeconds = p.EstimatedDuration.TotalSeconds,
            p.Approved,
            p.Sent
        }), new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool, Description("Clear all queued reply proposals.")]
    public string ClearPendingReplies()
    {
        int cleared = _transmitController.ClearProposals();
        return $"Cleared {cleared} pending proposal(s).";
    }

    [McpServerTool, Description("Configure TX guard. When armed=false, all CW transmit is blocked. maxSeconds limits TX length. requireProposal blocks direct custom text and requires proposal approval.")]
    public string SetTxGuard(bool armed, int maxSeconds = 60, bool requireProposal = true)
    {
        if (maxSeconds <= 0)
            return "maxSeconds must be greater than 0.";

        var guard = _transmitController.ConfigureTxGuard(armed, maxSeconds, requireProposal);
        return JsonSerializer.Serialize(guard, new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool, Description("Get current TX guard settings.")]
    public string GetTxGuard()
    {
        var guard = _transmitController.GetTxGuardState();
        return JsonSerializer.Serialize(guard, new JsonSerializerOptions { WriteIndented = true });
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

    [McpServerTool, Description("Run predefined macro workflows: find_cq, answer_cq, close_qso_safely.")]
    public string RunMacro(string name)
    {
        var macro = (name ?? string.Empty).Trim().ToLowerInvariant();
        return macro switch
        {
            "find_cq" => RunFindCq(),
            "answer_cq" => RunAnswerCq(),
            "close_qso_safely" => RunCloseQsoSafely(),
            _ => "Unknown macro. Use find_cq, answer_cq, or close_qso_safely."
        };
    }

    private string RunFindCq()
    {
        if (!_radioManager.IsConnected)
            return "Not connected to a radio.";

        _radioManager.SetMode("CW");

        if (_cwPipeline.IsRunning)
            return "Macro find_cq complete: CW listener already running in CW mode.";

        var state = _radioManager.GetState();
        bool audioStarted = _audioPipeline.Start(daxChannel: 1);
        if (!audioStarted)
            return "Macro find_cq failed: unable to start DAX audio on channel 1.";

        _cwPipeline.Reset();
        _cwPipeline.SetToneFrequency(state.CwPitch);
        _cwPipeline.Start();
        _messageSegmenter.Start(state.FrequencyMHz);

        return "Macro find_cq complete: mode=CW, listener started on DAX channel 1.";
    }

    private string RunAnswerCq()
    {
        var state = _qsoTracker.CurrentState;
        if (state.Stage == QsoStage.Idle)
            return "Macro answer_cq aborted: no active CQ/QSO context.";

        return CwGenerateReply();
    }

    private string RunCloseQsoSafely()
    {
        string target = _qsoTracker.CurrentState.TheirCallsign ?? "OM";
        var proposal = new ReplyProposal(
            SuggestedText: $"TU {target} 73 SK",
            Reason: "Closing sequence macro",
            EstimatedWpm: 20,
            EstimatedDuration: TimeSpan.FromSeconds(4),
            ProposedAt: DateTime.UtcNow,
            RequiresApproval: true);

        _transmitController.ProposeTransmission(proposal);

        return JsonSerializer.Serialize(new
        {
            Macro = "close_qso_safely",
            proposal.Id,
            proposal.SuggestedText,
            Message = "Closing proposal queued. Approve with cw_send_text(proposalId)."
        }, new JsonSerializerOptions { WriteIndented = true });
    }
}
