using SmartSdrMcp.Radio;

namespace SmartSdrMcp.BandScout;

public class BandScoutMonitor
{
    private readonly RadioManager _radioManager;
    private readonly object _lock = new();
    private readonly List<string> _statusLog = new();
    private readonly Dictionary<string, BandActivity> _bandActivity = new();

    private const int MaxStatusLog = 50;
    private const int PollIntervalMs = 15_000; // 15 seconds

    private Thread? _monitorThread;
    private volatile bool _running;
    private DateTime _startedUtc;

    // Band definitions: name → (low MHz, high MHz)
    private static readonly Dictionary<string, (double Low, double High)> HfBands = new()
    {
        ["160m"] = (1.800, 2.000),
        ["80m"] = (3.500, 4.000),
        ["60m"] = (5.330, 5.410),
        ["40m"] = (7.000, 7.300),
        ["30m"] = (10.100, 10.150),
        ["20m"] = (14.000, 14.350),
        ["17m"] = (18.068, 18.168),
        ["15m"] = (21.000, 21.450),
        ["12m"] = (24.890, 24.990),
        ["10m"] = (28.000, 29.700),
        ["6m"] = (50.000, 54.000),
    };

    public bool IsRunning => _running;

    public BandScoutMonitor(RadioManager radioManager)
    {
        _radioManager = radioManager;
    }

    public string Start()
    {
        if (_running) return "Band Scout is already running.";
        if (!_radioManager.IsConnected) return "Not connected to a radio.";

        _running = true;
        _startedUtc = DateTime.UtcNow;

        lock (_lock)
        {
            _statusLog.Clear();
            _bandActivity.Clear();
            LogStatus("Band Scout started. Monitoring spots for band activity...");
        }

        // Do an immediate scan
        ScanBands();

        _monitorThread = new Thread(MonitorLoop)
        {
            IsBackground = true,
            Name = "BandScout"
        };
        _monitorThread.Start();

        return "Band Scout started. Use band_scout_report to see activity.";
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _monitorThread?.Join(5000);
        _monitorThread = null;
        lock (_lock) { LogStatus("Band Scout stopped."); }
    }

    public BandScoutReport GetReport()
    {
        lock (_lock)
        {
            var bands = _bandActivity.Values
                .OrderByDescending(b => b.Score)
                .ToList();

            var bestBand = bands.FirstOrDefault();

            return new BandScoutReport(
                IsRunning: _running,
                StartedUtc: _startedUtc,
                LastScanUtc: bands.FirstOrDefault()?.LastScanUtc ?? DateTime.MinValue,
                Bands: bands,
                Recommendation: bestBand != null
                    ? $"Best band: {bestBand.Band} — {bestBand.SpotCount} spots, {bestBand.UniqueCallsigns} unique stations"
                    : "No activity detected yet.",
                StatusLog: _statusLog.ToList());
        }
    }

    private void MonitorLoop()
    {
        while (_running)
        {
            Thread.Sleep(PollIntervalMs);
            if (!_running) break;

            try
            {
                ScanBands();
            }
            catch (Exception ex)
            {
                lock (_lock) { LogStatus($"Error: {ex.Message}"); }
            }
        }
    }

