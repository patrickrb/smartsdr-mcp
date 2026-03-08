using SmartSdrMcp.BandScout;
using SmartSdrMcp.Radio;

namespace SmartSdrMcp.DxHunter;

public class DxHunterAgent
{
    private readonly RadioManager _radioManager;
    private readonly object _lock = new();
    private readonly List<string> _statusLog = new();

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

    private Thread? _hunterThread;
    private volatile bool _running;
    private DateTime _startedUtc;
    private string _targetBand = ""; // empty = all bands
    private string _targetMode = ""; // empty = all modes
    private bool _autoTune;

    public bool IsRunning => _running;

    public DxHunterAgent(RadioManager radioManager)
    {
        _radioManager = radioManager;
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
    public string Start(string? band = null, string? mode = null, bool autoTune = false)
    {
        if (_running) return "DX Hunter is already running.";
        if (!_radioManager.IsConnected) return "Not connected to a radio.";

        if (_workedEntityBand.Count == 0)
            return "No logbook loaded. Call dx_hunter_load_log first with your ADIF file path.";

        _targetBand = band?.Trim() ?? "";
        _targetMode = mode?.Trim().ToUpperInvariant() ?? "";
        _autoTune = autoTune;
        _running = true;
        _startedUtc = DateTime.UtcNow;

        lock (_lock)
        {
            _statusLog.Clear();
            _alerts.Clear();
            _alertedCallsigns.Clear();
            var bandInfo = string.IsNullOrEmpty(_targetBand) ? "all bands" : _targetBand;
            var modeInfo = string.IsNullOrEmpty(_targetMode) ? "all modes" : _targetMode;
            LogStatus($"DX Hunter started. Watching {bandInfo}, {modeInfo}. AutoTune={autoTune}.");
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
                StatusLog: _statusLog.ToList());
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
            Thread.Sleep(PollIntervalMs);
            if (!_running) break;

            try
            {
                ScanSpots();
            }
            catch (Exception ex)
            {
                lock (_lock) { LogStatus($"Error: {ex.Message}"); }
            }
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

            // Filter by target band/mode
            if (!string.IsNullOrEmpty(_targetBand) &&
                !band.Equals(_targetBand, StringComparison.OrdinalIgnoreCase))
                continue;

            // If a target mode is set, exclude spots with missing or non-matching mode
            if (!string.IsNullOrEmpty(_targetMode) &&
                (string.IsNullOrEmpty(mode) ||
                 !mode.Equals(_targetMode, StringComparison.OrdinalIgnoreCase)))
                continue;

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

            // Auto-tune to highest priority if enabled
            if (_autoTune && newNeeds > 0)
            {
                var msg = TuneToNext();
                lock (_lock) { LogStatus($"AutoTune: {msg}"); }
            }
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
    List<string> StatusLog);

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
