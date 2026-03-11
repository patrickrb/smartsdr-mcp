using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SmartSdrMcp.Ai;
using SmartSdrMcp.Audio;
using SmartSdrMcp.BandScout;
using SmartSdrMcp.Contest;
using SmartSdrMcp.Cw;
using SmartSdrMcp.Qso;
using SmartSdrMcp.Radio;
using SmartSdrMcp.Ssb;
using SmartSdrMcp.Tx;

namespace SmartSdrMcp.CqCaller;

public class CqCallerAgent
{
    private readonly RadioManager _radioManager;
    private readonly CwPipeline _cwPipeline;
    private readonly AudioPipeline _audioPipeline;
    private readonly TransmitController _txController;
    private readonly SsbPipeline _ssbPipeline;
    private readonly CwAiRescorer? _aiRescorer;
    private readonly SsbAiCallsignExtractor? _aiExtractor;
    private readonly FccLicenseLookup _fccLookup = new();
    private readonly FrequencyScanner _scanner = new();
    private VoiceTransmitter? _voiceTx;
    private readonly object _lock = new();
    private readonly List<string> _statusLog = new();
    private readonly List<CqCallerQso> _qsoLog = new();

    private const int MaxStatusLog = 50;
    private const int MaxPileupAttempts = 3;
    private const int CycleIntervalMs = 500;

    // Timeouts (milliseconds)
    private const int ListeningTimeoutMs = 5_000;
    private const int VoiceListeningTimeoutMs = 10_000;
    private const int NoCallerTimeoutMs = 3_000;
    private const int ReceivingExchangeTimeoutMs = 15_000;
    private const int VoiceReceivingExchangeTimeoutMs = 20_000;
    private const int ListeningForPartialTimeoutMs = 4_000;

    // CW message templates
    private const string CwCqTemplate = "CQ AI ON THE AIR CQ AI ON THE AIR CALLING ANY STATION ANY WHERE AI ON THE AIR DE {0} {0} K";
    private const string CwExchangeTemplate = "{0} UR 599 599 NAME {1} {1} QTH {2} {2} K";
    private const string CwConfirmTemplate = "R {0} TU 73 DE {1} CQ AI ON THE AIR DE {1} {1} K";
    private const string CwQrzTemplate = "QRZ DE {0} K";
    private const string CwPartialTemplate = "{0}?";

    // Voice message templates
    private const string VoiceCqTemplate =
        "CQ AI on the Air. CQ AI On the air. CQ AI on the air. " +
        "This is {0} calling CQ For AI On the air.";
    private const string VoiceExchangeTemplate =
        "{0}, you are five nine. My name is {1}, QTH {2}. " +
        "Please speak slowly and clearly for the AI. QSL?";
    private const string VoiceConfirmTemplate =
        "Roger {0}, thanks for the QSO, 73. CQ CQ this is {1}";
    private const string VoiceQrzTemplate = "QRZ, this is {0}, standing by";
    private const string VoicePartialTemplate =
        "Station with {0}, please call again slowly and clearly";

    // Context-aware response templates for common questions
    private const string VoiceLegalResponse =
        "{0}, great question. Patrick Burns, {1}, is the licensed control operator. " +
        "He is sitting at the operating position and ready to take over at any time. " +
        "This is fully compliant with FCC part 97 rules. 73.";
    private const string VoiceAboutMeResponse =
        "{0}, Patrick is a dad with two young daughters, a senior software engineer at Microsoft, " +
        "and an amateur extra class operator. He got his license in 2020. " +
        "He loves programming, hacking, cyber security, and technology. 73.";
    private const string VoiceAboutTechResponse =
        "{0}, this AI agent runs on a Windows server using a Model Context Protocol server, or MCP, " +
        "that allows Claude Code, an AI assistant by Anthropic, to operate the FlexRadio. " +
        "It uses text to speech for transmit, and Whisper speech to text for receive. " +
        "Pretty cool, right? 73.";
    private const string VoiceWeakSignalResponse =
        "{0}, you are not quite readable, you are down in the noise. " +
        "Please try again slowly and clearly, or increase power if you can.";
    private const string VoiceUnintelligibleResponse =
        "Station calling, I could not copy you. " +
        "Please call again slowly and clearly. This is {0}, AI on the air.";
    private const string VoicePartialCallsignResponse =
        "{0} station, please say your full callsign again slowly. This is {1}.";

    private Thread? _agentThread;
    private volatile bool _running;
    private CqCallerStage _stage = CqCallerStage.Idle;
    private DateTime _stageEnteredAt;
    private DateTime _startedUtc;

    private string _myCallsign = "";
    private string _myName = "";
    private string _myQth = "";
    private int _wpm = 20;
    private CqCallerMode _mode = CqCallerMode.Voice;
    private LicenseClass? _licenseClass;

    private string? _currentCaller;
    private string? _partialCallsign;
    private int _pileupAttempt;
    private string? _lastDecodedText;
    private string? _lastSentText;
    private string? _lastError;
    private int _qsosCompleted;
    private int _cqsSent;
    private DateTime _qsoStartedUtc;

    // Retry state for voice callsign extraction
    private int _repeatAttempts;
    private string _accumulatedTranscriptions = "";
    private const int MaxRepeatAttempts = 3;

    // Exchange parsing
    private string? _theirRst;
    private string? _theirName;
    private string? _theirQth;

    public bool IsRunning => _running;

    // Callsign validation regex
    private static readonly Regex CallsignPattern = new(
        @"^[A-Z\d]{1,2}\d[A-Z]{1,4}$", RegexOptions.Compiled);

