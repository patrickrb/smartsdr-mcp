using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using SmartSdrMcp.Audio;
using SmartSdrMcp.Radio;
using SmartSdrMcp.Ssb;

namespace SmartSdrMcp.Contest;

public class ContestAgent
{
    private readonly SsbPipeline _ssbPipeline;
    private readonly AudioPipeline _audioPipeline;
    private readonly RadioManager _radioManager;
    private readonly VoiceTransmitter _voiceTx;
    private readonly ClusterClient _cluster = new();

    private const string AnthropicUrl = "https://api.anthropic.com/v1/messages";
    private const string Model = "claude-haiku-4-5-20251001";
    private const int MaxStatusLog = 50;
    private const double ClusterPollIntervalSec = 30;

    // Direct audio settings
    private const int AudioSampleRate = 24000;
    private const int AudioBufferSeconds = 3;
    private const int AudioBufferSize = AudioSampleRate * AudioBufferSeconds;

    private static readonly HttpClient AnthropicClient = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly object _lock = new();
    private string _myCallsign = "";
    private string _myName = "";
    private string _myQth = "";

    private ContestStage _stage = ContestStage.Stopped;
    private string? _runningStation;
    private double _runningStationConfidence;
    private string? _lastHeardText;
    private ContestPrompt? _pendingPrompt;
    private DateTime _stageEnteredAt;
    private string? _lastError;
    private DateTime _lastProcessedTimestamp = DateTime.MinValue;
    private readonly List<string> _statusLog = new();
    private bool _autoMode;
    private bool _useDirectAudio = false; // Audio input not yet supported by Anthropic API

    private readonly List<ContestQsoLog> _qsoLog = new();
    private DateTime _qsoStartedUtc;

    // Direct audio buffering
    private readonly object _audioLock = new();
    private readonly List<float> _rawAudioBuffer = new();

    // Cluster spot tracking
    private string? _clusterStation;
    private DateTime _lastClusterPoll = DateTime.MinValue;

    private Thread? _agentThread;
    private volatile bool _running;

    public bool IsRunning => _running;

    public ContestAgent(SsbPipeline ssbPipeline, RadioManager radioManager, AudioPipeline audioPipeline)
    {
        _ssbPipeline = ssbPipeline;
        _radioManager = radioManager;
        _audioPipeline = audioPipeline;
        _voiceTx = new VoiceTransmitter(radioManager);
    }

    public string Start(string myCallsign, string? myName = null, string? myQth = null, bool autoMode = false)
    {
        if (_running) return "Contest agent is already running.";

        var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
            return "ANTHROPIC_API_KEY environment variable is not set. " +
                   "Get an API key at https://console.anthropic.com/settings/keys and set it: " +
                   "export ANTHROPIC_API_KEY=sk-ant-...";

        if (string.IsNullOrWhiteSpace(myCallsign))
            return "Callsign is required. Usage: contest_agent_start(callsign=\"K1AF\")";

        _myCallsign = myCallsign.ToUpperInvariant();
        _myName = myName ?? "";
        _myQth = myQth ?? "";
        _autoMode = autoMode;

        _running = true;
        if (_useDirectAudio)
        {
            lock (_audioLock) { _rawAudioBuffer.Clear(); }
            _audioPipeline.AudioDataAvailable += OnAudioData;
        }

        lock (_lock)
        {
            _stage = ContestStage.Monitoring;
            _stageEnteredAt = DateTime.UtcNow;
            _runningStation = null;
            _runningStationConfidence = 0;
            _lastHeardText = null;
            _pendingPrompt = null;
            _lastError = null;
            _lastProcessedTimestamp = DateTime.UtcNow;
            _statusLog.Clear();
            var audioLabel = _useDirectAudio ? "direct audio" : "Whisper";
            var modeLabel = _autoMode ? "AUTO mode" : "manual mode";
            LogStatus($"Agent started for {_myCallsign} ({modeLabel}, {audioLabel}). Monitoring frequency...");
        }

        _agentThread = new Thread(AgentLoop)
        {
            IsBackground = true,
            Name = "ContestAgent"
        };
        _agentThread.Start();

        return $"Contest agent started for {_myCallsign}. Monitoring for openings...";
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _audioPipeline.AudioDataAvailable -= OnAudioData;
        _agentThread?.Join(5000);
        _agentThread = null;

        lock (_lock)
        {
            _stage = ContestStage.Stopped;
            _pendingPrompt = null;
            LogStatus("Agent stopped.");
        }
    }

