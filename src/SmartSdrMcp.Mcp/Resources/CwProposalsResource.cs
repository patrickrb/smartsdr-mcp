using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using SmartSdrMcp.Tx;

namespace SmartSdrMcp.Mcp.Resources;

[McpServerResourceType]
public class CwProposalsResource
{
    private readonly TransmitController _transmitController;

    public CwProposalsResource(TransmitController transmitController)
    {
        _transmitController = transmitController;
    }

    [McpServerResource(UriTemplate = "flex://cw/proposals", Name = "CwProposals"), Description("Pending AI-generated reply proposals awaiting approval")]
    public string GetProposals()
    {
        var proposals = _transmitController.GetPendingProposals();
        return JsonSerializer.Serialize(proposals.Select(p => new
        {
            p.Id,
            p.SuggestedText,
            p.Reason,
            p.EstimatedWpm
        }), new JsonSerializerOptions { WriteIndented = true });
    }
}
