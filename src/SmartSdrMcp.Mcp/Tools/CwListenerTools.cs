using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;
using SmartSdrMcp.Ai;
using SmartSdrMcp.Audio;
using SmartSdrMcp.Cw;
using SmartSdrMcp.CwNeural;
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
    private readonly CwAiRescorer _aiRescorer;
    private readonly CwStreamingRescorer? _streamingRescorer;
    private readonly NeuralCwDecoder? _neuralDecoder;

    public CwListenerTools(
        RadioManager radioManager,
        AudioPipeline audioPipeline,
        CwPipeline cwPipeline,
        MessageSegmenter messageSegmenter,
        QsoTracker qsoTracker,
        CwAiRescorer aiRescorer,
        CwStreamingRescorer? streamingRescorer = null,
        NeuralCwDecoder? neuralDecoder = null)
    {
        _radioManager = radioManager;
        _audioPipeline = audioPipeline;
        _cwPipeline = cwPipeline;
        _messageSegmenter = messageSegmenter;
        _qsoTracker = qsoTracker;
        _aiRescorer = aiRescorer;
        _streamingRescorer = streamingRescorer;
        _neuralDecoder = neuralDecoder;
    }

    [McpServerTool, Description("Start continuous CW listening on the current slice. Captures DAX audio, decodes Morse code, and tracks QSO state. Set fixedWpm to lock decoder speed (0 = auto-detect).")]
    public string CwListenerStart(int daxChannel = 1, int fixedWpm = 0)
    {
        if (!_radioManager.IsConnected)
            return "Not connected to a radio. Call connect_radio first.";

        if (_cwPipeline.IsRunning)
            return "CW listener is already running.";

        var state = _radioManager.GetState();

        var (audioStarted, audioError) = _audioPipeline.Start(daxChannel);
        if (!audioStarted)
            return audioError ?? "Failed to start audio pipeline. Check DAX channel configuration.";

        _cwPipeline.Reset();
        _cwPipeline.SetToneFrequency(state.CwPitch);
        if (fixedWpm > 0)
            _cwPipeline.SetFixedWpm(fixedWpm);
        _cwPipeline.Start();
        _messageSegmenter.Start(state.FrequencyMHz);

        // Start neural decoder if model is available
        string neuralStatus = "";
        if (_neuralDecoder != null)
        {
            var neuralResult = _neuralDecoder.Start();
            neuralStatus = neuralResult == "ok" ? " Neural CW decoder active." : $" Neural: {neuralResult}";
        }

        string wpmMode = fixedWpm > 0 ? $"fixed {fixedWpm}" : $"~{_cwPipeline.EstimatedWpm:F0} (auto)";
        return $"CW listener started on {state.FrequencyMHz:F6} MHz, DAX channel {daxChannel}. " +
               $"CW pitch {state.CwPitch} Hz, decoding at {wpmMode} WPM.{neuralStatus}";
    }

    [McpServerTool, Description("Stop CW listening and audio capture.")]
    public string CwListenerStop()
    {
        _neuralDecoder?.Stop();
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
        if (_neuralDecoder is { IsRunning: true })
        {
            var neuralText = _neuralDecoder.GetLiveText();
            result += $"\nNeural: {neuralText}";
        }
        if (_streamingRescorer is { IsRunning: true })
        {
            var aiText = _streamingRescorer.GetRescoredText();
            if (!string.IsNullOrWhiteSpace(aiText))
                result += $"\nAI: {aiText}";
        }
        if (!string.IsNullOrEmpty(keys))
            result += $"\nKeys: {keys}";
        return result;
    }

    [McpServerTool, Description("Get low-level CW decoder diagnostics for noise-floor and tone-detection troubleshooting.")]
    public string CwDecodeDiagnostics()
    {
        double snrLikeDb = 20.0 * Math.Log10((_cwPipeline.ToneMagnitude + 1e-9) / (_cwPipeline.NoiseFloor + 1e-9));
        var recent = _qsoTracker.GetRecentMessages(1).LastOrDefault();
        var result = new
        {
            _cwPipeline.IsRunning,
            _cwPipeline.EstimatedWpm,
            _cwPipeline.SignalRms,
            _cwPipeline.ToneMagnitude,
            _cwPipeline.NoiseFloor,
            _cwPipeline.PeakMagnitude,
            _cwPipeline.TonePresent,
            _cwPipeline.GateOpen,
            SnrLikeDb = snrLikeDb,
            LastMessageUtc = recent?.Timestamp,
            LastMessageConfidence = recent?.Confidence,
            _audioPipeline.LastAudioLevelRms,
            _audioPipeline.LastAudioSampleUtc
        };

        return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
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

    [McpServerTool, Description("Start recording incoming DAX audio. Format: wav or raw. Seconds is optional auto-stop duration (0 = manual stop).")]
    public string RecordAudioStart(string format = "wav", int seconds = 30)
    {
        var (success, message) = _audioPipeline.StartRecording(format, seconds);
        return success ? message : $"Failed: {message}";
    }

    [McpServerTool, Description("Stop active DAX audio recording and return file details.")]
    public string RecordAudioStop()
    {
        var (success, message, info) = _audioPipeline.StopRecording();
        if (!success)
            return $"Failed: {message}";

        return JsonSerializer.Serialize(new
        {
            message,
            info!.Path,
            info.Format,
            info.SampleRate,
            info.SampleCount,
            info.DurationSeconds,
            info.StartedUtc,
            info.EndedUtc
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool, Description("AI-rescore the current CW decode buffer. Uses Claude to correct dit/dah confusions and apply ham radio context. Requires ANTHROPIC_API_KEY environment variable.")]
    public async Task<string> CwAiRescore()
    {
        if (!_cwPipeline.IsRunning)
            return "CW listener is not running.";

        var rawText = _cwPipeline.GetLiveText();
        if (string.IsNullOrWhiteSpace(rawText))
            return "No CW text to rescore.";

        var characters = _cwPipeline.GetRecentCharacters();
        var result = await _aiRescorer.RescoreAsync(rawText, characters);

        if (!result.AiApplied)
            return $"AI rescoring failed: {result.Error}\nOriginal: {result.OriginalText}";

        return $"Original:  {result.OriginalText}\nCorrected: {result.CorrectedText}";
    }

    [McpServerTool, Description("Get N-best character alternatives for the current CW decode. Shows ambiguous characters with their alternative interpretations and confidence scores.")]
    public string CwGetAlternatives()
    {
        if (!_cwPipeline.IsRunning)
            return "CW listener is not running.";

        var characters = _cwPipeline.GetRecentCharacters();
        if (characters.Count == 0)
            return "No decoded characters yet.";

        var ambiguous = characters
            .Select((c, i) => new { c, i })
            .Where(x => x.c.Alternatives is { Count: > 0 })
            .Select(x => new
            {
                Position = x.i,
                x.c.Character,
                Pattern = x.c.DitDahPattern,
                x.c.Confidence,
                Alternatives = x.c.Alternatives!.Select(a => new
                {
                    a.Character,
                    a.DitDahPattern,
                    a.Score
                })
            })
            .ToList();

        if (ambiguous.Count == 0)
            return "No ambiguous characters — all decodes were high confidence.";

        var text = _cwPipeline.GetLiveText();
        var result = new { DecodedText = text, AmbiguousCount = ambiguous.Count, Total = characters.Count, Ambiguous = ambiguous };
        return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool, Description("Start streaming AI CW rescoring. Automatically corrects decoded text after each word gap using QSO context, DX cluster spots, and character alternatives. Requires ANTHROPIC_API_KEY.")]
    public string CwStreamingRescoreStart()
    {
        if (_streamingRescorer == null)
            return "Streaming AI rescorer not available. Set ANTHROPIC_API_KEY environment variable.";
        if (!_cwPipeline.IsRunning)
            return "CW listener is not running. Start it first.";
        return _streamingRescorer.Start();
    }

    [McpServerTool, Description("Stop streaming AI CW rescoring and return the accumulated AI-corrected text.")]
    public string CwStreamingRescoreStop()
    {
        if (_streamingRescorer == null)
            return "Streaming AI rescorer not available.";
        var text = _streamingRescorer.GetRescoredText();
        var result = _streamingRescorer.Stop();
        return string.IsNullOrWhiteSpace(text) ? result : $"{result}\nAI-corrected text: {text}";
    }

    [McpServerTool, Description("Get the current AI-corrected CW text from streaming rescoring.")]
    public string CwGetStreamingText()
    {
        if (_streamingRescorer == null)
            return "Streaming AI rescorer not available.";
        if (!_streamingRescorer.IsRunning)
            return "Streaming AI rescorer is not running.";
        var text = _streamingRescorer.GetRescoredText();
        return string.IsNullOrWhiteSpace(text) ? "(no AI-corrected text yet)" : text;
    }

    [McpServerTool, Description("Export current/recent QSO data. Formats: json or adif.")]
    public string QsoExport(string format = "json", int recentCount = 50)
    {
        var state = _qsoTracker.CurrentState;
        var recent = _qsoTracker.GetRecentMessages(recentCount);
        string normalized = (format ?? "json").Trim().ToLowerInvariant();

        if (normalized == "json")
        {
            var payload = new
            {
                ExportedAtUtc = DateTime.UtcNow,
                _qsoTracker.MyCallsign,
                CurrentState = state,
                RecentMessages = recent
            };

            return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        }

        if (normalized == "adif")
            return BuildAdif(state, recent);

        return "Unsupported format. Use 'json' or 'adif'.";
    }

    private string BuildAdif(QsoState state, List<CwMessage> recent)
    {
        var sb = new StringBuilder();
        sb.AppendLine("SmartSDR MCP export");
        sb.AppendLine("<ADIF_VER:5>3.1.0");
        sb.AppendLine("<EOH>");

        var recordTs = state.LastReceived?.Timestamp ?? recent.LastOrDefault()?.Timestamp ?? DateTime.UtcNow;
        string date = recordTs.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        string time = recordTs.ToString("HHmmss", CultureInfo.InvariantCulture);

        AppendAdif(sb, "QSO_DATE", date);
        AppendAdif(sb, "TIME_ON", time);
        AppendAdif(sb, "STATION_CALLSIGN", _qsoTracker.MyCallsign);
        AppendAdif(sb, "CALL", state.TheirCallsign ?? "UNKNOWN");
        AppendAdif(sb, "MODE", "CW");
        AppendAdif(sb, "RST_RCVD", state.TheirRst ?? "599");
        AppendAdif(sb, "NAME", state.TheirName ?? "");
        AppendAdif(sb, "QTH", state.TheirQth ?? "");

        double freq = state.LastReceived?.FrequencyMHz ?? recent.LastOrDefault()?.FrequencyMHz ?? 0;
        if (freq > 0)
            AppendAdif(sb, "FREQ", freq.ToString("F6", CultureInfo.InvariantCulture));

        sb.AppendLine("<EOR>");
        return sb.ToString();
    }

    private static void AppendAdif(StringBuilder sb, string field, string value)
    {
        value ??= string.Empty;
        sb.AppendLine($"<{field}:{value.Length}>{value}");
    }
}
