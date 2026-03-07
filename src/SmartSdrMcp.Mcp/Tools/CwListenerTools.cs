using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using SmartSdrMcp.Audio;
using SmartSdrMcp.Cw;
using SmartSdrMcp.Qso;
using SmartSdrMcp.Radio;

namespace SmartSdrMcp.Mcp.Tools;

[McpServerToolType]
public class CwListenerTools
{
    private readonly RadioManager _radioManager;
    private readonly AudioPipeline _audioPipeline;
    private readonly CwPipeline _cwPipeline;
    private readonly MessageSegmenter _messageSegmenter;
    private readonly QsoTracker _qsoTracker;

    public CwListenerTools(
        RadioManager radioManager,
        AudioPipeline audioPipeline,
        CwPipeline cwPipeline,
        MessageSegmenter messageSegmenter,
        QsoTracker qsoTracker)
    {
        _radioManager = radioManager;
        _audioPipeline = audioPipeline;
        _cwPipeline = cwPipeline;
        _messageSegmenter = messageSegmenter;
        _qsoTracker = qsoTracker;
    }

    [McpServerTool, Description("Start continuous CW listening on the current slice. Captures DAX audio, decodes Morse code, and tracks QSO state. Set fixedWpm to lock decoder speed (0 = auto-detect).")]
    public string CwListenerStart(int daxChannel = 1, int fixedWpm = 0)
    {
        if (!_radioManager.IsConnected)
            return "Not connected to a radio. Call connect_radio first.";

        if (_cwPipeline.IsRunning)
            return "CW listener is already running.";

        var state = _radioManager.GetState();

        bool audioStarted = _audioPipeline.Start(daxChannel);
        if (!audioStarted)
            return "Failed to start audio pipeline. Check DAX channel configuration.";

        // Configure CW decoder to match the radio's CW pitch
        // Reset pipeline state (clears stale text buffer from previous sessions)
        _cwPipeline.Reset();
        _cwPipeline.SetToneFrequency(state.CwPitch);
        if (fixedWpm > 0)
            _cwPipeline.SetFixedWpm(fixedWpm);
        _cwPipeline.Start();
        _messageSegmenter.Start(state.FrequencyMHz);

        string wpmMode = fixedWpm > 0 ? $"fixed {fixedWpm}" : $"~{_cwPipeline.EstimatedWpm:F0} (auto)";
        return $"CW listener started on {state.FrequencyMHz:F6} MHz, DAX channel {daxChannel}. " +
               $"CW pitch {state.CwPitch} Hz, decoding at {wpmMode} WPM.";
    }

    [McpServerTool, Description("Stop CW listening and audio capture.")]
    public string CwListenerStop()
    {
        _messageSegmenter.Stop();
        _cwPipeline.Stop();
        _audioPipeline.Stop();
        return "CW listener stopped.";
    }

    [McpServerTool, Description("Get the live CW decode buffer showing text being decoded in real-time.")]
    public string CwGetLiveText()
    {
        if (!_cwPipeline.IsRunning)
            return "CW listener is not running.";

        var text = _cwPipeline.GetLiveText();
        var keys = _cwPipeline.GetKeyEventLog();
        var diag = $"[{_cwPipeline.EstimatedWpm:F0} WPM | Mag:{_cwPipeline.ToneMagnitude:F4} NF:{_cwPipeline.NoiseFloor:F4} Pk:{_cwPipeline.PeakMagnitude:F4} TD:{(_cwPipeline.TonePresent ? "ON" : "off")}]";
        var result = string.IsNullOrWhiteSpace(text) ? $"{diag} (no CW detected)" : $"{diag} {text}";
        if (!string.IsNullOrEmpty(keys))
            result += $"\nKeys: {keys}";
        return result;
    }

    [McpServerTool, Description("Get the last N decoded CW messages with timestamps, callsigns, and CQ detection.")]
    public string CwGetRecentMessages(int count = 10)
    {
        var messages = _qsoTracker.GetRecentMessages(count);
        if (messages.Count == 0)
            return "No messages decoded yet.";

        return JsonSerializer.Serialize(messages, new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool, Description("Get the current QSO state including tracked callsign, stage, and exchange info.")]
    public string CwGetQsoState()
    {
        var state = _qsoTracker.CurrentState;
        return JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool, Description("Reset the current QSO tracking state back to Idle.")]
    public string CwResetQso()
    {
        _qsoTracker.ResetQso();
        _cwPipeline.Reset();
        return "QSO state reset to Idle.";
    }
}
