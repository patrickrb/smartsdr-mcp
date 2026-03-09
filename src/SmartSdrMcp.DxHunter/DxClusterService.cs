using System.Globalization;
using SmartSdrMcp.BandScout;
using SmartSdrMcp.Radio;

namespace SmartSdrMcp.DxHunter;

/// <summary>
/// Polls a DX cluster via HamQTH HTTP API and pushes spots to the radio's panadapter.
/// Integrates with DxHunterAgent to highlight "needs" in a different color.
/// </summary>
public class DxClusterService : IDisposable
{
    private readonly RadioManager _radioManager;
    private readonly DxHunterAgent? _dxHunter;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private const string BaseUrl = "https://www.hamqth.com/dxc_csv.php";

    private Timer? _pollTimer;
    private volatile bool _running;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _pollGuard = new(1, 1); // prevent overlapping polls
    private readonly HashSet<string> _pushedSpots = new(StringComparer.OrdinalIgnoreCase); // "CALL|FREQ" dedup
    private readonly List<string> _statusLog = new();

    // Configuration
    public int PollIntervalSeconds { get; set; } = 30;
    public string? BandFilter { get; set; }
    public string? ModeFilter { get; set; }
    public int SpotLifetimeSeconds { get; set; } = 600; // 10 minutes
    public int MaxSpots { get; set; } = 50;

    // Colors: AARRGGBB hex format for FlexRadio
    public string NormalSpotColor { get; set; } = "#FF00BFFF";    // Deep sky blue
    public string NeedSpotColor { get; set; } = "#FFFF4444";      // Red — DX need!
    public string NeedSpotBgColor { get; set; } = "#44FF0000";    // Semi-transparent red bg

    public bool IsRunning => _running;

    public DxClusterService(RadioManager radioManager, DxHunterAgent? dxHunter = null)
    {
        _radioManager = radioManager;
        _dxHunter = dxHunter;
    }

    public string Start(string? band = null, string? mode = null, int pollSeconds = 30)
    {
        if (_running) return "DX Cluster feed is already running.";
        if (!_radioManager.IsConnected) return "Radio not connected.";

        BandFilter = band;
        ModeFilter = mode;
        PollIntervalSeconds = Math.Max(15, pollSeconds); // minimum 15s to be polite

        _running = true;
        lock (_lock)
        {
            _pushedSpots.Clear();
            _statusLog.Clear();
            _statusLog.Add($"[{DateTime.UtcNow:HH:mm:ss}] DX Cluster started. Band={band ?? "all"}, Mode={mode ?? "all"}, Poll={PollIntervalSeconds}s");
        }

        // Poll immediately, then on timer
        _ = PollAndPushSpots();
        _pollTimer = new Timer(_ => _ = PollAndPushSpots(), null,
            TimeSpan.FromSeconds(PollIntervalSeconds),
            TimeSpan.FromSeconds(PollIntervalSeconds));

        return $"DX Cluster feed started. Polling every {PollIntervalSeconds}s. Spots will appear on the panadapter.";
    }

    public string Stop()
    {
        _pollTimer?.Dispose();
        _pollTimer = null;
        _running = false;
        LogStatus("DX Cluster feed stopped.");
        return "DX Cluster feed stopped.";
    }

    public object GetStatus()
    {
        lock (_lock)
        {
            return new
            {
                IsRunning = _running,
                BandFilter,
                ModeFilter,
                PollIntervalSeconds,
                SpotsPushed = _pushedSpots.Count,
                StatusLog = _statusLog.TakeLast(20).ToList()
            };
        }
    }

