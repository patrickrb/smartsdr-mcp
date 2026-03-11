using Microsoft.AspNetCore.SignalR;
using SmartSdrMcp.Audio;
using SmartSdrMcp.BandScout;
using SmartSdrMcp.Contest;
using SmartSdrMcp.CqCaller;
using SmartSdrMcp.Cw;
using SmartSdrMcp.DxHunter;
using SmartSdrMcp.Ai;
using SmartSdrMcp.CwNeural;
using SmartSdrMcp.Radio;
using SmartSdrMcp.Ssb;
using SmartSdrMcp.Tx;

namespace SmartSdrMcp.Web;

public class DashboardBroadcaster : BackgroundService
{
    private readonly IHubContext<DashboardHub> _hub;
    private readonly CqCallerAgent _agent;
    private readonly RadioManager _radioManager;
    private readonly CwPipeline _cwPipeline;
    private readonly SsbPipeline _ssbPipeline;
    private readonly AudioPipeline _audioPipeline;
    private readonly DxHunterAgent _dxHunter;
    private readonly DxClusterService _dxCluster;
    private readonly BandScoutMonitor _bandScout;
    private readonly ContestAgent _contest;
    private readonly TransmitController _txController;
    private readonly CwStreamingRescorer? _streamingRescorer;
    private readonly NeuralCwDecoder? _neuralDecoder;
    private readonly SpeakerTracker _speakerTracker;

    public DashboardBroadcaster(
        IHubContext<DashboardHub> hub,
        CqCallerAgent agent,
        RadioManager radioManager,
        CwPipeline cwPipeline,
        SsbPipeline ssbPipeline,
        AudioPipeline audioPipeline,
        DxHunterAgent dxHunter,
        DxClusterService dxCluster,
        BandScoutMonitor bandScout,
        ContestAgent contest,
        TransmitController txController,
        CwStreamingRescorer? streamingRescorer = null,
        NeuralCwDecoder? neuralDecoder = null)
    {
        _hub = hub;
        _agent = agent;
        _radioManager = radioManager;
        _cwPipeline = cwPipeline;
        _ssbPipeline = ssbPipeline;
        _audioPipeline = audioPipeline;
        _dxHunter = dxHunter;
        _dxCluster = dxCluster;
        _bandScout = bandScout;
        _contest = contest;
        _txController = txController;
        _streamingRescorer = streamingRescorer;
        _neuralDecoder = neuralDecoder;
        _speakerTracker = new SpeakerTracker(myCallsign: "K1AF");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var snapshot = BuildSnapshot();
                await _hub.Clients.All.SendAsync("dashboardUpdate", snapshot, stoppingToken);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[DASHBOARD] Broadcast error: {ex.Message}");
            }