    private void OnAudioData(float[] samples)
    {
        if (!_running || !_useDirectAudio) return;
        lock (_audioLock)
        {
            _rawAudioBuffer.AddRange(samples);
        }
    }

    public ContestState GetState()
    {
        lock (_lock)
        {
            return new ContestState(
                _stage,
                _runningStation,
                _runningStationConfidence,
                _lastHeardText,
                _pendingPrompt,
                _stageEnteredAt,
                _qsoLog.Count,
                _lastError,
                _myCallsign,
                _clusterStation,
                _statusLog.ToList());
        }
    }

    public void Acknowledge()
    {
        lock (_lock)
        {
            if (_pendingPrompt != null)
                _pendingPrompt = _pendingPrompt with { Acknowledged = true };

            switch (_stage)
            {
                case ContestStage.ReadyToCall:
                    LogStatus($"Operator calling {_runningStation}. Waiting for response...");
                    TransitionTo(ContestStage.CallingStation,
                        MakePrompt($"Calling {_runningStation}... waiting for response.", "Wait for them to respond.", PromptUrgency.Info));
                    break;
                case ContestStage.Completing:
                    LogQso();
                    LogStatus($"QSO with {_runningStation} logged. Back to monitoring.");
                    TransitionTo(ContestStage.Monitoring,
                        MakePrompt("QSO logged. Listening...", "QSO complete. Listening for the next opening.", PromptUrgency.Info));
                    break;
            }
        }
    }

    public void Skip()
    {
        lock (_lock)
        {
            LogStatus($"Skipped {_runningStation ?? "opportunity"}. Back to monitoring.");
            TransitionTo(ContestStage.Monitoring,
                MakePrompt("Skipped. Listening...", "Skipped current opportunity. Listening for the next opening.", PromptUrgency.Info));
            _runningStation = null;
            _runningStationConfidence = 0;
        }
    }

    public List<ContestQsoLog> GetLog() => _qsoLog.ToList();

    private void AgentLoop()
    {
        while (_running)
        {
            Thread.Sleep(2000);
            if (!_running) break;

            try
            {
                ProcessCycle();
            }
            catch (Exception ex)
            {
                lock (_lock)
                {
                    _lastError = ex.Message;
                    LogStatus($"Error: {ex.Message}");
                }
                Console.Error.WriteLine($"[CONTEST] Error: {ex.Message}");
            }
        }
    }

    private void ProcessCycle()
    {
        // Check timeouts first
        lock (_lock) { CheckTimeouts(); }
        if (!_running) return;

        // Poll DX cluster periodically
        PollCluster();

        SituationAnalysis? analysis = null;
        string? displayText = null;

        if (_useDirectAudio)
        {
            // Direct audio mode: grab buffered audio, encode as WAV, send to Claude
            float[]? audioSamples = null;
            int bufferedCount;
            lock (_audioLock)
            {
                bufferedCount = _rawAudioBuffer.Count;
                if (bufferedCount >= AudioBufferSize)
                {
                    audioSamples = _rawAudioBuffer.ToArray();
                    _rawAudioBuffer.Clear();
                }
            }

            if (audioSamples == null)
            {
                // Log buffer status once (first cycle only)
                if (bufferedCount == 0 && _statusLog.Count < 5)
                {
                    lock (_lock) { LogStatus($"Waiting for audio... buffer: {bufferedCount}/{AudioBufferSize}"); }
                }
                return;
            }

            lock (_lock) { LogStatus($"Audio buffer ready: {audioSamples.Length} samples, sending to Claude..."); }

            analysis = AnalyzeSituationFromAudioAsync(audioSamples).GetAwaiter().GetResult();
            if (analysis != null)
            {
                displayText = analysis.Reasoning ?? "(audio analysis)";
                lock (_lock) { _lastHeardText = displayText; }
            }
        }
        else
        {
            // Text fallback: use SsbPipeline (Whisper)
            var segments = _ssbPipeline.GetRecentSegments(20);
            var newSegments = segments
                .Where(s => s.Timestamp > _lastProcessedTimestamp)
                .ToList();

            if (newSegments.Count == 0) return;

            _lastProcessedTimestamp = newSegments.Max(s => s.Timestamp);
            displayText = string.Join(" ", newSegments.Select(s => s.Text));

            lock (_lock) { _lastHeardText = displayText; }

            analysis = AnalyzeSituationAsync(displayText).GetAwaiter().GetResult();
        }

        if (analysis == null) return;

        lock (_lock)
        {
            var callInfo = !string.IsNullOrEmpty(analysis.Callsign) ? $" [{analysis.Callsign}]" : "";
            LogStatus($"Heard: \"{Truncate(displayText ?? "", 80)}\" → {analysis.Situation}{callInfo} ({analysis.Confidence:P0})");

            // If Claude couldn't identify the running station but cluster has one, inject it
            if (string.IsNullOrEmpty(analysis.Callsign) && _clusterStation != null
                && _stage == ContestStage.Monitoring
                && analysis.Situation is "qso_ending" or "closing" or "ready_for_callers" or "unknown")
            {
                analysis = analysis with
                {
                    Callsign = _clusterStation,
                    Situation = analysis.Situation == "unknown" ? "ready_for_callers" : analysis.Situation,
                    Confidence = Math.Max(analysis.Confidence, 0.7)
                };
                LogStatus($"Cluster boost: using spotted station {_clusterStation} on this frequency.");
            }

            ProcessAnalysis(analysis);
        }
    }

