using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using SmartSdrMcp.Qso;

namespace SmartSdrMcp.Mcp.Resources;

[McpServerResourceType]
public class CwRecentResource
{
    private readonly QsoTracker _qsoTracker;

    public CwRecentResource(QsoTracker qsoTracker)
    {
        _qsoTracker = qsoTracker;
    }

    [McpServerResource(UriTemplate = "flex://cw/recent", Name = "CwRecent"), Description("Recently decoded CW messages with callsigns and CQ detection")]
    public string GetRecentMessages()
    {
        var messages = _qsoTracker.GetRecentMessages(20);
        return JsonSerializer.Serialize(messages, new JsonSerializerOptions { WriteIndented = true });
    }
}