            await Task.Delay(1000, stoppingToken);
        }
    }

    private DashboardSnapshot BuildSnapshot()
    {
        var snapshot = new DashboardSnapshot { TimestampUtc = DateTime.UtcNow };

        // Agent
        var agentStatus = _agent.GetStatus();
        snapshot.Agent = new AgentSnapshot
        {
            Stage = agentStatus.Stage.ToString(),
            IsRunning = agentStatus.IsRunning,
            MyCallsign = agentStatus.MyCallsign,
            QsosCompleted = agentStatus.QsosCompleted,
            CqsSent = agentStatus.CqsSent,
            CurrentCaller = agentStatus.CurrentCaller,
            PartialCallsign = agentStatus.PartialCallsign,
            PileupAttempt = agentStatus.PileupAttempt,
            LastDecodedText = agentStatus.LastDecodedText,
            LastSentText = agentStatus.LastSentText,
            LastError = agentStatus.LastError,
            Mode = agentStatus.Mode.ToString(),
            LicenseClass = agentStatus.LicenseClass?.ToString(),
            StatusLog = agentStatus.StatusLog
        };

        // Radio
        var radioState = _radioManager.GetState();
        var meters = _radioManager.GetMeters();
        var meterDoubles = new Dictionary<string, double>();
        foreach (var kvp in meters)
        {
            if (kvp.Value is double d) meterDoubles[kvp.Key] = d;
            else if (kvp.Value is float f) meterDoubles[kvp.Key] = f;
            else if (kvp.Value is int i) meterDoubles[kvp.Key] = i;
            else if (double.TryParse(kvp.Value?.ToString(), out var parsed)) meterDoubles[kvp.Key] = parsed;
        }

        var rfPowerObj = _radioManager.GetRfPower();
        int rfPower = 0;
        if (rfPowerObj != null)
        {
            var prop = rfPowerObj.GetType().GetProperty("RFPower");
            if (prop != null) rfPower = (int)(prop.GetValue(rfPowerObj) ?? 0);
        }

        snapshot.Radio = new RadioSnapshot
        {
            RadioName = radioState.RadioName,
            RadioModel = radioState.RadioModel,
            Connected = radioState.Connected,
            FrequencyMHz = radioState.FrequencyMHz,
            Mode = radioState.Mode,
            IsTransmitting = radioState.IsTransmitting,
            CwPitch = radioState.CwPitch,
            Meters = meterDoubles,
            RfPower = rfPower
        };

        // CW
        var cwChars = _cwPipeline.GetRecentCharacters(30);
        snapshot.Cw = new CwSnapshot
        {
            Running = _cwPipeline.IsRunning,
            LiveText = _cwPipeline.IsRunning ? _cwPipeline.GetLiveText() : "",
            AiText = _streamingRescorer is { IsRunning: true } ? _streamingRescorer.GetRescoredText() : null,
            AiRunning = _streamingRescorer?.IsRunning ?? false,
            NeuralText = _neuralDecoder is { IsRunning: true } ? _neuralDecoder.GetLiveText() : null,
            NeuralRunning = _neuralDecoder?.IsRunning ?? false,
            NeuralCallsigns = _neuralDecoder is { IsRunning: true }
                ? _neuralDecoder.GetLatestParse()?.DetectedCallsigns.ToArray()
                : null,
            NeuralMessageType = _neuralDecoder is { IsRunning: true }
                ? _neuralDecoder.GetLatestParse()?.MessageType.ToString()
                : null,
            Wpm = _cwPipeline.EstimatedWpm,
            SignalRms = _cwPipeline.SignalRms,
            ToneMagnitude = _cwPipeline.ToneMagnitude,
            NoiseFloor = _cwPipeline.NoiseFloor,
            TonePresent = _cwPipeline.TonePresent,
            GateOpen = _cwPipeline.GateOpen,
            RecentCharacters = cwChars.Select(c => new CwCharEntry
            {
                Character = c.Character,
                Confidence = c.Confidence,
                Alternatives = c.Alternatives?.Select(a => a.Character).ToList() ?? []
            }).ToList()
        };

        // SSB
        var ssbSegments = _ssbPipeline.GetRecentSegments(30);
        var speakerLines = _speakerTracker.ProcessNewSegments(ssbSegments);
        // Also label any segments we haven't tracked yet
        var labeledSegments = ssbSegments.Select(s => new SsbSegmentEntry
        {
            Timestamp = s.Timestamp,
            Text = s.Text,
            Speaker = _speakerTracker.LabelSegment(s)
        }).ToList();

        snapshot.Ssb = new SsbSnapshot
        {
            Running = _ssbPipeline.IsRunning,
            LiveText = _ssbPipeline.IsRunning ? _ssbPipeline.GetLiveText() : "",
            RecentSegments = labeledSegments
        };

        // Audio
        snapshot.Audio = new AudioSnapshot
        {
            Running = _audioPipeline.IsRunning,
            LevelRms = _audioPipeline.LastAudioLevelRms,
            DaxChannel = _audioPipeline.CurrentDaxChannel
        };

        // Agents running state
        snapshot.Agents = new AgentsRunningSnapshot
        {
            DxHunter = _dxHunter.IsRunning,
            DxCluster = _dxCluster.IsRunning,
            BandScout = _bandScout.IsRunning,
            Contest = _contest.IsRunning
        };
        snapshot.TxGuardArmed = _txController.GetTxGuardState().Armed;

        // QSOs
        snapshot.Qsos = _agent.GetLog().Select(q => new QsoEntry
        {
            TheirCallsign = q.TheirCallsign,
            OurRst = q.OurRst,
            TheirRst = q.TheirRst,
            TheirName = q.TheirName,
            TheirQth = q.TheirQth,
            FrequencyMHz = q.FrequencyMHz,
            Band = q.Band,
            Mode = q.Mode,
            StartedUtc = q.StartedUtc,
            CompletedUtc = q.CompletedUtc
        }).ToList();

        return snapshot;
    }
}