    // NATO phonetic alphabet
    private static readonly Dictionary<char, string> NatoAlphabet = new()
    {
        ['A'] = "Alpha", ['B'] = "Bravo", ['C'] = "Charlie", ['D'] = "Delta",
        ['E'] = "Echo", ['F'] = "Foxtrot", ['G'] = "Golf", ['H'] = "Hotel",
        ['I'] = "India", ['J'] = "Juliett", ['K'] = "Kilo", ['L'] = "Lima",
        ['M'] = "Mike", ['N'] = "November", ['O'] = "Oscar", ['P'] = "Papa",
        ['Q'] = "Quebec", ['R'] = "Romeo", ['S'] = "Sierra", ['T'] = "Tango",
        ['U'] = "Uniform", ['V'] = "Victor", ['W'] = "Whiskey", ['X'] = "Xray",
        ['Y'] = "Yankee", ['Z'] = "Zulu",
        ['0'] = "Zero", ['1'] = "One", ['2'] = "Two", ['3'] = "Three",
        ['4'] = "Four", ['5'] = "Five", ['6'] = "Six", ['7'] = "Seven",
        ['8'] = "Eight", ['9'] = "Niner"
    };

    public CqCallerAgent(
        RadioManager radioManager,
        CwPipeline cwPipeline,
        AudioPipeline audioPipeline,
        TransmitController txController,
        SsbPipeline ssbPipeline,
        CwAiRescorer? aiRescorer = null,
        SsbAiCallsignExtractor? aiExtractor = null)
    {
        _radioManager = radioManager;
        _cwPipeline = cwPipeline;
        _audioPipeline = audioPipeline;
        _txController = txController;
        _ssbPipeline = ssbPipeline;
        _aiRescorer = aiRescorer;
        _aiExtractor = aiExtractor;
    }

    public string Start(string callsign, string name, string qth,
        string mode = "voice", int wpm = 20, int daxChannel = 1, string? licenseClass = null)
    {
        if (_running) return "CQ Caller is already running.";
        if (!_radioManager.IsConnected) return "Not connected to a radio.";

        var guard = _txController.GetTxGuardState();
        if (!guard.Armed)
            return "TX guard is not armed. Call set_tx_guard with armed=true first.";

        _myCallsign = callsign.Trim().ToUpperInvariant();
        _myName = name.Trim().ToUpperInvariant();
        _myQth = qth.Trim().ToUpperInvariant();
        _wpm = Math.Clamp(wpm, 10, 40);

        if (string.IsNullOrWhiteSpace(_myCallsign))
            return "Callsign is required.";
        if (string.IsNullOrWhiteSpace(_myName))
            return "Name is required for exchange.";
        if (string.IsNullOrWhiteSpace(_myQth))
            return "QTH is required for exchange.";

        // Parse mode
        _mode = mode.Trim().ToLowerInvariant() switch
        {
            "cw" => CqCallerMode.Cw,
            _ => CqCallerMode.Voice
        };

        // Parse or look up license class
        if (licenseClass != null)
        {
            _licenseClass = licenseClass.Trim().ToLowerInvariant() switch
            {
                "extra" or "amateur extra" => LicenseClass.Extra,
                "general" => LicenseClass.General,
                "technician" or "tech" => LicenseClass.Technician,
                _ => null
            };
        }
        else
        {
            // FCC lookup (synchronous wait on background, with timeout)
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                _licenseClass = _fccLookup.LookupAsync(_myCallsign, cts.Token).GetAwaiter().GetResult();
            }
            catch
            {
                _licenseClass = LicenseClass.Extra; // Default on failure
            }
        }

        // Get current band and check privileges
        var radioState = _radioManager.GetState();
        var currentBand = BandScoutMonitor.FrequencyToBand(radioState.FrequencyMHz);
        var radioMode = _mode == CqCallerMode.Cw ? "CW" : "VOICE";

        if (_licenseClass.HasValue && currentBand != "OOB")
        {
            var range = BandPrivileges.GetRange(_licenseClass.Value, currentBand, radioMode);
            if (range == null)
                return $"No {_licenseClass.Value} class privileges on {currentBand} for {radioMode} mode.";

            // Set radio mode
            if (_mode == CqCallerMode.Voice)
            {
                var ssbMode = radioState.FrequencyMHz >= 10.0 ? "USB" : "LSB";
                _radioManager.SetMode(ssbMode);
            }
            else
            {
                _radioManager.SetMode("CW");
            }

            // Scan for clear frequency within the allowed range
            var (found, clearFreq, scanMsg) = _scanner.FindClearFrequency(
                _radioManager, range.Value.LowMHz, range.Value.HighMHz, radioMode);
            LogStatus($"Frequency scan: {scanMsg}");
            _radioManager.SetFrequency(clearFreq);
        }
        else
        {
            // No privilege info or OOB — just set the mode and use current frequency
            if (_mode == CqCallerMode.Voice)
            {
                var ssbMode = radioState.FrequencyMHz >= 10.0 ? "USB" : "LSB";
                _radioManager.SetMode(ssbMode);
            }
            else
            {
                _radioManager.SetMode("CW");
            }
        }

        // Start audio pipeline
        if (!_audioPipeline.IsRunning)
        {
            var (audioStarted, audioError) = _audioPipeline.Start(daxChannel);
            if (!audioStarted)
                return audioError ?? "Failed to start audio pipeline.";
        }

