using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using SmartSdrMcp.CqCaller;

namespace SmartSdrMcp.Mcp.Tools;

[McpServerToolType]
public class CqCallerTools
{
    private readonly CqCallerAgent _cqCaller;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public CqCallerTools(CqCallerAgent cqCaller)
    {
        _cqCaller = cqCaller;
    }

    [McpServerTool, Description(
        "Start the autonomous CQ Caller agent. Supports voice (SSB) and CW modes. " +
        "Voice mode uses TTS for transmit and Whisper speech-to-text for receive. " +
        "CW mode uses CW keyer for transmit and DSP decoder for receive. " +
        "The agent automatically looks up the operator's FCC license class (or accepts it manually), " +
        "enforces band privileges for the license class, scans for a clear frequency within the allowed range, " +
        "then runs the full QSO cycle: CQ → listen → exchange → confirm → TU CQ. " +
        "For pileups, it sends partial callsigns to single out one station (max 3 attempts before re-CQing). " +
        "Requires TX guard to be armed. Audio and decode pipelines are started automatically.")]
    public string CqCallerStart(
        string callsign, string name, string qth,
        string mode = "voice", int wpm = 20, int daxChannel = 1, string? licenseClass = null)
    {
        return _cqCaller.Start(callsign, name, qth, mode, wpm, daxChannel, licenseClass);
    }

    [McpServerTool, Description("Stop the CQ Caller agent and abort any in-progress transmission.")]
    public string CqCallerStop()
    {
        _cqCaller.Stop();
        return "CQ Caller stopped.";
    }

    [McpServerTool, Description(
        "Get CQ Caller agent status including current stage, mode (voice/CW), license class, " +
        "QSOs completed, CQs sent, current caller being worked, pileup state, and recent activity log. " +
        "Poll every 2-3 seconds for live updates during operation.")]
    public string CqCallerStatus()
    {
        var status = _cqCaller.GetStatus();
        return JsonSerializer.Serialize(status, JsonOptions);
    }

    [McpServerTool, Description(
        "Get the CQ Caller's QSO log. Returns completed QSOs in JSON or ADIF format. " +
        "Use format='adif' for standard ADIF that can be imported into logging software. " +
        "The MODE field reflects the actual mode used (CW or SSB).")]
    public string CqCallerLog(string format = "json")
    {
        string normalized = (format ?? "json").Trim().ToLowerInvariant();

        if (normalized == "adif")
            return _cqCaller.ExportAdif();

        var log = _cqCaller.GetLog();
        if (log.Count == 0)
            return "No QSOs logged yet.";

        return JsonSerializer.Serialize(log, JsonOptions);
    }

    [McpServerTool, Description(
        "Export the CQ Caller's QSO log to an ADIF file on disk. " +
        "The file path must be under your home directory and use .adi or .adif extension. " +
        "The exported file can be imported into any logging program (Log4OM, N1MM, etc).")]
    public string CqCallerExport(string filePath)
    {
        return _cqCaller.ExportAdifToFile(filePath);
    }
}