    private void ScanBands()
    {
        if (!_radioManager.IsConnected) return;

        var spots = _radioManager.ListSpots();
        var now = DateTime.UtcNow;
        var newActivity = new Dictionary<string, BandActivity>();

        // Single-pass: materialize spots and cache PropertyInfo to avoid O(bands × spots) reflection
        var allSpotInfos = new List<SpotInfo>(spots.Count);
        if (spots.Count > 0)
        {
            var spotType = spots[0].GetType();
            var freqProp = spotType.GetProperty("FrequencyMHz");
            var callsignProp = spotType.GetProperty("Callsign");
            var modeProp = spotType.GetProperty("Mode");
            var spotterProp = spotType.GetProperty("SpotterCallsign");
            var commentProp = spotType.GetProperty("Comment");
            var timestampProp = spotType.GetProperty("Timestamp");

            foreach (var spotObj in spots)
            {
                var freq = (double)(freqProp?.GetValue(spotObj) ?? 0.0);
                var callsign = callsignProp?.GetValue(spotObj)?.ToString() ?? "";
                var mode = modeProp?.GetValue(spotObj)?.ToString() ?? "";
                var spotter = spotterProp?.GetValue(spotObj)?.ToString() ?? "";
                var comment = commentProp?.GetValue(spotObj)?.ToString() ?? "";
                var tsObj = timestampProp?.GetValue(spotObj);
                var timestamp = tsObj is DateTime dt ? dt : (tsObj as DateTime?) ?? DateTime.MinValue;

                allSpotInfos.Add(new SpotInfo(callsign, freq, mode, spotter, comment, timestamp));
            }
        }

        foreach (var (bandName, (low, high)) in HfBands)
        {
            var bandSpots = new List<SpotInfo>();

            foreach (var spot in allSpotInfos)
            {
                if (spot.FrequencyMHz >= low && spot.FrequencyMHz <= high)
                    bandSpots.Add(spot);
            }

            var uniqueCallsigns = bandSpots.Select(s => s.Callsign.ToUpperInvariant()).Distinct().Count();
            var modeCounts = bandSpots
                .GroupBy(s => string.IsNullOrEmpty(s.Mode) ? "Unknown" : s.Mode.ToUpperInvariant())
                .ToDictionary(g => g.Key, g => g.Count());
            var newestSpot = bandSpots.MaxBy(s => s.Timestamp);
            var ageMinutes = newestSpot != null ? (now - newestSpot.Timestamp).TotalMinutes : double.MaxValue;

            // Score: more spots + more unique calls + recency bonus
            double score = bandSpots.Count * 2.0
                         + uniqueCallsigns * 3.0
                         + (ageMinutes < 5 ? 10 : ageMinutes < 15 ? 5 : ageMinutes < 30 ? 2 : 0);

            newActivity[bandName] = new BandActivity(
                Band: bandName,
                LowMHz: low,
                HighMHz: high,
                SpotCount: bandSpots.Count,
                UniqueCallsigns: uniqueCallsigns,
                ModeCounts: modeCounts,
                NewestSpot: newestSpot,
                AgeMinutes: ageMinutes == double.MaxValue ? -1 : Math.Round(ageMinutes, 1),
                Score: Math.Round(score, 1),
                LastScanUtc: now);
        }

        lock (_lock)
        {
            _bandActivity.Clear();
            foreach (var kvp in newActivity)
                _bandActivity[kvp.Key] = kvp.Value;

            var activeBands = newActivity.Values
                .Where(b => b.SpotCount > 0)
                .OrderByDescending(b => b.Score)
                .ToList();

            if (activeBands.Count > 0)
            {
                var summary = string.Join(", ", activeBands.Take(5)
                    .Select(b => $"{b.Band}:{b.SpotCount}spots"));
                LogStatus($"Scan: {summary} (total {spots.Count} spots)");
            }
            else
            {
                LogStatus($"Scan: No spots on HF bands (total {spots.Count} spots)");
            }
        }
    }

    // Read S-meter for the current band
    public double? GetCurrentSMeter()
    {
        var meters = _radioManager.GetMeters();
        // Find any S-meter reading
        foreach (var kvp in meters)
        {
            if (kvp.Key.StartsWith("S-METER") && kvp.Value is double val)
                return val;
        }
        return null;
    }

    public static string FrequencyToBand(double freqMHz)
    {
        foreach (var (name, (low, high)) in HfBands)
        {
            if (freqMHz >= low && freqMHz <= high)
                return name;
        }
        return "OOB";
    }

    private void LogStatus(string message)
    {
        var entry = $"[{DateTime.UtcNow:HH:mm:ss}] {message}";
        _statusLog.Add(entry);
        if (_statusLog.Count > MaxStatusLog)
            _statusLog.RemoveAt(0);
        Console.Error.WriteLine($"[BANDSCOUT] {message}");
    }
}

// --- Records ---

public record BandScoutReport(
    bool IsRunning,
    DateTime StartedUtc,
    DateTime LastScanUtc,
    List<BandActivity> Bands,
    string Recommendation,
    List<string> StatusLog);

public record BandActivity(
    string Band,
    double LowMHz,
    double HighMHz,
    int SpotCount,
    int UniqueCallsigns,
    Dictionary<string, int> ModeCounts,
    SpotInfo? NewestSpot,
    double AgeMinutes,
    double Score,
    DateTime LastScanUtc);

public record SpotInfo(
    string Callsign,
    double FrequencyMHz,
    string Mode,
    string Spotter,
    string Comment,
    DateTime Timestamp);
