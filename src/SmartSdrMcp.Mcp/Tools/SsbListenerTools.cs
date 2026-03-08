using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using SmartSdrMcp.Audio;
using SmartSdrMcp.Radio;
using SmartSdrMcp.Ssb;

namespace SmartSdrMcp.Mcp.Tools;

[McpServerToolType]
public class SsbListenerTools
{
    private readonly RadioManager _radioManager;
    private readonly AudioPipeline _audioPipeline;
    private readonly SsbPipeline _ssbPipeline;

    public SsbListenerTools(
        RadioManager radioManager,
        AudioPipeline audioPipeline,
        SsbPipeline ssbPipeline)
    {
        _radioManager = radioManager;
        _audioPipeline = audioPipeline;
        _ssbPipeline = ssbPipeline;
    }

    [McpServerTool, Description("Start SSB speech-to-text listener. Captures DAX audio and transcribes voice using Whisper.")]
    public string SsbListenerStart(int daxChannel = 1)
    {
        if (!_radioManager.IsConnected)
            return "Not connected to a radio. Call connect_radio first.";

        if (_ssbPipeline.IsRunning)
            return "SSB listener is already running.";

        var state = _radioManager.GetState();

        var (audioStarted, audioError) = _audioPipeline.Start(daxChannel);
        if (!audioStarted)
            return audioError ?? "Failed to start audio pipeline. Check DAX channel configuration.";

        var result = _ssbPipeline.Start();
        if (result != "ok")
        {
            _audioPipeline.Stop();
            return result;
        }

        return $"SSB listener started on {state.FrequencyMHz:F6} MHz, DAX channel {daxChannel}. " +
               "Transcribing speech in real-time.";
    }

    [McpServerTool, Description("Stop SSB speech-to-text listener.")]
    public string SsbListenerStop()
    {
        _ssbPipeline.Stop();
        _audioPipeline.Stop();
        return "SSB listener stopped.";
    }

    [McpServerTool, Description("Get the live SSB transcription showing speech being decoded in real-time.")]
    public string SsbGetLiveText()
    {
        if (!_ssbPipeline.IsRunning)
            return "SSB listener is not running.";

        return _ssbPipeline.GetLiveText();
    }

    [McpServerTool, Description("Get the last N transcribed SSB speech segments with timestamps.")]
    public string SsbGetRecentMessages(int count = 10)
    {
        var segments = _ssbPipeline.GetRecentSegments(count);
        if (segments.Count == 0)
            return "No speech transcribed yet.";

        return JsonSerializer.Serialize(segments, new JsonSerializerOptions { WriteIndented = true });
    }
}
