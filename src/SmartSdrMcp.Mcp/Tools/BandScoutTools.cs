using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using SmartSdrMcp.BandScout;
using SmartSdrMcp.Radio;

namespace SmartSdrMcp.Mcp.Tools;

[McpServerToolType]
public class BandScoutTools
{
    private readonly BandScoutMonitor _bandScout;
    private readonly RadioManager _radioManager;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public BandScoutTools(BandScoutMonitor bandScout, RadioManager radioManager)
    {
        _bandScout = bandScout;
        _radioManager = radioManager;
    }

    [McpServerTool, Description(
        "Start the Band Scout monitor. Continuously analyzes DX spots to determine which HF bands are active. " +
        "Tracks spot count, unique callsigns, modes, and recency for each band (160m through 6m). " +
        "Scores bands by activity level and recommends the best band to operate on. " +
        "Use band_scout_report to get current conditions.")]
    public string BandScoutStart()
    {
        if (!_radioManager.IsConnected)
            return "Not connected to a radio. Call connect_radio first.";

        return _bandScout.Start();
    }

    [McpServerTool, Description("Stop the Band Scout monitor.")]
    public string BandScoutStop()
    {
        _bandScout.Stop();
        return "Band Scout stopped.";
    }

    [McpServerTool, Description(
        "Get the Band Scout activity report. Shows all HF bands ranked by activity score. " +
        "Each band shows: spot count, unique callsigns, mode breakdown (CW/SSB/FT8/etc), " +
        "newest spot age, and activity score. Includes a recommendation for the best band. " +
        "Poll every 15-30 seconds for live updates.")]
    public string BandScoutReport()
    {
        if (!_bandScout.IsRunning)
            return "Band Scout is not running. Start it with band_scout_start.";

        var report = _bandScout.GetReport();
        return JsonSerializer.Serialize(report, JsonOptions);
    }
}
