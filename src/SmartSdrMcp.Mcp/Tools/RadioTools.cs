using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using SmartSdrMcp.Radio;

namespace SmartSdrMcp.Mcp.Tools;

[McpServerToolType]
public class RadioTools
{
    private readonly RadioManager _radioManager;

    public RadioTools(RadioManager radioManager)
    {
        _radioManager = radioManager;
    }

    [McpServerTool, Description("Discover and connect to a FlexRadio on the network. Optionally specify a serial number or IP address to connect to a specific radio.")]
    public string ConnectRadio(string? serial = null, string? ip = null)
    {
        _radioManager.Initialize();

        // Wait for discovery with retries
        List<RadioInfo> radios = [];
        for (int i = 0; i < 5; i++)
        {
            Thread.Sleep(2000);
            radios = _radioManager.DiscoverRadios();
            if (radios.Count > 0) break;
        }

        if (radios.Count == 0)
            return "No radios found on the network. Ensure the radio is powered on and on the same subnet.";

        bool connected = _radioManager.Connect(serial, ip);
        if (!connected)
            return $"Failed to connect. Available radios: {JsonSerializer.Serialize(radios)}";

        var state = _radioManager.GetState();
        return $"Connected to {state.RadioName} ({state.RadioModel}), serial {state.Serial}. " +
               $"Frequency: {state.FrequencyMHz:F6} MHz, Mode: {state.Mode}";
    }

    [McpServerTool, Description("Disconnect from the currently connected FlexRadio.")]
    public string DisconnectRadio()
    {
        _radioManager.Disconnect();
        return "Disconnected from radio.";
    }

    [McpServerTool, Description("Get the current state of the connected FlexRadio including frequency, mode, and TX state.")]
    public string GetRadioState()
    {
        var state = _radioManager.GetState();
        return JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool, Description("Set the active slice frequency in MHz. Example: 14.035 for 20m CW.")]
    public string SetFrequency(double frequencyMHz)
    {
        if (!_radioManager.IsConnected)
            return "Not connected to a radio.";

        bool ok = _radioManager.SetFrequency(frequencyMHz);
        return ok ? $"Frequency set to {frequencyMHz:F6} MHz" : "Failed to set frequency.";
    }

    [McpServerTool, Description("Set the demodulation mode. Common modes: CW, USB, LSB, AM, FM.")]
    public string SetMode(string mode)
    {
        if (!_radioManager.IsConnected)
            return "Not connected to a radio.";

        bool ok = _radioManager.SetMode(mode);
        return ok ? $"Mode set to {mode.ToUpper()}" : "Failed to set mode.";
    }
}
