using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using SmartSdrMcp.Radio;

namespace SmartSdrMcp.Mcp.Resources;

[McpServerResourceType]
public class RadioStateResource
{
    private readonly RadioManager _radioManager;

    public RadioStateResource(RadioManager radioManager)
    {
        _radioManager = radioManager;
    }

    [McpServerResource(UriTemplate = "flex://radio/state", Name = "RadioState"), Description("Current FlexRadio state including frequency, mode, and TX status")]
    public string GetRadioState()
    {
        var state = _radioManager.GetState();
        return JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
    }
}
