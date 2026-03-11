using Microsoft.AspNetCore.SignalR;
using SmartSdrMcp.Audio;
using SmartSdrMcp.BandScout;
using SmartSdrMcp.Contest;
using SmartSdrMcp.CqCaller;
using SmartSdrMcp.Cw;
using SmartSdrMcp.DxHunter;
using SmartSdrMcp.Radio;
using SmartSdrMcp.Ssb;
using SmartSdrMcp.Ai;
using SmartSdrMcp.CwNeural;
using SmartSdrMcp.Tx;

namespace SmartSdrMcp.Web;

public class DashboardHub : Hub
{
    private readonly CqCallerAgent _agent;
    private readonly RadioManager _radioManager;
    private readonly TransmitController _txController;
    private readonly CwPipeline _cwPipeline;
    private readonly SsbPipeline _ssbPipeline;
    private readonly AudioPipeline _audioPipeline;
    private readonly DxHunterAgent _dxHunter;
    private readonly DxClusterService _dxCluster;
    private readonly BandScoutMonitor _bandScout;
    private readonly ContestAgent _contest;
    private readonly CwStreamingRescorer? _streamingRescorer;
    private readonly NeuralCwDecoder? _neuralDecoder;

    public DashboardHub(
        CqCallerAgent agent,
        RadioManager radioManager,
        TransmitController txController,
        CwPipeline cwPipeline,
        SsbPipeline ssbPipeline,
        AudioPipeline audioPipeline,
        DxHunterAgent dxHunter,
        DxClusterService dxCluster,
        BandScoutMonitor bandScout,
        ContestAgent contest,
        CwStreamingRescorer? streamingRescorer = null,
        NeuralCwDecoder? neuralDecoder = null)
    {
        _agent = agent;
        _radioManager = radioManager;
        _txController = txController;
        _cwPipeline = cwPipeline;
        _ssbPipeline = ssbPipeline;
        _audioPipeline = audioPipeline;
        _dxHunter = dxHunter;
        _dxCluster = dxCluster;
        _bandScout = bandScout;
        _contest = contest;
        _streamingRescorer = streamingRescorer;
        _neuralDecoder = neuralDecoder;
    }

    // --- Radio ---

    public string ConnectRadio()
    {
        if (_radioManager.IsConnected)
            return "Already connected.";
        _radioManager.Initialize();
        Thread.Sleep(2000);
        var connected = _radioManager.Connect();
        if (!connected)
            return "No radio found. Make sure SmartSDR is running.";
        Thread.Sleep(1000);
        return "Connected.";
    }

    public string ArmTxGuard()
    {
        var state = _txController.GetTxGuardState();
        if (state.Armed)
            return "TX guard already armed.";
        _txController.ConfigureTxGuard(armed: true, maxSeconds: 60, requireProposal: false);
        return "TX guard armed.";
    }

    // --- CW Listener ---

    public string StartCwListener()
    {
        if (_cwPipeline.IsRunning)
            return "CW listener already running.";
        if (!EnsureRadioAndAudio(out var err))
            return err!;
        _cwPipeline.Start();
        _neuralDecoder?.Start();
        return "CW listener started.";
    }

    public string StopCwListener()
    {
        if (!_cwPipeline.IsRunning)
            return "CW listener is not running.";
        _neuralDecoder?.Stop();
        _cwPipeline.Stop();
        return "CW listener stopped.";
    }

    // --- SSB Listener ---

    public string StartSsbListener()
    {
        if (_ssbPipeline.IsRunning)
            return "SSB listener already running.";
        if (!EnsureRadioAndAudio(out var err))
            return err!;
        return _ssbPipeline.Start();
    }

    public string StopSsbListener()
    {
        if (!_ssbPipeline.IsRunning)
            return "SSB listener is not running.";
        _ssbPipeline.Stop();
        return "SSB listener stopped.";
    }

    // --- CQ Caller Agent ---

    public string PauseAgent()
    {
        if (!_agent.IsRunning)
            return "Agent is not running.";
        _agent.Stop();
        return "Agent paused.";
    }

    public string ResumeAgent(string callsign, string name, string qth, string mode = "voice", int wpm = 20)
    {
        if (_agent.IsRunning)
            return "Agent is already running.";

        if (!_radioManager.IsConnected)
        {
            var connectResult = ConnectRadio();
            if (!_radioManager.IsConnected)
                return $"Cannot start: {connectResult}";
        }

        var guard = _txController.GetTxGuardState();
        if (!guard.Armed)
            _txController.ConfigureTxGuard(armed: true, maxSeconds: 60, requireProposal: false);

        return _agent.Start(callsign, name, qth, mode, wpm);
    }

    // --- DX Hunter ---

    public string StartDxHunter(string? band = null, string? mode = null)
    {
        if (!EnsureRadio(out var err))
            return err!;
        return _dxHunter.Start(band, mode);
    }

    public string StopDxHunter()
    {
        _dxHunter.Stop();
        return "DX Hunter stopped.";
    }

    // --- DX Cluster ---

    public string StartDxCluster()
    {
        if (!EnsureRadio(out var err))
            return err!;
        return _dxCluster.Start();
    }

    public string StopDxCluster()
    {
        return _dxCluster.Stop();
    }

    // --- Band Scout ---

    public string StartBandScout()
    {
        if (!EnsureRadio(out var err))
            return err!;
        return _bandScout.Start();
    }

    public string StopBandScout()
    {
        _bandScout.Stop();
        return "Band Scout stopped.";
    }

    // --- Contest Agent ---

    public string StartContest(string callsign, string? name = null, string? qth = null)
    {
        if (!EnsureRadio(out var err))
            return err!;
        return _contest.Start(callsign, name, qth);
    }

    public string StopContest()
    {
        _contest.Stop();
        return "Contest agent stopped.";
    }

    // --- Intercept ---

    public void ClearIntercept()
    {
        _cwPipeline.ClearLiveText();
        _ssbPipeline.ClearLiveText();
        _streamingRescorer?.Reset();
        _neuralDecoder?.ClearText();
    }

    public string ToggleAiRescore()
    {
        if (_streamingRescorer == null)
            return "AI rescorer not available. Set ANTHROPIC_API_KEY.";
        if (_streamingRescorer.IsRunning)
            return _streamingRescorer.Stop();
        if (!_cwPipeline.IsRunning)
            return "CW listener not running.";
        return _streamingRescorer.Start();
    }

    // --- Helpers ---

    private bool EnsureRadio(out string? error)
    {
        if (_radioManager.IsConnected) { error = null; return true; }
        var result = ConnectRadio();
        if (!_radioManager.IsConnected) { error = result; return false; }
        error = null;
        return true;
    }

    private bool EnsureRadioAndAudio(out string? error)
    {
        if (!EnsureRadio(out error))
            return false;
        if (!_audioPipeline.IsRunning)
        {
            var (ok, audioErr) = _audioPipeline.Start();
            if (!ok) { error = audioErr ?? "Failed to start audio pipeline."; return false; }
        }
        error = null;
        return true;
    }
}
