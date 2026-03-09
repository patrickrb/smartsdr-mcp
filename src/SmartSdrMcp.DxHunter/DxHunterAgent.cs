using SmartSdrMcp.Ai;
using SmartSdrMcp.BandScout;
using SmartSdrMcp.Cw;
using SmartSdrMcp.Radio;
using SmartSdrMcp.Ssb;
using SmartSdrMcp.Tx;

namespace SmartSdrMcp.DxHunter;

public class DxHunterAgent
{
    private readonly RadioManager _radioManager;
    private readonly CwPipeline? _cwPipeline;
    private readonly CwAiRescorer? _aiRescorer;
    private readonly SsbPipeline? _ssbPipeline;
    private readonly TransmitController? _txController;
    private readonly object _lock = new();
    private readonly List<string> _statusLog = new();
    private readonly List<ListenResult> _listenResults = new();
    private const int MaxListenResults = 50;
    private int _listenDurationSeconds = 15;

    // Worked log: set of "ENTITY|BAND" and "ENTITY|BAND|MODE" keys
    private readonly HashSet<string> _workedEntityBand = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _workedEntityBandMode = new(StringComparer.OrdinalIgnoreCase);
    private int _logRecordCount;
    private string? _logFilePath;

    // Alerts: spots that are "needs" (not yet worked on that band)
    private readonly List<DxAlert> _alerts = new();
    private readonly HashSet<string> _alertedCallsigns = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxAlerts = 100;
    private const int MaxStatusLog = 50;
    private const int PollIntervalMs = 10_000; // 10 seconds
    private readonly HashSet<string> _listenedAlerts = new(StringComparer.OrdinalIgnoreCase); // track what we've already listened to

    private Thread? _hunterThread;
    private volatile bool _running;
    private DateTime _startedUtc;
    private string _targetBand = ""; // empty = all bands
    private string _targetMode = ""; // empty = all modes
    private bool _autoTune;
    private string _currentBand = ""; // track band for ATU tune on change

    public bool IsRunning => _running;

    public DxHunterAgent(RadioManager radioManager, CwPipeline? cwPipeline = null, CwAiRescorer? aiRescorer = null, SsbPipeline? ssbPipeline = null, TransmitController? txController = null)
    {
        _radioManager = radioManager;
        _cwPipeline = cwPipeline;
        _aiRescorer = aiRescorer;
        _ssbPipeline = ssbPipeline;
        _txController = txController;
    }