        // Start appropriate decode pipeline
        if (_mode == CqCallerMode.Cw)
        {
            if (!_cwPipeline.IsRunning)
            {
                var state = _radioManager.GetState();
                _cwPipeline.Reset();
                _cwPipeline.SetToneFrequency(state.CwPitch);
                _cwPipeline.Start();
            }
        }
        else
        {
            if (!_ssbPipeline.IsRunning)
            {
                var result = _ssbPipeline.Start();
                if (result != "ok" && !result.Contains("already running"))
                    return $"Failed to start SSB pipeline: {result}";
            }
            _voiceTx = new VoiceTransmitter(_radioManager);

            // Optimize Whisper prompt for CQ caller — emphasize phonetic callsign patterns
            _ssbPipeline.Prompt =
                "Ham radio CQ call response. Stations reply with their callsign using NATO phonetics. " +
                "Phonetic alphabet: Alpha, Bravo, Charlie, Delta, Echo, Foxtrot, Golf, Hotel, " +
                "India, Juliett, Kilo, Lima, Mike, November, Oscar, Papa, Quebec, Romeo, " +
                "Sierra, Tango, Uniform, Victor, Whiskey, Xray, Yankee, Zulu. " +
                "Numbers: Zero, One, Two, Three, Four, Five, Six, Seven, Eight, Niner. " +
                "Callsign format: letter-number-letters like Kilo One Alpha Foxtrot = K1AF. " +
                "Common phrases: CQ, QRZ, you're 59, copy, roger, 73, over, this is, calling.";
        }

        _running = true;
        _startedUtc = DateTime.UtcNow;
        _qsosCompleted = 0;
        _cqsSent = 0;
        _currentCaller = null;
        _partialCallsign = null;
        _pileupAttempt = 0;
        _lastDecodedText = null;
        _lastSentText = null;
        _lastError = null;
        _repeatAttempts = 0;
        _accumulatedTranscriptions = "";

        lock (_lock)
        {
            _statusLog.Clear();
            _qsoLog.Clear();
            var modeStr = _mode == CqCallerMode.Voice ? "Voice" : "CW";
            LogStatus($"CQ Caller started: {_myCallsign}, Mode={modeStr}, License={_licenseClass?.ToString() ?? "Unknown"}, Name={_myName}, QTH={_myQth}");
        }

        TransitionTo(CqCallerStage.CallingCq);

        _agentThread = new Thread(AgentLoop)
        {
            IsBackground = true,
            Name = "CqCaller"
        };
        _agentThread.Start();

