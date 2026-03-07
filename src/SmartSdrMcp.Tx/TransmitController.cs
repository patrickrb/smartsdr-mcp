using Flex.Smoothlake.FlexLib;
using SmartSdrMcp.Ai;
using SmartSdrMcp.Radio;

namespace SmartSdrMcp.Tx;

public record TxGuardState(bool Armed, int MaxSeconds, bool RequireProposal);

public class TransmitController
{
    private readonly RadioManager _radioManager;
    private readonly TransmitSafety _safety;
    private readonly List<ReplyProposal> _pendingProposals = new();
    private readonly object _lock = new();

    private bool _txArmed;
    private bool _requireProposal = true;

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
            return _pendingProposals.Where(p => !p.Sent).ToList();
        }
    }

    public TxGuardState GetTxGuardState()
    {
        lock (_lock)
        {
            return new TxGuardState(
                Armed: _txArmed,
                MaxSeconds: (int)_safety.MaxTransmitDuration.TotalSeconds,
                RequireProposal: _requireProposal);
        }
    }

    public TxGuardState ConfigureTxGuard(bool armed, int maxSeconds, bool requireProposal)
    {
        lock (_lock)
        {
            _txArmed = armed;
            _requireProposal = requireProposal;
            if (maxSeconds > 0)
                _safety.MaxTransmitDuration = TimeSpan.FromSeconds(maxSeconds);

            return new TxGuardState(
                Armed: _txArmed,
                MaxSeconds: (int)_safety.MaxTransmitDuration.TotalSeconds,
                RequireProposal: _requireProposal);
        }
    }

    public (bool Success, string Message) ApproveAndSend(string proposalId)
    {
        ReplyProposal? proposal;
        lock (_lock)
        {
            proposal = _pendingProposals.FirstOrDefault(p => p.Id == proposalId);
            if (proposal != null)
                proposal.Approved = true;
        }

        if (proposal == null)
            return (false, $"Proposal {proposalId} not found");

        var result = SendTextInternal(proposal.SuggestedText, proposal.EstimatedWpm, fromProposal: true);
        if (result.Success)
        {
            lock (_lock)
            {
                proposal.Sent = true;
            }
        }

        return result;
    }

    public (bool Success, string Message) SendText(string text, int wpm = 20)
    {
        return SendTextInternal(text, wpm, fromProposal: false);
    }

    private (bool Success, string Message) SendTextInternal(string text, int wpm, bool fromProposal)
    {
        var guard = GetTxGuardState();
        if (!guard.Armed)
            return (false, "TX guard blocked transmit: set armed=true via set_tx_guard first.");

        if (guard.RequireProposal && !fromProposal)
            return (false, "TX guard blocked direct text: requireProposal=true, use a proposal ID.");

        var radio = _radioManager.Radio;
        if (radio == null || !radio.Connected)
            return (false, "Radio not connected");

        var state = _radioManager.GetState();

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

    public int ClearProposals()
    {
        lock (_lock)
        {
            int count = _pendingProposals.Count;
            _pendingProposals.Clear();
            return count;
        }
    }
}