    // Allowed file extensions for log files
    private static readonly HashSet<string> AllowedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".adi", ".adif", ".ADI", ".ADIF" };

    /// <summary>
    /// Load an ADIF logbook to determine what's already been worked.
    /// </summary>
    public string LoadLog(string filePath)
    {
        // Validate file extension
        var ext = Path.GetExtension(filePath);
        if (!AllowedExtensions.Contains(ext))
            return $"Invalid file type '{ext}'. Only .adi and .adif files are accepted.";

        // Resolve to absolute path and block path traversal
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(filePath);
        }
        catch (Exception ex)
        {
            return $"Invalid file path: {ex.Message}";
        }

        // Restrict to user's home directory tree (normalize with trailing separator to prevent prefix bypass)
        var homeDir = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var relativePath = Path.GetRelativePath(homeDir, fullPath);
        if (Path.IsPathRooted(relativePath) ||
            relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relativePath.Equals("..", StringComparison.Ordinal))
            return $"Access denied. Log files must be under your home directory ({homeDir.TrimEnd(Path.DirectorySeparatorChar)}).";

        if (!File.Exists(fullPath))
            return $"File not found: {fullPath}";

        try
        {
            var records = AdifParser.ParseFile(fullPath);

            lock (_lock)
            {
                _workedEntityBand.Clear();
                _workedEntityBandMode.Clear();
                _logRecordCount = records.Count;
                _logFilePath = fullPath;

                var entities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var rec in records)
                {
                    var entity = DxccLookup.GetEntity(rec.Callsign);
                    if (entity == "Unknown") continue;

                    entities.Add(entity);
                    _workedEntityBand.Add($"{entity}|{rec.Band}");
                    _workedEntityBandMode.Add($"{entity}|{rec.Band}|{rec.Mode}");
                }

                LogStatus($"Loaded {records.Count} QSOs from log. {entities.Count} unique DXCC entities, " +
                         $"{_workedEntityBand.Count} entity-band slots worked.");
            }

            return $"Loaded {records.Count} QSOs. {_workedEntityBand.Count} entity-band slots worked.";
        }
        catch (Exception ex)
        {
            return $"Error parsing ADIF: {ex.Message}";
        }
    }

    /// <summary>
    /// Start the DX Hunter agent.
    /// </summary>
    public string Start(string? band = null, string? mode = null, bool autoTune = false, int listenSeconds = 15)
    {
        if (_running) return "DX Hunter is already running.";
        if (!_radioManager.IsConnected) return "Not connected to a radio.";

        if (_workedEntityBand.Count == 0)
            return "No logbook loaded. Call dx_hunter_load_log first with your ADIF file path.";

        _targetBand = band?.Trim() ?? "";
        _targetMode = mode?.Trim().ToUpperInvariant() ?? "";
        _autoTune = autoTune;
        _listenDurationSeconds = Math.Clamp(listenSeconds, 5, 60);
        _running = true;
        _startedUtc = DateTime.UtcNow;

        lock (_lock)
        {
            _statusLog.Clear();
            _alerts.Clear();
            _alertedCallsigns.Clear();
            _listenResults.Clear();
            _listenedAlerts.Clear();
            var bandInfo = string.IsNullOrEmpty(_targetBand) ? "all bands" : _targetBand;
            var modeInfo = string.IsNullOrEmpty(_targetMode) ? "all modes" : _targetMode;
            var listenInfo = autoTune && _cwPipeline != null ? $", Listen={_listenDurationSeconds}s" : "";
            LogStatus($"DX Hunter started. Watching {bandInfo}, {modeInfo}. AutoTune={autoTune}{listenInfo}.");
        }

        // Immediate scan
        ScanSpots();

        _hunterThread = new Thread(HunterLoop)
        {
            IsBackground = true,
            Name = "DxHunter"
        };
        _hunterThread.Start();

        return "DX Hunter started. Use dx_hunter_status to see alerts.";
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _hunterThread?.Join(5000);
        _hunterThread = null;
        lock (_lock) { LogStatus("DX Hunter stopped."); }
    }

    public DxHunterState GetStatus()
    {
        lock (_lock)
        {
            return new DxHunterState(
                IsRunning: _running,
                StartedUtc: _startedUtc,
                LogFile: _logFilePath,
                LogRecords: _logRecordCount,
                WorkedEntityBandSlots: _workedEntityBand.Count,
                TargetBand: _targetBand,
                TargetMode: _targetMode,
                AutoTune: _autoTune,
                Alerts: _alerts.OrderByDescending(a => a.DetectedUtc).Take(20).ToList(),
                TotalAlerts: _alerts.Count,
                ListenResults: _listenResults.OrderByDescending(r => r.ListenedUtc).Take(10).ToList(),
                StatusLog: _statusLog.ToList());
        }
    }

    /// <summary>
    /// Get recent listen results from active hunting.
    /// </summary>
    public List<ListenResult> GetListenResults()
    {
        lock (_lock)
        {
            return _listenResults.OrderByDescending(r => r.ListenedUtc).ToList();
        }
    }

    /// <summary>
    /// Get a list of all "needed" entities from the current spot list.
    /// </summary>
    public List<DxAlert> GetNeeds()
    {
        lock (_lock)
        {
            return _alerts.OrderByDescending(a => a.DetectedUtc).ToList();
        }
    }

    /// <summary>
    /// Tune to the top priority DX alert.
    /// </summary>
    public string TuneToNext()
    {
        DxAlert? best;
        lock (_lock)
        {
            best = _alerts
                .OrderByDescending(a => a.Priority)
                .ThenByDescending(a => a.DetectedUtc)
                .FirstOrDefault();
        }

        if (best == null) return "No DX needs detected.";

        var (success, message) = _radioManager.TuneToSpot(best.Callsign);
        if (success)
        {
            lock (_lock) { LogStatus($"Tuned to {best.Callsign} ({best.Entity}) on {best.Band} at {best.FrequencyMHz:F3} MHz"); }
        }
        return success ? $"Tuned to {best.Callsign} ({best.Entity}, {best.Band}) at {best.FrequencyMHz:F3} MHz" : message;
    }

    /// <summary>
    /// Mark an entity/band as worked (e.g., after completing a QSO).
    /// </summary>
    public string MarkWorked(string callsign, string? band = null, string? mode = null)
    {
        var entity = DxccLookup.GetEntity(callsign);
        if (entity == "Unknown")
            return $"Could not resolve DXCC entity for {callsign}.";

        string actualBand;
        string actualMode;

        if (band == null || mode == null)
        {
            var state = _radioManager.GetState();
            var inferredBand = BandScout.BandScoutMonitor.FrequencyToBand(state.FrequencyMHz);
            var inferredMode = state.Mode;

            if (band == null && string.Equals(inferredBand, "OOB", StringComparison.OrdinalIgnoreCase))
                return "Cannot infer band from radio state (radio may be disconnected). Please specify the band explicitly.";

            if (mode == null && string.Equals(inferredMode, "N/A", StringComparison.OrdinalIgnoreCase))
                return "Cannot infer mode from radio state (radio may be disconnected). Please specify the mode explicitly.";

            actualBand = band ?? inferredBand;
            actualMode = mode ?? inferredMode;
        }
        else
        {
            actualBand = band;
            actualMode = mode;
        }

        lock (_lock)
        {
            _workedEntityBand.Add($"{entity}|{actualBand}");
            _workedEntityBandMode.Add($"{entity}|{actualBand}|{actualMode}");

            // Remove alerts for this entity/band
            _alerts.RemoveAll(a => a.Entity.Equals(entity, StringComparison.OrdinalIgnoreCase)
                                && a.Band.Equals(actualBand, StringComparison.OrdinalIgnoreCase));

            LogStatus($"Marked {entity} ({callsign}) as worked on {actualBand} {actualMode}.");
        }

        return $"Marked {entity} ({callsign}) as worked on {actualBand} {actualMode}.";
    }

    private void HunterLoop()
    {
        while (_running)
        {
            try
            {
                // Scan for new spots/needs
                ScanSpots();

                // If autoTune, cycle through alerts and listen to each one
                if (_autoTune && _running)
                {
                    var next = GetNextUnlistenedAlert();
                    if (next != null)
                    {
                        TuneAndListen(next);
                    }
                    else
                    {
                        // All alerts listened to — wait for new spots
                        Thread.Sleep(PollIntervalMs);
                    }
                }
                else
                {
                    Thread.Sleep(PollIntervalMs);
                }
            }
            catch (Exception ex)
            {
                lock (_lock) { LogStatus($"Error: {ex.Message}"); }
                Thread.Sleep(PollIntervalMs);
            }

            if (!_running) break;
        }
    }

    // Band priority order for cycling (lower bands first, best propagation windows)
    private static readonly string[] BandOrder =
        ["160m", "80m", "60m", "40m", "30m", "20m", "17m", "15m", "12m", "10m", "6m"];

    private static int BandSortKey(string band)
    {
        var idx = Array.IndexOf(BandOrder, band);
        return idx >= 0 ? idx : 999;
    }

    private DxAlert? GetNextUnlistenedAlert()
    {
        lock (_lock)
        {
            if (_alerts.Count == 0) return null;

            // Group by band, prioritize current band first to minimize tuning,
            // then sort remaining bands in order
            var unlistened = _alerts
                .Where(a => !_listenedAlerts.Contains($"{a.Callsign}|{a.Band}"))
                .OrderBy(a => a.Band.Equals(_currentBand, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(a => BandSortKey(a.Band))
                .ThenByDescending(a => a.Priority)
                .FirstOrDefault();

            if (unlistened != null)
            {
                _listenedAlerts.Add($"{unlistened.Callsign}|{unlistened.Band}");
                return unlistened;
            }

            // All listened — reset and start over
            _listenedAlerts.Clear();
            return null; // pause one cycle before restarting
        }
    }

    private void ScanSpots()
    {
        if (!_radioManager.IsConnected) return;

        var spots = _radioManager.ListSpots();
        int newNeeds = 0;

        foreach (var spotObj in spots)
        {
            var type = spotObj.GetType();
            var callsign = type.GetProperty("Callsign")?.GetValue(spotObj)?.ToString() ?? "";
            var freq = (double)(type.GetProperty("FrequencyMHz")?.GetValue(spotObj) ?? 0.0);
            var mode = type.GetProperty("Mode")?.GetValue(spotObj)?.ToString() ?? "";
            var comment = type.GetProperty("Comment")?.GetValue(spotObj)?.ToString() ?? "";
            var spotter = type.GetProperty("SpotterCallsign")?.GetValue(spotObj)?.ToString() ?? "";

            if (string.IsNullOrWhiteSpace(callsign)) continue;

            var band = BandScout.BandScoutMonitor.FrequencyToBand(freq);
            if (band == "OOB") continue;

            // Skip FT8/FT4 spots — we can't decode those
            if (IsFt8Frequency(freq) || IsFt8Mode(mode, comment)) continue;

            // Filter by target band/mode
            if (!string.IsNullOrEmpty(_targetBand) &&
                !band.Equals(_targetBand, StringComparison.OrdinalIgnoreCase))
                continue;

            // If a target mode is set, check mode field AND comment for mode keywords
            if (!string.IsNullOrEmpty(_targetMode))
            {
                bool modeMatch = (!string.IsNullOrEmpty(mode) &&
                    mode.Equals(_targetMode, StringComparison.OrdinalIgnoreCase));
                // Also check comment for mode keywords (cluster spots often put mode in comment)
                if (!modeMatch && !string.IsNullOrEmpty(comment))
                    modeMatch = comment.Contains(_targetMode, StringComparison.OrdinalIgnoreCase);
                // Also infer CW from frequency (below x.060 on most bands is CW)
                if (!modeMatch && _targetMode.Equals("CW", StringComparison.OrdinalIgnoreCase))
                    modeMatch = IsCwFrequency(freq);
                if (!modeMatch) continue;
            }

            var entity = DxccLookup.GetEntity(callsign);
            if (entity == "Unknown") continue;

            var key = $"{entity}|{band}";
            bool isNeed;

            lock (_lock)
            {
                isNeed = !_workedEntityBand.Contains(key);
            }

            if (!isNeed) continue;

            // This is a need!
            var alertKey = $"{callsign}|{band}".ToUpperInvariant();

            lock (_lock)
            {
                if (_alertedCallsigns.Contains(alertKey)) continue;
                _alertedCallsigns.Add(alertKey);

                // Priority: rarer entities get higher priority (could be improved with actual DXCC most-wanted list)
                var priority = 5; // Default medium

                var alert = new DxAlert(
                    Callsign: callsign.ToUpperInvariant(),
                    Entity: entity,
                    Band: band,
                    Mode: mode,
                    FrequencyMHz: freq,
                    Spotter: spotter,
                    Comment: comment,
                    Priority: priority,
                    DetectedUtc: DateTime.UtcNow);

                _alerts.Add(alert);
                if (_alerts.Count > MaxAlerts)
                    _alerts.RemoveAt(0);

                newNeeds++;
                LogStatus($"NEW NEED: {callsign} = {entity} on {band} ({freq:F3} MHz) — not worked!");
            }
        }

        if (newNeeds > 0)
        {
            lock (_lock)
            {
                LogStatus($"Found {newNeeds} new needs! Total alerts: {_alerts.Count}");
            }
        }
    }

    /// <summary>
    /// Tune to a specific DX alert and listen with the appropriate decoder (CW or SSB).
    /// </summary>
    private void TuneAndListen(DxAlert best)
    {
        // Check if we're changing bands — need TUNE
        var spotBand = BandScout.BandScoutMonitor.FrequencyToBand(best.FrequencyMHz);
        bool bandChanged = !string.IsNullOrEmpty(_currentBand) &&
                           !spotBand.Equals(_currentBand, StringComparison.OrdinalIgnoreCase) &&
                           spotBand != "OOB";

        // Tune to the spot
        var (success, message) = _radioManager.TuneToSpot(best.Callsign);
        if (!success)
        {
            lock (_lock) { LogStatus($"AutoTune failed for {best.Callsign}: {message}"); }
            return;
        }

        // Set the correct mode for this spot
        bool isCw = IsCwSpot(best);
        if (isCw)
        {
            _radioManager.SetMode("CW");
        }
        else
        {
            // SSB: USB above 10 MHz, LSB below (ham convention)
            _radioManager.SetMode(best.FrequencyMHz >= 10.0 ? "USB" : "LSB");
        }

        // TUNE carrier if band changed — keys TX at 5W, watches SWR until it stabilizes
        if (bandChanged && spotBand != "OOB")
        {
            // Check TX guard — don't transmit if inhibited
            var txGuard = _txController?.GetTxGuardState();
            if (txGuard != null && !txGuard.Armed)
            {
                lock (_lock) { LogStatus($"Band change: {_currentBand} → {spotBand} — TX inhibited, skipping TUNE."); }
                _currentBand = spotBand;
            }
            else
            {
                lock (_lock) { LogStatus($"Band change: {_currentBand} → {spotBand} — TUNE at 5W..."); }
                _currentBand = spotBand;

                // Save prior tune power to restore after
                int? priorTunePower = null;
                var rfState = _radioManager.GetRfPower();
                if (rfState != null)
                {
                    var tpProp = rfState.GetType().GetProperty("TunePower");
                    if (tpProp?.GetValue(rfState) is int tp) priorTunePower = tp;
                }

                // Set tune power to 5W, then key the TUNE carrier
                _radioManager.SetRfPower(rfPower: null, tunePower: 5);
                _radioManager.SetTx(mox: null, txTune: true, txMonitor: null, txInhibit: null);

                // Wait for SWR to stabilize (< 2.0) or timeout at 10s
                var tuneResult = WaitForSwrStable(maxWaitMs: 10_000, targetSwr: 2.0, stableReadings: 3);

                // Stop TUNE
                _radioManager.SetTx(mox: null, txTune: false, txMonitor: null, txInhibit: null);

                // Restore prior tune power
                if (priorTunePower.HasValue)
                    _radioManager.SetRfPower(rfPower: null, tunePower: priorTunePower.Value);

                if (!_running) return;

                Thread.Sleep(300); // brief settle time
                lock (_lock) { LogStatus(tuneResult); }
            }
        }
        else if (spotBand != "OOB")
        {
            _currentBand = spotBand;
        }

        string decodeMode = isCw ? "CW" : "SSB";

        lock (_lock)
        {
            LogStatus($"Tuned to {best.Callsign} ({best.Entity}) on {best.Band} at {best.FrequencyMHz:F3} MHz — {decodeMode} listening for {_listenDurationSeconds}s...");
        }

        if (isCw)
            ListenCw(best);
        else
            ListenSsb(best);
    }

    private bool IsCwSpot(DxAlert alert)
    {
        // Check explicit mode
        if (!string.IsNullOrEmpty(alert.Mode))
        {
            if (alert.Mode.Equals("CW", StringComparison.OrdinalIgnoreCase))
                return true;
            if (alert.Mode.Equals("SSB", StringComparison.OrdinalIgnoreCase) ||
                alert.Mode.Equals("LSB", StringComparison.OrdinalIgnoreCase) ||
                alert.Mode.Equals("USB", StringComparison.OrdinalIgnoreCase))
                return false;
        }
        // Check comment for mode hints
        if (!string.IsNullOrEmpty(alert.Comment))
        {
            if (alert.Comment.Contains("CW", StringComparison.OrdinalIgnoreCase))
                return true;
            if (alert.Comment.Contains("SSB", StringComparison.OrdinalIgnoreCase))
                return false;
        }
        // Infer from frequency
        return IsCwFrequency(alert.FrequencyMHz);
    }

    private void ListenCw(DxAlert spot)
    {
        if (_cwPipeline == null)
        {
            lock (_lock) { LogStatus($"No CW decoder available for {spot.Callsign}"); }
            return;
        }

        _cwPipeline.ClearLiveText();
        bool weStartedCw = false;
        if (!_cwPipeline.IsRunning)
        {
            _cwPipeline.Start();
            weStartedCw = true;
        }

        Thread.Sleep(_listenDurationSeconds * 1000);
        if (!_running) return;

        var rawText = _cwPipeline.GetLiveText();
        var chars = _cwPipeline.GetRecentCharacters();

        string correctedText = rawText;
        bool aiApplied = false;
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
                        correctedText = result.CorrectedText;
                        aiApplied = true;
                    }
                }
                else
                {
                    lock (_lock) { LogStatus($"AI rescore timeout for {spot.Callsign}, skipping"); }
                }
            }
            catch (Exception ex)
            {
                lock (_lock) { LogStatus($"AI rescore error: {ex.Message}"); }
            }
        }

        RecordListenResult(spot, "CW", rawText, correctedText, aiApplied);

        if (weStartedCw)
            _cwPipeline.Stop();
    }

    private void ListenSsb(DxAlert spot)
    {
        if (_ssbPipeline == null)
        {
            lock (_lock) { LogStatus($"No SSB decoder available for {spot.Callsign}"); }
            return;
        }

        _ssbPipeline.ClearLiveText();
        bool weStartedSsb = false;
        if (!_ssbPipeline.IsRunning)
        {
            var startResult = _ssbPipeline.Start();
            if (startResult != "ok")
            {
                lock (_lock) { LogStatus($"SSB start failed for {spot.Callsign}: {startResult}"); }
                return;
            }
            weStartedSsb = true;
        }

        Thread.Sleep(_listenDurationSeconds * 1000);
        if (!_running) return;

        var rawText = _ssbPipeline.GetLiveText();
        bool signalHeard = !string.IsNullOrWhiteSpace(rawText) && rawText != "(no speech detected)";

        RecordListenResult(spot, "SSB", signalHeard ? rawText : "", rawText, false);

        if (weStartedSsb)
            _ssbPipeline.Stop();
    }

    private void RecordListenResult(DxAlert spot, string decodeMode, string rawText, string correctedText, bool aiApplied)
    {
        var listenResult = new ListenResult(
            Callsign: spot.Callsign,
            Entity: spot.Entity,
            Band: spot.Band,
            FrequencyMHz: spot.FrequencyMHz,
            DecodeMode: decodeMode,
            RawDecode: rawText,
            AiCorrected: correctedText,
            AiApplied: aiApplied,
            ListenDurationSeconds: _listenDurationSeconds,
            SignalHeard: !string.IsNullOrWhiteSpace(rawText) && rawText != "(no speech detected)",
            ListenedUtc: DateTime.UtcNow);

        lock (_lock)
        {
            _listenResults.Add(listenResult);
            if (_listenResults.Count > MaxListenResults)
                _listenResults.RemoveAt(0);

            if (listenResult.SignalHeard)
                LogStatus($"HEARD [{decodeMode}] {spot.Callsign}: \"{correctedText.Trim()}\"");
            else
                LogStatus($"No signal [{decodeMode}] on {spot.Callsign} ({spot.FrequencyMHz:F3} MHz)");
        }
    }

    private void LogStatus(string message)
    {
        var entry = $"[{DateTime.UtcNow:HH:mm:ss}] {message}";
        _statusLog.Add(entry);
        if (_statusLog.Count > MaxStatusLog)
            _statusLog.RemoveAt(0);
        Console.Error.WriteLine($"[DXHUNTER] {message}");
    }

    /// <summary>
    /// Poll SWR meter during TUNE and return once it stabilizes below target or times out.
    /// </summary>
    private string WaitForSwrStable(int maxWaitMs, double targetSwr, int stableReadings)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int goodCount = 0;
        double lastSwr = 99.0;

        // Give the tuner a moment to start
        Thread.Sleep(500);

        while (sw.ElapsedMilliseconds < maxWaitMs && _running)
        {
            var meters = _radioManager.GetMeters();
            if (meters.TryGetValue("SWR", out var swrObj) && swrObj is double swr && swr > 0)
            {
                lastSwr = swr;
                if (swr <= targetSwr)
                {
                    goodCount++;
                    if (goodCount >= stableReadings)
                    {
                        return $"TUNE done in {sw.ElapsedMilliseconds / 1000.0:F1}s — SWR {swr:F1}:1";
                    }
                }
                else
                {
                    goodCount = 0; // reset if SWR goes back up
                }
            }

            Thread.Sleep(250); // poll every 250ms
        }

        return $"TUNE timeout ({maxWaitMs / 1000}s) — SWR {lastSwr:F1}:1";
    }

    /// <summary>
    /// Detect FT8/FT4 from mode or comment fields.
    /// </summary>
    private static bool IsFt8Mode(string mode, string comment)
    {
        if (!string.IsNullOrEmpty(mode))
        {
            if (mode.Equals("FT8", StringComparison.OrdinalIgnoreCase) ||
                mode.Equals("FT4", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        if (!string.IsNullOrEmpty(comment))
        {
            if (comment.Contains("FT8", StringComparison.OrdinalIgnoreCase) ||
                comment.Contains("FT4", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Detect FT8/FT4 from standard dial frequencies.
    /// </summary>
    private static bool IsFt8Frequency(double mhz)
    {
        // FT8 standard dial frequencies (±2 kHz window)
        ReadOnlySpan<double> ft8Freqs = [1.840, 3.573, 5.357, 7.074, 10.136, 14.074,
                                          18.100, 21.074, 24.915, 28.074, 50.313, 50.323];
        foreach (var f in ft8Freqs)
        {
            if (Math.Abs(mhz - f) < 0.003) return true;
        }
        return false;
    }

    /// <summary>
    /// Infer CW mode from frequency — below the CW/digital boundary on each band.
    /// </summary>
    private static bool IsCwFrequency(double mhz) => mhz switch
    {
        >= 1.800 and < 1.840 => true,
        >= 3.500 and < 3.570 => true,
        >= 5.330 and < 5.360 => true,
        >= 7.000 and < 7.060 => true,
        >= 10.100 and < 10.130 => true,
        >= 14.000 and < 14.070 => true,
        >= 18.068 and < 18.095 => true,
        >= 21.000 and < 21.070 => true,
        >= 24.890 and < 24.920 => true,
        >= 28.000 and < 28.070 => true,
        _ => false
    };
}

// --- Records ---

public record DxHunterState(
    bool IsRunning,
    DateTime StartedUtc,
    string? LogFile,
    int LogRecords,
    int WorkedEntityBandSlots,
    string TargetBand,
    string TargetMode,
    bool AutoTune,
    List<DxAlert> Alerts,
    int TotalAlerts,
    List<ListenResult> ListenResults,
    List<string> StatusLog);

public record ListenResult(
    string Callsign,
    string Entity,
    string Band,
    double FrequencyMHz,
    string DecodeMode,
    string RawDecode,
    string AiCorrected,
    bool AiApplied,
    int ListenDurationSeconds,
    bool SignalHeard,
    DateTime ListenedUtc);

public record DxAlert(
    string Callsign,
    string Entity,
    string Band,
    string Mode,
    double FrequencyMHz,
    string Spotter,
    string Comment,
    int Priority,
    DateTime DetectedUtc);