    private void PollCluster()
    {
        if ((DateTime.UtcNow - _lastClusterPoll).TotalSeconds < ClusterPollIntervalSec)
            return;

        _lastClusterPoll = DateTime.UtcNow;

        try
        {
            var state = _radioManager.GetState();
            if (state.FrequencyMHz <= 0) return;

            var spots = _cluster.GetSpotsNearFrequencyAsync(state.FrequencyMHz).GetAwaiter().GetResult();
            if (spots.Count > 0)
            {
                var best = spots[0]; // Most recent
                var newStation = best.DxCall.ToUpperInvariant();

                if (newStation != _clusterStation)
                {
                    _clusterStation = newStation;
                    lock (_lock)
                    {
                        LogStatus($"DX Cluster: {_clusterStation} spotted on {best.FrequencyKhz:F1} kHz by {best.Spotter}" +
                                  (best.Country != null ? $" ({best.Country})" : ""));
                    }
                }
            }
            else
            {
                _clusterStation = null;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[CONTEST] Cluster poll error: {ex.Message}");
        }
    }

    private void CheckTimeouts()
    {
        var elapsed = DateTime.UtcNow - _stageEnteredAt;

        switch (_stage)
        {
            case ContestStage.ReadyToCall when elapsed > TimeSpan.FromSeconds(10):
                LogStatus("Missed window — operator didn't ack in time.");
                TransitionTo(ContestStage.Monitoring,
                    MakePrompt("Missed window.", "Timed out waiting for acknowledgment. Listening for next opening.", PromptUrgency.Info));
                _runningStation = null;
                break;

            case ContestStage.CallingStation when elapsed > TimeSpan.FromSeconds(15):
                LogStatus($"{_runningStation} didn't respond. Back to monitoring.");
                TransitionTo(ContestStage.Monitoring,
                    MakePrompt("No response.", "Station didn't respond. Listening for next opening.", PromptUrgency.Info));
                _runningStation = null;
                break;

            case ContestStage.RepeatCall when elapsed > TimeSpan.FromSeconds(10):
                LogStatus($"{_runningStation} didn't respond after repeat. Back to monitoring.");
                TransitionTo(ContestStage.Monitoring,
                    MakePrompt("No response after repeat.", "Station didn't respond to repeat call. Listening.", PromptUrgency.Info));
                _runningStation = null;
                break;

            case ContestStage.ExchangingReports when elapsed > TimeSpan.FromSeconds(20):
                LogStatus("Exchange timed out. Wrapping up QSO.");
                TransitionTo(ContestStage.Completing,
                    MakePrompt("Say: 73, thanks", $"Exchange timed out. Say '73' to {_runningStation} and move on.", PromptUrgency.Now));
                break;

            case ContestStage.Completing when elapsed > TimeSpan.FromSeconds(5):
                LogQso();
                LogStatus("QSO auto-completed. Back to monitoring.");
                TransitionTo(ContestStage.Monitoring,
                    MakePrompt("QSO logged. Listening...", "QSO auto-completed. Listening for next opening.", PromptUrgency.Info));
                break;
        }
    }

    private void ProcessAnalysis(SituationAnalysis analysis)
    {
        if (analysis.Confidence < 0.4) return;

        switch (_stage)
        {
            case ContestStage.Monitoring:
                var isOpenForCallers = analysis.Situation is "cq" or "qso_ending" or "closing" or "ready_for_callers";
                if (isOpenForCallers && analysis.Confidence > 0.6 && !string.IsNullOrEmpty(analysis.Callsign))
                {
                    if (!IsValidCallsign(analysis.Callsign))
                    {
                        LogStatus($"Partial callsign: {analysis.Callsign} — waiting for full callsign.");
                        break;
                    }

                    if (analysis.Callsign.Equals(_myCallsign, StringComparison.OrdinalIgnoreCase))
                    {
                        LogStatus($"Heard our own callsign {_myCallsign} — ignoring.");
                        break;
                    }

                    _runningStation = analysis.Callsign;
                    _runningStationConfidence = analysis.Confidence;
                    _qsoStartedUtc = DateTime.UtcNow;

                    var reason = analysis.Situation == "cq"
                        ? $"Identified running station: {analysis.Callsign} (calling CQ)"
                        : $"Identified running station: {analysis.Callsign} (QSO just ended — window open)";
                    LogStatus(reason);

                    if (_autoMode)
                    {
                        LogStatus($"AUTO: Calling {analysis.Callsign} — '{_myCallsign}'");
                        TransitionTo(ContestStage.CallingStation,
                            MakePrompt($"AUTO CALLING: '{_myCallsign}'",
                                $"{reason}. Auto-calling now.",
                                PromptUrgency.Now));
                        // Fire TTS transmit in background
                        _ = Task.Run(async () =>
                        {
                            var callText = CallsignToPhonetic(_myCallsign);
                            var result = await _voiceTx.SpeakAsync(callText);
                            lock (_lock)
                            {
                                if (result.Success)
                                    LogStatus($"TX complete: {callText}");
                                else
                                    LogStatus($"TX failed: {result.Message}");
                            }
                        });
                    }
                    else
                    {
                        TransitionTo(ContestStage.ReadyToCall,
                            MakePrompt($"NOW: Call '{_myCallsign} {_myCallsign}'",
                                $"{reason}. Confidence {analysis.Confidence:P0}. Call them now!",
                                PromptUrgency.Now));
                    }
                }
                break;

            case ContestStage.CallingStation:
            case ContestStage.RepeatCall:
                if (analysis.MentionsUs && !analysis.IsPartialCall)
                {
                    LogStatus($"{_runningStation} responded to us! Exchange time.");
                    var exchangeText = !string.IsNullOrEmpty(_myQth)
                        ? $"You're 59, {_myQth}"
                        : "You're 59";
                    TransitionTo(ContestStage.ExchangingReports,
                        MakePrompt($"Say: {exchangeText}",
                            $"{_runningStation} responded! Give them your report and exchange.",
                            PromptUrgency.Now));
                }
                else if (analysis.MentionsUs && analysis.IsPartialCall)
                {
                    LogStatus($"{_runningStation} heard partial callsign. Repeating.");
                    TransitionTo(ContestStage.RepeatCall,
                        MakePrompt($"Say again: {_myCallsign} {_myCallsign}",
                            $"They heard a partial callsign. Repeat: '{_myCallsign} {_myCallsign}'",
                            PromptUrgency.Repeat));
                }
                else if (analysis.Situation == "responding_to_us")
                {
                    LogStatus($"{_runningStation} is responding to us.");
                    var exchangeText = !string.IsNullOrEmpty(_myQth)
                        ? $"You're 59, {_myQth}"
                        : "You're 59";
                    TransitionTo(ContestStage.ExchangingReports,
                        MakePrompt($"Say: {exchangeText}",
                            $"{_runningStation} is responding to us. Give report and exchange.",
                            PromptUrgency.Now));
                }
                break;

            case ContestStage.ExchangingReports:
                if (analysis.Situation == "exchange" || analysis.Situation == "closing"
                    || (analysis.SignalReport != null && analysis.Exchange != null))
                {
                    var reportInfo = analysis.SignalReport != null ? $" Report: {analysis.SignalReport}" : "";
                    var exchangeInfo = analysis.Exchange != null ? $" Exchange: {analysis.Exchange}" : "";
                    LogStatus($"Got their exchange.{reportInfo}{exchangeInfo} Wrapping up.");
                    TransitionTo(ContestStage.Completing,
                        MakePrompt("Say: 73, thanks",
                            $"Got their exchange. Say '73, thanks' to complete the QSO.",
                            PromptUrgency.Now));
                }
                break;
        }
    }

    private string BuildSystemPrompt(bool isAudio)
    {
        var clusterHint = _clusterStation != null
            ? $"\n\nDX CLUSTER HINT: The station {_clusterStation} has been spotted on this frequency. " +
              "If you hear activity but can't identify the callsign, this is likely the running station.\n"
            : "";

        var inputDescription = isAudio
            ? "You are listening to live HF SSB radio audio. The audio is narrowband (300-3000 Hz), " +
              "may contain noise, QRM, and fading. Stations use NATO phonetics to spell callsigns. " +
              "Listen carefully for callsigns, signal reports, and contest exchanges."
            : "You analyze transcribed radio speech to understand what's happening on frequency.\n\n" +
              "CRITICAL — CALLSIGN RECONSTRUCTION:\n" +
              "The input is from speech-to-text which BADLY mangles NATO phonetics. You MUST reconstruct callsigns:\n" +
              "- Map phonetics to letters: Alpha=A, Bravo=B, Charlie=C, Delta=D, Echo=E, Foxtrot=F, Golf=G, " +
              "Hotel=H, India=I, Juliett=J, Kilo=K, Lima=L, Mike=M, November=N, Oscar=O, Papa=P, " +
              "Quebec=Q, Romeo=R, Sierra=S, Tango=T, Uniform=U, Victor=V, Whiskey=W, Xray=X, Yankee=Y, Zulu=Z.\n" +
              "- Callsigns are 1-2 letter prefix + 1 digit + 1-3 letter suffix (e.g. TI1K, K1AF, W2WCM, VE3ABC)\n" +
              "- If you can't reconstruct a valid callsign, set callsign to null rather than guessing wrong";

        return
            $"You are an AI assistant helping a ham radio operator ({_myCallsign}) in an SSB voice contest. " +
            $"{inputDescription}\n\n" +
            "IMPORTANT CONTEST RHYTHM: In contests, stations rarely say 'CQ' between QSOs. " +
            "The running station works one caller after another rapidly. The pattern is:\n" +
            "1. Running station gives report/exchange to current caller\n" +
            "2. Current caller says '73' or 'thanks' or 'QSL'\n" +
            "3. Running station says their callsign (or 'QRZ') — THIS is the window to call\n" +
            "4. New callers immediately throw out their callsign\n" +
            "So when you hear a QSO wrapping up (59 thanks, 73, QSL) followed by a callsign, " +
            "that callsign is the RUNNING STATION and the frequency is open for callers.\n\n" +
            "Respond with ONLY a JSON object (no markdown, no explanation):\n" +
            "{\n" +
            "  \"situation\": \"cq\" | \"qso_ending\" | \"ready_for_callers\" | \"qso_in_progress\" | \"responding_to_us\" | \"partial_call\" | \"exchange\" | \"closing\" | \"noise\" | \"unknown\",\n" +
            "  \"callsign\": \"<the RUNNING STATION's callsign if identified>\",\n" +
            "  \"confidence\": <0.0 to 1.0>,\n" +
            "  \"signal_report\": \"<if mentioned, e.g. '59'>\",\n" +
            "  \"exchange\": \"<contest exchange info if heard>\",\n" +
            $"  \"mentions_us\": <true if they said or referenced '{_myCallsign}'>,\n" +
            $"  \"is_partial_call\": <true if they only got part of our callsign>,\n" +
            "  \"reasoning\": \"<brief transcription/explanation of what you heard>\"\n" +
            "}\n\n" +
            "Situation values:\n" +
            "- \"cq\": Station explicitly calling CQ contest\n" +
            "- \"qso_ending\": QSO wrapping up — you hear '59 thanks', '73', 'QSL', 'good luck' etc.\n" +
            "- \"ready_for_callers\": Running station said their callsign or 'QRZ' after finishing a QSO — window is OPEN\n" +
            "- \"qso_in_progress\": Two stations mid-exchange (not our turn)\n" +
            $"- \"responding_to_us\": They said '{_myCallsign}' or a close match\n" +
            "- \"partial_call\": They repeated back only part of our callsign\n" +
            "- \"exchange\": Signal report and contest exchange being given\n" +
            "- \"closing\": Final acknowledgment of a QSO\n" +
            "- \"noise\": Unintelligible or no meaningful content\n" +
            "- \"unknown\": Can't determine what's happening\n\n" +
            "The callsign field should always be the RUNNING STATION (the station working the pileup), not the caller." +
            clusterHint;
    }

    private async Task<SituationAnalysis?> AnalyzeSituationFromAudioAsync(float[] samples)
    {
        try
        {
            var wavBytes = EncodeWav(samples, AudioSampleRate);
            var wavBase64 = Convert.ToBase64String(wavBytes);

            var systemPrompt = BuildSystemPrompt(isAudio: true);

            // Build request with audio content block
            var requestObj = new Dictionary<string, object>
            {
                ["model"] = Model,
                ["max_tokens"] = 300,
                ["system"] = systemPrompt,
                ["messages"] = new[]
                {
                    new Dictionary<string, object>
                    {
                        ["role"] = "user",
                        ["content"] = new object[]
                        {
                            new Dictionary<string, object>
                            {
                                ["type"] = "input_audio",
                                ["source"] = new Dictionary<string, object>
                                {
                                    ["type"] = "base64",
                                    ["media_type"] = "audio/wav",
                                    ["data"] = wavBase64
                                }
                            },
                            new Dictionary<string, object>
                            {
                                ["type"] = "text",
                                ["text"] = "Listen to this HF SSB radio audio clip and analyze the contest situation."
                            }
                        }
                    }
                }
            };

            return await SendAnthropicRequestAsync(requestObj);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[CONTEST] Direct audio analysis failed: {ex.Message}");
            // Fall back to text mode
            lock (_lock) { LogStatus($"Direct audio failed ({ex.Message}), falling back to Whisper text mode."); }
            _useDirectAudio = false;
            return null;
        }
    }

    private async Task<SituationAnalysis?> AnalyzeSituationAsync(string text)
    {
        try
        {
            var systemPrompt = BuildSystemPrompt(isAudio: false);

            var requestObj = new Dictionary<string, object>
            {
                ["model"] = Model,
                ["max_tokens"] = 300,
                ["system"] = systemPrompt,
                ["messages"] = new[]
                {
                    new Dictionary<string, object>
                    {
                        ["role"] = "user",
                        ["content"] = $"Transcribed radio speech: \"{text}\""
                    }
                }
            };

            return await SendAnthropicRequestAsync(requestObj);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[CONTEST] Claude analysis failed: {ex.Message}");
            return null;
        }
    }

    private int _audioFailCount;

    private async Task<SituationAnalysis?> SendAnthropicRequestAsync(Dictionary<string, object> requestObj)
    {
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, AnthropicUrl);
        httpRequest.Headers.Add("x-api-key", Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? "");
        httpRequest.Headers.Add("anthropic-version", "2023-06-01");
        httpRequest.Content = JsonContent.Create(requestObj);
        var response = await AnthropicClient.SendAsync(httpRequest);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            Console.Error.WriteLine($"[CONTEST] Anthropic API error {response.StatusCode}: {body}");
            lock (_lock) { LogStatus($"API error {(int)response.StatusCode}: {Truncate(body, 120)}"); }

            // Fall back to text mode after 3 consecutive audio failures
            if (_useDirectAudio && ++_audioFailCount >= 3)
            {
                _useDirectAudio = false;
                lock (_lock) { LogStatus("Falling back to Whisper text mode after repeated audio API errors."); }
            }
            return null;
        }