    private async Task PollAndPushSpots()
    {
        if (!_running) return;
        if (!_pollGuard.Wait(0)) return; // skip if another poll is already running
        try
        {
            var spots = await FetchSpots();
            if (!_running) return; // re-check after async fetch
            int pushed = 0;
            int needCount = 0;

            foreach (var spot in spots)
            {
                if (!_running) break;

                var key = $"{spot.DxCall}|{spot.FrequencyKhz:F1}";
                lock (_lock)
                {
                    if (_pushedSpots.Contains(key)) continue;
                    if (_pushedSpots.Count >= MaxSpots * 2) // cleanup old entries
                    {
                        _pushedSpots.Clear();
                    }
                    _pushedSpots.Add(key);
                }

                var freqMHz = spot.FrequencyKhz / 1000.0;
                bool isNeed = IsNeed(spot.DxCall, spot.FrequencyKhz);

                string color = isNeed ? NeedSpotColor : NormalSpotColor;
                string? bgColor = isNeed ? NeedSpotBgColor : null;
                string comment = spot.Comment ?? "";
                if (isNeed) comment = "** DX NEED ** " + comment;

                // Use the actual mode if available, or infer from comment/filter
                string? spotMode = spot.Mode;
                if (string.IsNullOrEmpty(spotMode) || spotMode.Length <= 2)
                {
                    // HamQTH puts continent codes in mode field; check comment for real mode
                    if (spot.Comment != null && spot.Comment.Contains("CW", StringComparison.OrdinalIgnoreCase))
                        spotMode = "CW";
                    else if (spot.Comment != null && spot.Comment.Contains("FT8", StringComparison.OrdinalIgnoreCase))
                        spotMode = "FT8";
                    else if (spot.Comment != null && spot.Comment.Contains("SSB", StringComparison.OrdinalIgnoreCase))
                        spotMode = "SSB";
                    else
                        spotMode = ModeFilter;
                }

                bool spotPushed = _radioManager.AddSpot(
                    callsign: spot.DxCall,
                    frequencyMHz: freqMHz,
                    mode: spotMode,
                    color: color,
                    backgroundColor: bgColor,
                    spotterCallsign: spot.Spotter,
                    source: "SmartSdrMcp-Cluster",
                    comment: comment,
                    lifetimeSeconds: SpotLifetimeSeconds);

                if (spotPushed)
                {
                    pushed++;
                    if (isNeed) needCount++;
                }
            }

            if (pushed > 0)
                LogStatus($"Pushed {pushed} spots ({needCount} needs)");
        }
        catch (Exception ex)
        {
            LogStatus($"Poll error: {ex.Message}");
        }
        finally
        {
            _pollGuard.Release();
        }
    }

    private bool IsNeed(string callsign, double frequencyKhz)
    {
        if (_dxHunter == null || !_dxHunter.IsRunning) return false;

        // Infer band from frequency for accurate matching
        var band = BandScout.BandScoutMonitor.FrequencyToBand(frequencyKhz / 1000.0);

        // Check if the DX hunter considers this a need on this specific band
        var needs = _dxHunter.GetNeeds();
        return needs.Any(n => string.Equals(n.Callsign, callsign, StringComparison.OrdinalIgnoreCase)
            && (band == "OOB" || string.Equals(n.Band, band, StringComparison.OrdinalIgnoreCase)));
    }

    private async Task<List<ClusterSpot>> FetchSpots()
    {
        var url = $"{BaseUrl}?limit={MaxSpots}";
        if (BandFilter != null)
            url += $"&band={BandFilter}";
        if (ModeFilter != null)
            url += $"&mode={ModeFilter}";

        var response = await _http.GetStringAsync(url);
        return ParseSpots(response);
    }

    private static List<ClusterSpot> ParseSpots(string csv)
    {
        var spots = new List<ClusterSpot>();

        foreach (var line in csv.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split('^');
            if (fields.Length < 5) continue;

            if (!double.TryParse(fields[1], CultureInfo.InvariantCulture, out var freq)) continue;

            var call = fields[2].Trim();
            if (string.IsNullOrEmpty(call)) continue;

            DateTime spotted = DateTime.UtcNow;
            var dtParts = fields[4].Trim().Split(' ');
            if (dtParts.Length == 2 && dtParts[0].Length == 4)
            {
                if (DateTime.TryParse(dtParts[1], CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date) &&
                    int.TryParse(dtParts[0][..2], out var hour) &&
                    int.TryParse(dtParts[0][2..], out var min))
                {
                    spotted = new DateTime(date.Year, date.Month, date.Day, hour, min, 0, DateTimeKind.Utc);
                }
            }

            string? mode = fields.Length > 7 ? fields[7].Trim() : null;
            if (string.IsNullOrEmpty(mode)) mode = null;

            spots.Add(new ClusterSpot(
                Spotter: fields[0].Trim(),
                FrequencyKhz: freq,
                DxCall: call,
                Comment: fields.Length > 3 ? fields[3].Trim() : null,
                SpottedUtc: spotted,
                Band: fields.Length > 8 ? fields[8].Trim() : null,
                Mode: mode,
                Country: fields.Length > 9 ? fields[9].Trim() : null));
        }

        return spots;
    }

    private void LogStatus(string message)
    {
        lock (_lock)
        {
            _statusLog.Add($"[{DateTime.UtcNow:HH:mm:ss}] {message}");
            while (_statusLog.Count > 100) _statusLog.RemoveAt(0);
        }
    }

    public void Dispose()
    {
        Stop();
        _http.Dispose();
    }
}

public record ClusterSpot(
    string Spotter,
    double FrequencyKhz,
    string DxCall,
    string? Comment,
    DateTime SpottedUtc,
    string? Band,
    string? Mode,
    string? Country);
