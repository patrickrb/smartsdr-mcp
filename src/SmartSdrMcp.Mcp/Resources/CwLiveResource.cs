using System.ComponentModel;
using ModelContextProtocol.Server;
using SmartSdrMcp.Cw;

namespace SmartSdrMcp.Mcp.Resources;

[McpServerResourceType]
public class CwLiveResource
{
    private readonly CwPipeline _cwPipeline;

    public CwLiveResource(CwPipeline cwPipeline)
    {
        _cwPipeline = cwPipeline;
    }

    [McpServerResource(UriTemplate = "flex://cw/live", Name = "CwLive"), Description("Real-time CW decode buffer showing Morse code being decoded")]
    public string GetLiveText()
    {
        if (!_cwPipeline.IsRunning) return "(CW listener not running)";
        var text = _cwPipeline.GetLiveText();
        return string.IsNullOrWhiteSpace(text) ? "(no CW detected)" : text;
    }
}
