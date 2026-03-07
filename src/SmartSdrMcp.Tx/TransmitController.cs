using Flex.Smoothlake.FlexLib;
using SmartSdrMcp.Ai;
using SmartSdrMcp.Radio;

namespace SmartSdrMcp.Tx;

public class TransmitController
{
    private readonly RadioManager _radioManager;
    private readonly TransmitSafety _safety;
    private readonly List<ReplyProposal> _pendingProposals = new();
    private readonly object _lock = new();

    public event Action<ReplyProposal>? ProposalQueued;
    public event Action<string>? TransmitStarted;
    public event Action? TransmitCompleted;
    public event Action<string>? TransmitError;

    public TransmitController(RadioManager radioManager)
    {
        _radioManager = radioManager;
        _safety = new TransmitSafety();
    }

    public string ProposeTransmission(ReplyProposal proposal)
    {
        lock (_lock)
        {
            _pendingProposals.Add(proposal);
        }
        ProposalQueued?.Invoke(proposal);
        return proposal.Id;
    }

    public List<ReplyProposal> GetPendingProposals()
    {
        lock (_lock)
        {
            return _pendingProposals.Where(p => !p.Approved && !p.Sent).ToList();
        }
    }

    public (bool Success, string Message) ApproveAndSend(string proposalId)
    {
        ReplyProposal? proposal;
        lock (_lock)
        {
            proposal = _pendingProposals.FirstOrDefault(p => p.Id == proposalId);
        }

        if (proposal == null)
            return (false, $"Proposal {proposalId} not found");

        return SendText(proposal.SuggestedText, proposal.EstimatedWpm);
    }

    public (bool Success, string Message) SendText(string text, int wpm = 20)
    {
        var radio = _radioManager.Radio;
        if (radio == null || !radio.Connected)
            return (false, "Radio not connected");

        var state = _radioManager.GetState();

        // Safety checks
        var freqCheck = _safety.CheckTransmitAllowed(state.FrequencyMHz);
        if (!freqCheck.Allowed)
            return (false, freqCheck.Reason!);

        var lengthCheck = _safety.CheckTextLength(text, wpm);
        if (!lengthCheck.Allowed)
            return (false, lengthCheck.Reason!);

        try
        {
            var cwx = radio.GetCWX();
            cwx.Speed = wpm;
            cwx.Send(text);
            TransmitStarted?.Invoke(text);
            return (true, $"Sending: {text}");
        }
        catch (Exception ex)
        {
            var error = $"TX error: {ex.Message}";
            TransmitError?.Invoke(error);
            return (false, error);
        }
    }

    public (bool Success, string Message) Abort()
    {
        var radio = _radioManager.Radio;
        if (radio == null || !radio.Connected)
            return (false, "Radio not connected");

        try
        {
            var cwx = radio.GetCWX();
            cwx.ClearBuffer();
            radio.Mox = false;
            return (true, "Transmission aborted");
        }
        catch (Exception ex)
        {
            return (false, $"Abort error: {ex.Message}");
        }
    }

    public void ClearProposals()
    {
        lock (_lock) _pendingProposals.Clear();
    }
}