        _audioFailCount = 0;

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var content = doc.RootElement
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString()?.Trim();

        if (string.IsNullOrWhiteSpace(content)) return null;

        // Strip markdown code fences if present
        content = Regex.Replace(content, @"^```(?:json)?\s*", "", RegexOptions.Multiline);
        content = Regex.Replace(content, @"\s*```\s*$", "", RegexOptions.Multiline);
        content = content.Trim();

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        };
        return JsonSerializer.Deserialize<SituationAnalysis>(content, options);
    }

    private static byte[] EncodeWav(float[] samples, int sampleRate)
    {
        // Encode as 16-bit mono PCM WAV
        int numSamples = samples.Length;
        int dataSize = numSamples * 2; // 16-bit = 2 bytes per sample
        int fileSize = 44 + dataSize;  // 44-byte header + data

        using var ms = new MemoryStream(fileSize);
        using var bw = new BinaryWriter(ms);

        // RIFF header
        bw.Write("RIFF"u8);
        bw.Write(fileSize - 8);
        bw.Write("WAVE"u8);

        // fmt chunk
        bw.Write("fmt "u8);
        bw.Write(16);              // chunk size
        bw.Write((short)1);        // PCM format
        bw.Write((short)1);        // mono
        bw.Write(sampleRate);      // sample rate
        bw.Write(sampleRate * 2);  // byte rate (sampleRate * channels * bitsPerSample/8)
        bw.Write((short)2);        // block align (channels * bitsPerSample/8)
        bw.Write((short)16);       // bits per sample

        // data chunk
        bw.Write("data"u8);
        bw.Write(dataSize);

        for (int i = 0; i < numSamples; i++)
        {
            float s = Math.Clamp(samples[i], -1f, 1f);
            bw.Write((short)(s * 32767));
        }

        return ms.ToArray();
    }

    private void TransitionTo(ContestStage newStage, ContestPrompt? prompt)
    {
        _stage = newStage;
        _stageEnteredAt = DateTime.UtcNow;
        _pendingPrompt = prompt;

        var stationInfo = _runningStation != null ? $" ({_runningStation})" : "";
        Console.Error.WriteLine($"[CONTEST] {newStage}{stationInfo}: {prompt?.Text}");
    }

    private void LogQso()
    {
        var state = _radioManager.GetState();
        _qsoLog.Add(new ContestQsoLog(
            TheirCallsign: _runningStation,
            TheirReport: null,
            TheirExchange: null,
            OurReport: "59",
            OurExchange: _myQth,
            FrequencyMHz: state.FrequencyMHz,
            StartedUtc: _qsoStartedUtc,
            CompletedUtc: DateTime.UtcNow));

        _runningStation = null;
        _runningStationConfidence = 0;
    }

    private void LogStatus(string message)
    {
        var entry = $"[{DateTime.UtcNow:HH:mm:ss}] {message}";
        _statusLog.Add(entry);
        if (_statusLog.Count > MaxStatusLog)
            _statusLog.RemoveAt(0);
        Console.Error.WriteLine($"[CONTEST] {message}");
    }

    private static ContestPrompt MakePrompt(string text, string instruction, PromptUrgency urgency) =>
        new(text, instruction, urgency, DateTime.UtcNow, false);

    private static string Truncate(string text, int maxLen) =>
        text.Length <= maxLen ? text : text[..maxLen] + "...";

    // Validates callsign format: 1-2 char prefix (letters/digits), 1 digit, 1-4 letter suffix
    // Handles standard (K1AF, VE3ABC) and digit-prefix calls (P40L, J62K, 4X4FF, 9A1A)
    private static readonly Regex CallsignPattern = new(
        @"^[A-Z\d]{1,2}\d[A-Z]{1,4}$", RegexOptions.Compiled);

    private static bool IsValidCallsign(string callsign) =>
        CallsignPattern.IsMatch(callsign.ToUpperInvariant());

    private static readonly Dictionary<char, string> NatoAlphabet = new()
    {
        ['A'] = "Alpha", ['B'] = "Bravo", ['C'] = "Charlie", ['D'] = "Delta",
        ['E'] = "Echo", ['F'] = "Foxtrot", ['G'] = "Golf", ['H'] = "Hotel",
        ['I'] = "India", ['J'] = "Juliett", ['K'] = "Kilo", ['L'] = "Lima",
        ['M'] = "Mike", ['N'] = "November", ['O'] = "Oscar", ['P'] = "Papa",
        ['Q'] = "Quebec", ['R'] = "Romeo", ['S'] = "Sierra", ['T'] = "Tango",
        ['U'] = "Uniform", ['V'] = "Victor", ['W'] = "Whiskey", ['X'] = "X-ray",
        ['Y'] = "Yankee", ['Z'] = "Zulu",
        ['0'] = "Zero", ['1'] = "One", ['2'] = "Two", ['3'] = "Three",
        ['4'] = "Four", ['5'] = "Five", ['6'] = "Six", ['7'] = "Seven",
        ['8'] = "Eight", ['9'] = "Niner"
    };

    private static string CallsignToPhonetic(string callsign) =>
        string.Join(" ", callsign.ToUpperInvariant().Select(c =>
            NatoAlphabet.TryGetValue(c, out var word) ? word : c.ToString()));
}