        var finalState = _radioManager.GetState();
        var modeLabel = _mode == CqCallerMode.Voice ? "Voice (SSB)" : $"CW ({_wpm} WPM)";
        return $"CQ Caller started for {_myCallsign} in {modeLabel} mode on {finalState.FrequencyMHz:F3} MHz. License: {_licenseClass?.ToString() ?? "Unknown"}.";
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _txController.Abort();
        _agentThread?.Join(5000);
        _agentThread = null;
        lock (_lock)
        {
            _stage = CqCallerStage.Idle;
            LogStatus("CQ Caller stopped.");
        }
    }

    public CqCallerState GetStatus()
    {
        lock (_lock)
        {
            return new CqCallerState(
                Stage: _stage,
                IsRunning: _running,
                MyCallsign: _myCallsign,
                QsosCompleted: _qsosCompleted,
                CqsSent: _cqsSent,
                CurrentCaller: _currentCaller,
                PartialCallsign: _partialCallsign,
                PileupAttempt: _pileupAttempt,
                LastDecodedText: _lastDecodedText,
                LastSentText: _lastSentText,
                LastError: _lastError,
                Mode: _mode,
                LicenseClass: _licenseClass,
                StatusLog: _statusLog.ToList());
        }
    }

    public List<CqCallerQso> GetLog()
    {
        lock (_lock) { return _qsoLog.ToList(); }
    }

    public string ExportAdif()
    {
        lock (_lock)
        {
            if (_qsoLog.Count == 0)
                return "No QSOs to export.";

            var sb = new StringBuilder();
            sb.AppendLine("SmartSDR MCP CQ Caller export");
            sb.AppendLine($"Generated {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine("<ADIF_VER:5>3.1.0");
            sb.AppendLine("<PROGRAMID:14>SmartSDR-MCP");
            sb.AppendLine("<EOH>");
            sb.AppendLine();

            foreach (var qso in _qsoLog)
            {
                AppendAdif(sb, "QSO_DATE", qso.StartedUtc.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
                AppendAdif(sb, "TIME_ON", qso.StartedUtc.ToString("HHmmss", CultureInfo.InvariantCulture));
                AppendAdif(sb, "TIME_OFF", qso.CompletedUtc.ToString("HHmmss", CultureInfo.InvariantCulture));
                AppendAdif(sb, "STATION_CALLSIGN", _myCallsign);
                AppendAdif(sb, "CALL", qso.TheirCallsign);
                AppendAdif(sb, "MODE", qso.Mode);
                AppendAdif(sb, "RST_SENT", qso.OurRst);
                AppendAdif(sb, "RST_RCVD", qso.TheirRst);
                if (!string.IsNullOrEmpty(qso.TheirName))
                    AppendAdif(sb, "NAME", qso.TheirName);
                if (!string.IsNullOrEmpty(qso.TheirQth))
                    AppendAdif(sb, "QTH", qso.TheirQth);
                if (qso.FrequencyMHz > 0)
                    AppendAdif(sb, "FREQ", qso.FrequencyMHz.ToString("F6", CultureInfo.InvariantCulture));
                if (!string.IsNullOrEmpty(qso.Band))
                    AppendAdif(sb, "BAND", qso.Band);
                sb.AppendLine("<EOR>");
                sb.AppendLine();
            }

            return sb.ToString();
        }
    }

    public string ExportAdifToFile(string filePath)
    {
        // Validate file extension
        var ext = Path.GetExtension(filePath);
        if (!ext.Equals(".adi", StringComparison.OrdinalIgnoreCase) &&
            !ext.Equals(".adif", StringComparison.OrdinalIgnoreCase))
            return $"Invalid file type '{ext}'. Only .adi and .adif files are accepted.";

        // Resolve to absolute path
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(filePath);
        }
        catch (Exception ex)
        {
            return $"Invalid file path: {ex.Message}";
        }

        // Restrict to user's home directory
        var homeDir = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var relativePath = Path.GetRelativePath(homeDir, fullPath);
        if (Path.IsPathRooted(relativePath) ||
            relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relativePath.Equals("..", StringComparison.Ordinal))
            return $"Access denied. Files must be under your home directory ({homeDir.TrimEnd(Path.DirectorySeparatorChar)}).";

        var adif = ExportAdif();
        if (adif == "No QSOs to export.")
            return adif;

        try
        {
            File.WriteAllText(fullPath, adif);
            return $"Exported {_qsoLog.Count} QSOs to {fullPath}";
        }
        catch (Exception ex)
        {
            return $"Write failed: {ex.Message}";
        }
    }

    private void AgentLoop()
    {
        // Send the first CQ immediately
        SendCq();

        while (_running)
        {
            Thread.Sleep(CycleIntervalMs);
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
                Console.Error.WriteLine($"[CQCALLER] Error: {ex.Message}");
            }
        }
    }

    private void ProcessCycle()
    {
        var elapsed = DateTime.UtcNow - _stageEnteredAt;

        switch (_stage)
        {
            case CqCallerStage.CallingCq:
                if (_mode == CqCallerMode.Voice)
                {
                    // Voice TX is synchronous — after SendCq returns, TX is done
                    // Transition directly to listening
                    ClearDecodeBuffer();
                    TransitionTo(CqCallerStage.Listening);
                }
                else
                {
                    // CW: wait for TX to complete (estimate duration)
                    var txDurationMs = EstimateTxDurationMs(_lastSentText ?? "", _wpm);
                    if (elapsed.TotalMilliseconds > txDurationMs + 500)
                    {
                        ClearDecodeBuffer();
                        TransitionTo(CqCallerStage.Listening);
                    }
                }
                break;

            case CqCallerStage.Listening:
            {
                var timeout = _mode == CqCallerMode.Voice ? VoiceListeningTimeoutMs : ListeningTimeoutMs;
                if (elapsed.TotalMilliseconds > timeout)
                {
                    var decoded = GetDecodedText();
                    if (string.IsNullOrWhiteSpace(decoded) || decoded == "(no speech detected)")
                    {
                        TransitionTo(CqCallerStage.NoCaller);
                    }
                    else
                    {
                        ProcessResponse(decoded);
                    }
                }
                break;
            }

            case CqCallerStage.NoCaller:
                if (elapsed.TotalMilliseconds > NoCallerTimeoutMs)
                {
                    LogStatus("No callers. Calling CQ again.");
                    SendCq();
                    TransitionTo(CqCallerStage.CallingCq);
                }
                break;

            case CqCallerStage.SendingExchange:
                if (_mode == CqCallerMode.Voice)
                {
                    // Voice TX is synchronous
                    ClearDecodeBuffer();
                    TransitionTo(CqCallerStage.ReceivingExchange);
                }
                else
                {
                    var exchDuration = EstimateTxDurationMs(_lastSentText ?? "", _wpm);
                    if (elapsed.TotalMilliseconds > exchDuration + 500)
                    {
                        ClearDecodeBuffer();
                        TransitionTo(CqCallerStage.ReceivingExchange);
                    }
                }
                break;

            case CqCallerStage.ReceivingExchange:
            {
                var timeout = _mode == CqCallerMode.Voice
                    ? VoiceReceivingExchangeTimeoutMs : ReceivingExchangeTimeoutMs;
                if (elapsed.TotalMilliseconds > timeout)
                {
                    var decoded2 = GetDecodedText();

                    // Check for questions before standard exchange parsing
                    if (_mode == CqCallerMode.Voice && DetectAndRespondToQuestion(decoded2))
                    {
                        // Answered a question — listen for more
                        ClearDecodeBuffer();
                        TransitionTo(CqCallerStage.ReceivingExchange);
                    }
                    else
                    {
                        ParseExchange(decoded2);
                        SendConfirm();
                        TransitionTo(CqCallerStage.Confirming);
                    }
                }
                break;
            }

            case CqCallerStage.Confirming:
                if (_mode == CqCallerMode.Voice)
                {
                    // Voice TX is synchronous
                    LogCompletedQso();
                    ClearDecodeBuffer();
                    TransitionTo(CqCallerStage.Listening);
                }
                else
                {
                    var confirmDuration = EstimateTxDurationMs(_lastSentText ?? "", _wpm);
                    if (elapsed.TotalMilliseconds > confirmDuration + 500)
                    {
                        LogCompletedQso();
                        ClearDecodeBuffer();
                        TransitionTo(CqCallerStage.Listening);
                    }
                }
                break;

            case CqCallerStage.SendingPartial:
                if (_mode == CqCallerMode.Voice)
                {
                    ClearDecodeBuffer();
                    TransitionTo(CqCallerStage.ListeningForPartial);
                }
                else
                {
                    var partialDuration = EstimateTxDurationMs(_lastSentText ?? "", _wpm);
                    if (elapsed.TotalMilliseconds > partialDuration + 500)
                    {
                        ClearDecodeBuffer();
                        TransitionTo(CqCallerStage.ListeningForPartial);
                    }
                }
                break;

            case CqCallerStage.ListeningForPartial:
                if (elapsed.TotalMilliseconds > ListeningForPartialTimeoutMs)
                {
                    var decoded3 = GetDecodedText();
                    ProcessPartialResponse(decoded3);
                }
                break;

            case CqCallerStage.Pileup:
                HandlePileup();
                break;

            case CqCallerStage.SingleCaller:
                SendExchangeTo(_currentCaller!);
                TransitionTo(CqCallerStage.SendingExchange);
                break;
        }
    }

    private void ProcessResponse(string decoded)
    {
        lock (_lock) { _lastDecodedText = decoded; }

        // Accumulate transcriptions across retries for better AI extraction
        if (_mode == CqCallerMode.Voice && !string.IsNullOrWhiteSpace(decoded) && decoded != "(no speech detected)")
        {
            _accumulatedTranscriptions = string.IsNullOrEmpty(_accumulatedTranscriptions)
                ? decoded
                : _accumulatedTranscriptions + " " + decoded;
        }

        var textToAnalyze = _mode == CqCallerMode.Voice ? _accumulatedTranscriptions : decoded;

        // Multi-stage callsign extraction pipeline
        var callsigns = ExtractCallsignsMultiStage(textToAnalyze);
        callsigns.RemoveAll(c => c.Equals(_myCallsign, StringComparison.OrdinalIgnoreCase));
        callsigns = callsigns.Where(c => CallsignPattern.IsMatch(c)).ToList();

        if (callsigns.Count == 0)
        {
            // If we decoded something but no callsigns, someone may be talking but unintelligible
            if (_mode == CqCallerMode.Voice && decoded.Trim().Length > 5 && decoded != "(no speech detected)")
            {
                if (_repeatAttempts < MaxRepeatAttempts)
                {
                    _repeatAttempts++;

                    // Try to extract partial phonetic characters to echo back
                    var (partialChars, partialPhonetic) = CallsignDetector.ExtractPartialPhonetics(textToAnalyze);
                    var myPhonetic = CallsignToPhonetic(_myCallsign);

                    if (partialChars.Length >= 1 && !string.IsNullOrWhiteSpace(partialPhonetic))
                    {
                        // Echo back what we heard: "Kilo One station, say your full callsign again"
                        LogStatus($"Decoded: \"{Truncate(decoded, 60)}\" — partial: {partialChars} (attempt {_repeatAttempts}/{MaxRepeatAttempts})");
                        SendVoiceOnly(string.Format(VoicePartialCallsignResponse, partialPhonetic, myPhonetic));
                    }
                    else
                    {
                        LogStatus($"Decoded: \"{Truncate(decoded, 60)}\" — no callsign found (attempt {_repeatAttempts}/{MaxRepeatAttempts}), requesting repeat.");
                        SendVoiceOnly(string.Format(VoiceUnintelligibleResponse, myPhonetic));
                    }

                    // Don't clear decode buffer — let text accumulate for better extraction
                    TransitionTo(CqCallerStage.Listening);
                }
                else
                {
                    LogStatus($"Decoded: \"{Truncate(decoded, 60)}\" — giving up after {MaxRepeatAttempts} attempts.");
                    ResetRetryState();
                    TransitionTo(CqCallerStage.NoCaller);
                }
            }
            else
            {
                LogStatus($"Decoded: \"{Truncate(decoded, 60)}\" — no valid callsigns found.");
                ResetRetryState();
                TransitionTo(CqCallerStage.NoCaller);
            }
        }
        else if (callsigns.Count == 1)
        {
            _currentCaller = callsigns[0];
            _qsoStartedUtc = DateTime.UtcNow;
            _questionsAnswered = 0;
            LogStatus($"Single caller: {_currentCaller}");
            ResetRetryState();
            TransitionTo(CqCallerStage.SingleCaller);
        }
        else
        {
            LogStatus($"Pileup detected! {callsigns.Count} callers: {string.Join(", ", callsigns)}");
            _pileupAttempt = 0;
            _currentCaller = null;
            ResetRetryState();
            TransitionTo(CqCallerStage.Pileup);
        }
    }

    /// <summary>
    /// Multi-stage callsign extraction: regex → phonetics → AI fallback.
    /// </summary>
    private List<string> ExtractCallsignsMultiStage(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        // Stage 1 & 2: Direct regex + phonetic conversion (handled by ExtractCallsignsWithPhonetics)
        var callsigns = CallsignDetector.ExtractCallsignsWithPhonetics(text);
        if (callsigns.Count > 0)
        {
            var source = CallsignDetector.ExtractCallsigns(text).Count > 0 ? "regex" : "phonetic";
            LogStatus($"Callsign extracted via {source}: {string.Join(", ", callsigns)}");
            return callsigns;
        }

        // Stage 3: AI extraction (if API key available)
        if (_aiExtractor != null)
        {
            try
            {
                var aiCallsign = _aiExtractor.ExtractCallsignAsync(text).GetAwaiter().GetResult();
                if (!string.IsNullOrWhiteSpace(aiCallsign))
                {
                    LogStatus($"Callsign extracted via AI: {aiCallsign}");
                    return [aiCallsign];
                }
            }
            catch (Exception ex)
            {
                LogStatus($"AI callsign extraction error: {ex.Message}");
            }
        }

        return [];
    }

    private void ResetRetryState()
    {
        _repeatAttempts = 0;
        _accumulatedTranscriptions = "";
    }

    private void HandlePileup()
    {
        _pileupAttempt++;

        if (_pileupAttempt > MaxPileupAttempts)
        {
            LogStatus($"Pileup unresolved after {MaxPileupAttempts} attempts. Calling CQ again.");
            _pileupAttempt = 0;
            _partialCallsign = null;
            SendCq();
            TransitionTo(CqCallerStage.CallingCq);
            return;
        }

        var decoded = _lastDecodedText ?? "";
        var callsigns = CallsignDetector.ExtractCallsigns(decoded);
        callsigns.RemoveAll(c => c.Equals(_myCallsign, StringComparison.OrdinalIgnoreCase));
        callsigns = callsigns.Where(c => CallsignPattern.IsMatch(c)).ToList();

        if (callsigns.Count == 0)
        {
            LogStatus("Pileup: can't extract callsigns. Calling CQ.");
            SendCq();
            TransitionTo(CqCallerStage.CallingCq);
            return;
        }

        var target = callsigns[0];
        _partialCallsign = ExtractPartial(target);
        _qsoStartedUtc = DateTime.UtcNow;

        LogStatus($"Pileup attempt {_pileupAttempt}/{MaxPileupAttempts}: sending {_partialCallsign}?");
        SendPartialQuery(_partialCallsign);
        TransitionTo(CqCallerStage.SendingPartial);
    }

    private void ProcessPartialResponse(string decoded)
    {
        lock (_lock) { _lastDecodedText = decoded; }

        var callsigns = CallsignDetector.ExtractCallsigns(decoded);
        callsigns.RemoveAll(c => c.Equals(_myCallsign, StringComparison.OrdinalIgnoreCase));
        callsigns = callsigns.Where(c => CallsignPattern.IsMatch(c)).ToList();

        if (callsigns.Count == 1)
        {
            _currentCaller = callsigns[0];
            LogStatus($"Pileup resolved: {_currentCaller}");
            _pileupAttempt = 0;
            SendExchangeTo(_currentCaller);
            TransitionTo(CqCallerStage.SendingExchange);
        }
        else if (callsigns.Count > 1)
        {
            LogStatus($"Still {callsigns.Count} callers after partial query.");
            TransitionTo(CqCallerStage.Pileup);
        }
        else
        {
            LogStatus("No response to partial query.");
            TransitionTo(CqCallerStage.Pileup);
        }
    }

    // ── Dispatch helpers (branch on mode) ────────────────────────────

    private void SendMessage(string cwText, string voiceText)
    {
        if (_mode == CqCallerMode.Cw)
        {
            var (success, message) = _txController.SendText(cwText, _wpm);
            lock (_lock)
            {
                _lastSentText = cwText;
                if (!success)
                    _lastError = message;
            }
        }
        else
        {
            lock (_lock) { _lastSentText = voiceText; }

            if (_voiceTx == null)
            {
                lock (_lock) { _lastError = "Voice transmitter not initialized."; }
                return;
            }

            var (success, message) = _voiceTx.SpeakAsync(voiceText).GetAwaiter().GetResult();
            if (!success)
            {
                lock (_lock) { _lastError = message; }
            }
        }
    }

    private string GetDecodedText()
    {
        if (_mode == CqCallerMode.Voice)
        {
            return _ssbPipeline.GetLiveText();
        }

        var rawText = _cwPipeline.GetLiveText();
        var chars = _cwPipeline.GetRecentCharacters();

        // Try AI rescore if available and text is long enough
        if (_aiRescorer != null && !string.IsNullOrWhiteSpace(rawText) && rawText.Trim().Length >= 3)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var rescoreTask = _aiRescorer.RescoreAsync(rawText, chars);
                var completed = Task.WhenAny(rescoreTask, Task.Delay(10_000, cts.Token)).GetAwaiter().GetResult();
                if (completed == rescoreTask)
                {
                    cts.Cancel();
                    var result = rescoreTask.GetAwaiter().GetResult();
                    if (result.AiApplied)
                    {
                        LogStatus($"AI rescore: \"{rawText.Trim()}\" → \"{result.CorrectedText.Trim()}\"");
                        return result.CorrectedText;
                    }
                }
            }
            catch (Exception ex)
            {
                LogStatus($"AI rescore error: {ex.Message}");
            }
        }

        return rawText;
    }

    private void ClearDecodeBuffer()
    {
        if (_mode == CqCallerMode.Voice)
            _ssbPipeline.ClearLiveText();
        else
            _cwPipeline.ClearLiveText();
    }

    // ── TX message builders ──────────────────────────────────────────

    private void SendCq()
    {
        ResetRetryState();

        var phonetic = CallsignToPhonetic(_myCallsign);
        var cwText = string.Format(CwCqTemplate, _myCallsign);
        var voiceText = string.Format(VoiceCqTemplate, phonetic, _myCallsign);

        SendMessage(cwText, voiceText);
        lock (_lock)
        {
            _cqsSent++;
            if (_lastError == null)
                LogStatus($"CQ #{_cqsSent} sent ({(_mode == CqCallerMode.Voice ? "voice" : "CW")})");
            else
                LogStatus($"CQ TX failed: {_lastError}");
        }
    }

    private void SendExchangeTo(string theirCallsign)
    {
        var cwText = string.Format(CwExchangeTemplate, theirCallsign, _myName, _myQth);
        var voiceText = string.Format(VoiceExchangeTemplate,
            CallsignToPhonetic(theirCallsign), _myName, _myQth);

        SendMessage(cwText, voiceText);
        lock (_lock)
        {
            if (_lastError == null)
                LogStatus($"Exchange sent to {theirCallsign}");
            else
                LogStatus($"Exchange TX failed: {_lastError}");
        }
    }

    private void SendConfirm()
    {
        var nameForConfirm = _theirName ?? _currentCaller ?? "OM";
        var cwText = string.Format(CwConfirmTemplate, nameForConfirm, _myCallsign);
        var voiceText = string.Format(VoiceConfirmTemplate,
            nameForConfirm, CallsignToPhonetic(_myCallsign));

        SendMessage(cwText, voiceText);
        lock (_lock)
        {
            if (_lastError == null)
                LogStatus($"TU CQ sent (QSO with {_currentCaller} complete)");
            else
                LogStatus($"Confirm TX failed: {_lastError}");
        }
    }

    private void SendPartialQuery(string partial)
    {
        var cwText = string.Format(CwPartialTemplate, partial);
        var voiceText = string.Format(VoicePartialTemplate, partial);

        SendMessage(cwText, voiceText);
        lock (_lock)
        {
            if (_lastError == null)
                LogStatus($"Partial query sent: {partial}");
            else
                LogStatus($"Partial TX failed: {_lastError}");
        }
    }

    // ── Exchange parsing ─────────────────────────────────────────────

    private void ParseExchange(string decoded)
    {
        if (string.IsNullOrWhiteSpace(decoded) || decoded == "(no speech detected)")
        {
            _theirRst = _mode == CqCallerMode.Cw ? "599" : "59";
            _theirName = null;
            _theirQth = null;
            return;
        }

        lock (_lock) { _lastDecodedText = decoded; }

        var upper = decoded.ToUpperInvariant();

        // Look for RST
        if (_mode == CqCallerMode.Cw)
        {
            var rstMatch = Regex.Match(upper, @"\b([1-5][1-9][1-9])\b");
            _theirRst = rstMatch.Success ? rstMatch.Value : "599";
        }
        else
        {
            var rstMatch = Regex.Match(upper, @"\b([1-5][1-9])\b");
            _theirRst = rstMatch.Success ? rstMatch.Value : "59";
        }

        // Look for NAME pattern
        var nameMatch = Regex.Match(upper, @"NAME\s+([A-Z]{2,15})");
        _theirName = nameMatch.Success ? nameMatch.Groups[1].Value : null;

        // Also try "MY NAME IS" pattern for voice
        if (_theirName == null && _mode == CqCallerMode.Voice)
        {
            var nameMatch2 = Regex.Match(upper, @"(?:MY\s+NAME\s+IS|I'M|I\s+AM)\s+([A-Z]{2,15})");
            _theirName = nameMatch2.Success ? nameMatch2.Groups[1].Value : null;
        }

        // Look for QTH pattern
        var qthMatch = Regex.Match(upper, @"QTH\s+([A-Z]{2,20})");
        _theirQth = qthMatch.Success ? qthMatch.Groups[1].Value : null;

        LogStatus($"Parsed exchange: RST={_theirRst}, Name={_theirName ?? "?"}, QTH={_theirQth ?? "?"}");
    }

    private void LogCompletedQso()
    {
        if (_currentCaller == null) return;

        var radioState = _radioManager.GetState();
        var freq = radioState.FrequencyMHz;
        var band = BandScoutMonitor.FrequencyToBand(freq);
        var adifMode = _mode == CqCallerMode.Cw ? "CW" : "SSB";

        var qso = new CqCallerQso(
            TheirCallsign: _currentCaller,
            OurRst: _mode == CqCallerMode.Cw ? "599" : "59",
            TheirRst: _theirRst ?? (_mode == CqCallerMode.Cw ? "599" : "59"),
            TheirName: _theirName,
            TheirQth: _theirQth,
            FrequencyMHz: freq,
            Band: band,
            Mode: adifMode,
            StartedUtc: _qsoStartedUtc,
            CompletedUtc: DateTime.UtcNow);

        lock (_lock)
        {
            _qsoLog.Add(qso);
            _qsosCompleted++;
            LogStatus($"QSO #{_qsosCompleted} logged: {_currentCaller} RST {_theirRst ?? "?"} on {band} ({adifMode})");
        }

        // Reset caller state
        _currentCaller = null;
        _partialCallsign = null;
        _pileupAttempt = 0;
        _theirRst = null;
        _theirName = null;
        _theirQth = null;
        ResetRetryState();
    }

    // ── Question detection and context-aware responses ─────────────

    private int _questionsAnswered;

    /// <summary>
    /// Detect common questions in decoded speech and send appropriate responses.
    /// Falls back to AI-generated conversational responses for unrecognized questions.
    /// Returns true if a question was detected and answered.
    /// </summary>
    private bool DetectAndRespondToQuestion(string decoded)
    {
        if (string.IsNullOrWhiteSpace(decoded) || decoded == "(no speech detected)")
            return false;

        // Limit to 4 question responses per QSO to allow natural conversation
        if (_questionsAnswered >= 4)
            return false;

        var upper = decoded.ToUpperInvariant();
        var callerRef = _currentCaller != null ? CallsignToPhonetic(_currentCaller) : "station";
        var myPhonetic = CallsignToPhonetic(_myCallsign);

        // Fast-path: hardcoded responses for common questions

        // Detect legality / FCC questions
        if (ContainsAny(upper, "LEGAL", "FCC", "ALLOWED", "LICENSED", "LICENSE", "COMPLY", "COMPLIANCE", "RULES", "PART 97", "CONTROL OPERATOR"))
        {
            var response = string.Format(VoiceLegalResponse, callerRef, _myCallsign);
            LogStatus($"Question detected (legal): responding");
            SendVoiceOnly(response);
            _questionsAnswered++;
            return true;
        }

        // Detect questions about the operator / who are you
        if (ContainsAny(upper, "WHO ARE YOU", "TELL ME ABOUT", "ABOUT YOURSELF", "ABOUT YOU", "WHO IS PATRICK", "WHO IS THE OPERATOR", "YOUR OPERATOR"))
        {
            var response = string.Format(VoiceAboutMeResponse, callerRef);
            LogStatus($"Question detected (about me): responding");
            SendVoiceOnly(response);
            _questionsAnswered++;
            return true;
        }

        // Detect questions about the technology / how does it work
        if (ContainsAny(upper, "HOW DOES", "HOW DO YOU", "WHAT TECHNOLOGY", "WHAT SOFTWARE", "MCP", "CLAUDE", "WHISPER", "AI WORK", "HOW ARE YOU DOING THIS", "WHAT ARE YOU RUNNING", "WHAT SYSTEM", "TEXT TO SPEECH", "SPEECH TO TEXT"))
        {
            var response = string.Format(VoiceAboutTechResponse, callerRef);
            LogStatus($"Question detected (technology): responding");
            SendVoiceOnly(response);
            _questionsAnswered++;
            return true;
        }

        // Detect weak/unintelligible signal indications
        if (ContainsAny(upper, "AGAIN", "REPEAT", "SAY AGAIN", "COPY", "NOT COPY", "DIDN'T GET", "COME AGAIN"))
        {
            // They didn't copy us — resend exchange
            LogStatus("Station requested repeat");
            return false; // Let normal flow resend
        }

        // AI conversational fallback — handle casual chat, jokes, general questions
        if (_aiExtractor != null && decoded.Trim().Length > 10 && LooksConversational(upper))
        {
            try
            {
                var aiResponse = _aiExtractor.GenerateConversationalResponseAsync(
                    decoded, _myCallsign, _myName, _myQth, _currentCaller)
                    .GetAwaiter().GetResult();

                if (!string.IsNullOrWhiteSpace(aiResponse))
                {
                    LogStatus($"AI conversational response to: \"{Truncate(decoded, 40)}\"");
                    SendVoiceOnly(aiResponse);
                    _questionsAnswered++;
                    return true;
                }
            }
            catch (Exception ex)
            {
                LogStatus($"AI conversation error: {ex.Message}");
            }
        }

        return false;
    }

    /// <summary>
    /// Heuristic: does the text look conversational rather than just a callsign/exchange?
    /// </summary>
    private static bool LooksConversational(string upperText)
    {
        // If it contains question-like words or is longer than a typical exchange
        if (upperText.Contains('?')) return true;
        if (ContainsAny(upperText, "WHAT", "WHERE", "HOW", "WHY", "TELL", "CAN YOU",
            "DO YOU", "ARE YOU", "HAVE YOU", "WOULD", "COULD", "SHOULD",
            "THINK", "KNOW", "LIKE", "ENJOY", "FAVORITE", "OPINION"))
            return true;
        // Long decoded text that isn't just signal reports
        return upperText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 8;
    }

    private void SendVoiceOnly(string text)
    {
        if (_voiceTx == null) return;
        lock (_lock) { _lastSentText = text; }
        var (success, message) = _voiceTx.SpeakAsync(text).GetAwaiter().GetResult();
        if (!success)
            lock (_lock) { _lastError = message; }
    }

    private static bool ContainsAny(string text, params string[] keywords) =>
        keywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));

    // ── Utility methods ──────────────────────────────────────────────

    private static string CallsignToPhonetic(string callsign) =>
        string.Join(" ", callsign.ToUpperInvariant().Select(c =>
            NatoAlphabet.TryGetValue(c, out var word) ? word : c.ToString()));

    private static string ExtractPartial(string callsign)
    {
        for (int i = 0; i < callsign.Length; i++)
        {
            if (char.IsDigit(callsign[i]))
                return callsign[..(i + 1)];
        }
        return callsign.Length >= 2 ? callsign[..2] : callsign;
    }

    private static int EstimateTxDurationMs(string text, int wpm)
    {
        if (string.IsNullOrEmpty(text) || wpm <= 0) return 2000;
        var dotMs = 1200.0 / wpm;
        int units = 0;
        foreach (var c in text)
        {
            units += c == ' ' ? 7 : 10;
        }
        return (int)(units * dotMs);
    }

    private void TransitionTo(CqCallerStage newStage)
    {
        _stage = newStage;
        _stageEnteredAt = DateTime.UtcNow;
        Console.Error.WriteLine($"[CQCALLER] → {newStage}");
    }

    private void LogStatus(string message)
    {
        var entry = $"[{DateTime.UtcNow:HH:mm:ss}] {message}";
        _statusLog.Add(entry);
        if (_statusLog.Count > MaxStatusLog)
            _statusLog.RemoveAt(0);
        Console.Error.WriteLine($"[CQCALLER] {message}");
    }

    private static void AppendAdif(StringBuilder sb, string field, string value)
    {
        value ??= string.Empty;
        sb.AppendLine($"<{field}:{value.Length}>{value}");
    }

    private static string Truncate(string text, int maxLen) =>
        text.Length <= maxLen ? text : text[..maxLen] + "...";
}
