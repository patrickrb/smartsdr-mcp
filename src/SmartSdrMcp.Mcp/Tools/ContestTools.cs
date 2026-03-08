using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using SmartSdrMcp.Audio;
using SmartSdrMcp.Contest;
using SmartSdrMcp.Radio;
using SmartSdrMcp.Ssb;

namespace SmartSdrMcp.Mcp.Tools;

[McpServerToolType]
public class ContestTools
{
    private readonly ContestAgent _contestAgent;
    private readonly RadioManager _radioManager;
    private readonly AudioPipeline _audioPipeline;
    private readonly SsbPipeline _ssbPipeline;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public ContestTools(
        ContestAgent contestAgent,
        RadioManager radioManager,
        AudioPipeline audioPipeline,
        SsbPipeline ssbPipeline)
    {
        _contestAgent = contestAgent;
        _radioManager = radioManager;
        _audioPipeline = audioPipeline;
        _ssbPipeline = ssbPipeline;
    }

    [McpServerTool, Description("Set the Anthropic API key for the contest agent's Claude Haiku analysis. Get one at https://console.anthropic.com/settings/keys")]
    public string ContestSetApiKey(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return "API key cannot be empty.";

        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", apiKey);
        return "Anthropic API key set.";
    }

    [McpServerTool, Description("Start the SSB contest agent. Requires your callsign and ANTHROPIC_API_KEY (set via contest_set_api_key). Monitors frequency, identifies running stations, and coaches operator through QSOs. Set autoMode=true to auto-call when a station is identified (skips manual ack).")]
    public string ContestAgentStart(string callsign, string? name = null, string? qth = null, bool autoMode = false, int daxChannel = 1)
    {
        if (string.IsNullOrWhiteSpace(callsign))
            return "Your callsign is required. Example: contest_agent_start(callsign=\"K1AF\")";

        if (!_radioManager.IsConnected)
            return "Not connected to a radio. Call connect_radio first.";

        // Start SSB listener if not already running
        if (!_ssbPipeline.IsRunning)
        {
            bool audioStarted = _audioPipeline.Start(daxChannel);
            if (!audioStarted)
                return "Failed to start audio pipeline. Check DAX channel configuration.";

            var ssbResult = _ssbPipeline.Start();
            if (ssbResult != "ok")
            {
                _audioPipeline.Stop();
                return $"Failed to start SSB listener: {ssbResult}";
            }
        }

        return _contestAgent.Start(callsign, name, qth, autoMode);
    }

    [McpServerTool, Description("Stop the SSB contest agent.")]
    public string ContestAgentStop()
    {
        _contestAgent.Stop();
        return "Contest agent stopped.";
    }

    [McpServerTool, Description("Get current contest agent status: stage, prompt, running station, QSO count. Poll every 2-3s for live updates.")]
    public string ContestAgentStatus()
    {
        if (!_contestAgent.IsRunning)
            return "Contest agent is not running.";

        var state = _contestAgent.GetState();
        return JsonSerializer.Serialize(state, JsonOptions);
    }

    [McpServerTool, Description("Acknowledge the current prompt — operator has spoken. Advances state: ReadyToCall→CallingStation, Completing→QsoComplete.")]
    public string ContestAgentAck()
    {
        if (!_contestAgent.IsRunning)
            return "Contest agent is not running.";

        _contestAgent.Acknowledge();
        var state = _contestAgent.GetState();
        return JsonSerializer.Serialize(state, JsonOptions);
    }

    [McpServerTool, Description("Skip the current opportunity and return to Monitoring.")]
    public string ContestAgentSkip()
    {
        if (!_contestAgent.IsRunning)
            return "Contest agent is not running.";

        _contestAgent.Skip();
        return "Skipped. Back to monitoring.";
    }

    [McpServerTool, Description("Get the list of completed contest QSOs.")]
    public string ContestAgentLog()
    {
        var log = _contestAgent.GetLog();
        if (log.Count == 0)
            return "No QSOs logged yet.";

        return JsonSerializer.Serialize(log, JsonOptions);
    }

    [McpServerTool, Description("Test voice TX by sending a text phrase via TTS through DAX TX, or a pure 1kHz tone if text='tone'. Automatically sets RF power to 0 watts for safety and restores it after.")]
    public async Task<string> ContestVoiceTest(string text = "Kilo One Alpha Foxtrot")
    {
        var radio = _radioManager.Radio;
        if (radio == null || !radio.Connected)
            return "Radio not connected.";

        // Safety: set RF power to 0 watts, restore after
        int savedPower = radio.RFPower;
        radio.RFPower = 0;

        try
        {
            var tx = new VoiceTransmitter(_radioManager);
            var (success, message) = text.Equals("tone", StringComparison.OrdinalIgnoreCase)
                ? await tx.SendToneAsync()
                : await tx.SpeakAsync(text);
            return message;
        }
        finally
        {
            radio.RFPower = savedPower;
        }
    }
}
