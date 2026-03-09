using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using SmartSdrMcp.DxHunter;
using SmartSdrMcp.Radio;

namespace SmartSdrMcp.Mcp.Tools;

[McpServerToolType]
public class DxHunterTools
{
    private readonly DxHunterAgent _dxHunter;
    private readonly RadioManager _radioManager;
    private readonly DxClusterService _clusterService;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public DxHunterTools(DxHunterAgent dxHunter, RadioManager radioManager, DxClusterService clusterService)
    {
        _dxHunter = dxHunter;
        _radioManager = radioManager;
        _clusterService = clusterService;
    }

    [McpServerTool, Description(
        "Load your ADIF logbook for the DX Hunter. Parses the log to determine which DXCC entities " +
        "you've already worked on each band. This must be called before starting the hunter. " +
        "Supports standard .adi ADIF files exported from any logging program (WSJT-X, N1MM, Log4OM, etc).")]
    public string DxHunterLoadLog(string filePath)
    {
        return _dxHunter.LoadLog(filePath);
    }

    [McpServerTool, Description(
        "Start the Auto DX Hunter. Watches incoming DX spots and cross-references each one against your logbook. " +
        "Alerts you when a spotted station is a DXCC entity you haven't worked on that band yet (a 'need'). " +
        "Optionally filter by band (e.g. '20m') and mode (e.g. 'CW'). " +
        "Set autoTune=true to automatically QSY to each need, listen with the CW decoder (for CW spots) " +
        "or SSB speech-to-text (for SSB/phone spots) for listenSeconds, AI-rescore if CW, " +
        "and report what was heard. This creates an active DX hunting loop.")]
    public string DxHunterStart(string? band = null, string? mode = null, bool autoTune = false, int listenSeconds = 15)
    {
        if (!_radioManager.IsConnected)
            return "Not connected to a radio. Call connect_radio first.";

        return _dxHunter.Start(band, mode, autoTune, listenSeconds);
    }

    [McpServerTool, Description("Stop the Auto DX Hunter.")]
    public string DxHunterStop()
    {
        _dxHunter.Stop();
        return "DX Hunter stopped.";
    }

    [McpServerTool, Description(
        "Get DX Hunter status and recent alerts. Shows the most recent 'needs' detected — " +
        "DXCC entities spotted that you haven't worked on that band. Each alert includes: " +
        "callsign, DXCC entity, band, frequency, spotter, and detection time. " +
        "Poll every 10-15 seconds for live updates.")]
    public string DxHunterStatus()
    {
        var status = _dxHunter.GetStatus();
        if (!status.IsRunning && status.LogRecords == 0)
            return "DX Hunter is not running and no log is loaded. " +
                   "Call dx_hunter_load_log with your ADIF file first, then dx_hunter_start.";

        return JsonSerializer.Serialize(status, JsonOptions);
    }

    [McpServerTool, Description(
        "Get all current DX needs — entities spotted but not yet worked on that band. " +
        "Returns the full alert list sorted by detection time (newest first).")]
    public string DxHunterNeeds()
    {
        var needs = _dxHunter.GetNeeds();
        if (needs.Count == 0)
            return "No new DXCC needs detected from current spots.";

        return JsonSerializer.Serialize(needs, JsonOptions);
    }

    [McpServerTool, Description(
        "Tune the radio to the next highest-priority DX need. " +
        "Picks the best unworked DXCC entity from the current alerts and QSYs to that frequency.")]
    public string DxHunterTuneNext()
    {
        if (!_radioManager.IsConnected)
            return "Not connected to a radio.";

        return _dxHunter.TuneToNext();
    }

    [McpServerTool, Description(
        "Mark a callsign as worked on the current band/mode. Use this after completing a QSO " +
        "to update the DX Hunter's needs list. Automatically resolves the DXCC entity from the callsign. " +
        "Band and mode default to the radio's current frequency and mode if not specified.")]
    public string DxHunterMarkWorked(string callsign, string? band = null, string? mode = null)
    {
        return _dxHunter.MarkWorked(callsign, band, mode);
    }

    [McpServerTool, Description(
        "Look up the DXCC entity for a callsign. Returns the entity name based on the callsign prefix.")]
    public string DxHunterLookup(string callsign)
    {
        var entity = DxccLookup.GetEntity(callsign);
        return $"{callsign.ToUpperInvariant()} → {entity}";
    }

    [McpServerTool, Description(
        "Get results from the DX Hunter's active listening sessions. When autoTune is enabled, " +
        "the hunter tunes to each DX need and listens with the CW decoder + AI rescorer. " +
        "This returns what was heard on each frequency — raw decode and AI-corrected text.")]
    public string DxHunterListenResults()
    {
        var results = _dxHunter.GetListenResults();
        if (results.Count == 0)
            return "No listen results yet. Start the DX Hunter with autoTune=true to begin active hunting.";

        return JsonSerializer.Serialize(results, JsonOptions);
    }

    // --- DX Cluster Feed ---

    [McpServerTool, Description(
        "Start the DX Cluster spot feed. Fetches DX spots from HamQTH and pushes them to the radio's panadapter. " +
        "Spots appear as colored markers — blue for normal spots, RED for DX needs (entities you haven't worked). " +
        "Optionally filter by band (e.g. '20m') and mode (e.g. 'CW'). Poll interval in seconds (default 30, minimum 15).")]
    public string DxClusterStart(string? band = null, string? mode = null, int pollSeconds = 30)
    {
        if (!_radioManager.IsConnected)
            return "Not connected to a radio. Call connect_radio first.";

        return _clusterService.Start(band, mode, pollSeconds);
    }

    [McpServerTool, Description("Stop the DX Cluster spot feed and stop pushing spots to the panadapter.")]
    public string DxClusterStop()
    {
        return _clusterService.Stop();
    }

    [McpServerTool, Description("Get DX Cluster feed status including spots pushed and recent log entries.")]
    public string DxClusterStatus()
    {
        return JsonSerializer.Serialize(_clusterService.GetStatus(), JsonOptions);
    }
}
