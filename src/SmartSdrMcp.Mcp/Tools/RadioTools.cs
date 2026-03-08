using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using SmartSdrMcp.Audio;
using SmartSdrMcp.Cw;
using SmartSdrMcp.Qso;
using SmartSdrMcp.Radio;
using SmartSdrMcp.Ssb;

namespace SmartSdrMcp.Mcp.Tools;

[McpServerToolType]
public class RadioTools
{
    private readonly RadioManager _radioManager;
    private readonly AudioPipeline _audioPipeline;
    private readonly CwPipeline _cwPipeline;
    private readonly SsbPipeline _ssbPipeline;
    private readonly QsoTracker _qsoTracker;

    public RadioTools(
        RadioManager radioManager,
        AudioPipeline audioPipeline,
        CwPipeline cwPipeline,
        SsbPipeline ssbPipeline,
        QsoTracker qsoTracker)
    {
        _radioManager = radioManager;
        _audioPipeline = audioPipeline;
        _cwPipeline = cwPipeline;
        _ssbPipeline = ssbPipeline;
        _qsoTracker = qsoTracker;
    }

    [McpServerTool, Description("Discover and connect to a FlexRadio on the network. Optionally specify a serial number or IP address to connect to a specific radio.")]
    public string ConnectRadio(string? serial = null, string? ip = null)
    {
        _radioManager.Initialize();

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

    [McpServerTool, Description("Discover radios currently visible on the network without connecting.")]
    public string ListRadios()
    {
        _radioManager.Initialize();
        var radios = _radioManager.DiscoverRadios();
        if (radios.Count == 0)
            return "No radios discovered.";

        return JsonSerializer.Serialize(radios, new JsonSerializerOptions { WriteIndented = true });
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

    [McpServerTool, Description("Get overall radio/listener health including connection, active slice, DAX status, audio level, and last decode timestamp.")]
    public string RadioHealth()
    {
        var state = _radioManager.GetState();
        var lastSsb = _ssbPipeline.GetRecentSegments(1).LastOrDefault()?.Timestamp;
        var lastDecode = MaxTimestamp(_qsoTracker.LastMessageAtUtc, lastSsb);

        var health = new
        {
            state.Connected,
            state.RadioName,
            state.RadioModel,
            state.ActiveSlice,
            state.FrequencyMHz,
            state.Mode,
            Dax = new
            {
                _audioPipeline.IsRunning,
                Channel = _audioPipeline.CurrentDaxChannel,
                LastAudioLevelRms = _audioPipeline.LastAudioLevelRms,
                _audioPipeline.LastAudioSampleUtc
            },
            Cw = new
            {
                _cwPipeline.IsRunning,
                _cwPipeline.EstimatedWpm,
                _cwPipeline.ToneMagnitude,
                _cwPipeline.NoiseFloor,
                _cwPipeline.PeakMagnitude,
                _cwPipeline.SignalRms,
                _cwPipeline.GateOpen,
                _cwPipeline.TonePresent
            },
            Ssb = new
            {
                _ssbPipeline.IsRunning,
                LastTranscriptionUtc = lastSsb
            },
            LastDecodeUtc = lastDecode
        };

        return JsonSerializer.Serialize(health, new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool, Description("Set the active slice frequency in MHz. Example: 14.035 for 20m CW.")]
    public string SetFrequency(double frequencyMHz)
    {
        if (!_radioManager.IsConnected)
            return "Not connected to a radio.";

        bool ok = _radioManager.SetFrequency(frequencyMHz);
        return ok ? $"Frequency set to {frequencyMHz:F6} MHz" : "Failed to set frequency.";
    }

    [McpServerTool, Description("Step active slice frequency by N Hz (positive or negative).")]
    public string StepFrequency(int stepHz)
    {
        if (!_radioManager.IsConnected)
            return "Not connected to a radio.";

        bool ok = _radioManager.StepFrequency(stepHz, out var newFrequencyMHz);
        return ok
            ? $"Frequency stepped by {stepHz} Hz to {newFrequencyMHz:F6} MHz"
            : "Failed to step frequency.";
    }

    [McpServerTool, Description("Set the active slice by index.")]
    public string SetActiveSlice(int sliceIndex)
    {
        if (!_radioManager.IsConnected)
            return "Not connected to a radio.";

        bool ok = _radioManager.SetActiveSlice(sliceIndex);
        return ok ? $"Active slice set to {sliceIndex}." : $"Failed to set active slice {sliceIndex}.";
    }

    [McpServerTool, Description("Set the demodulation mode. Common modes: CW, USB, LSB, AM, FM.")]
    public string SetMode(string mode)
    {
        if (!_radioManager.IsConnected)
            return "Not connected to a radio.";

        bool ok = _radioManager.SetMode(mode);
        return ok ? $"Mode set to {mode.ToUpper()}" : "Failed to set mode.";
    }

    [McpServerTool, Description("Get the current receiver passband filter bounds (low/high Hz) for the active slice.")]
    public string GetFilter()
    {
        if (!_radioManager.IsConnected)
            return "Not connected to a radio.";

        var filter = _radioManager.GetFilter();
        if (filter == null)
            return "No active slice.";

        return JsonSerializer.Serialize(new { FilterLow = filter.Value.Low, FilterHigh = filter.Value.High }, new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool, Description("Set the receiver passband filter bounds in Hz. Example: low=200, high=1000 for narrow CW.")]
    public string SetFilter(int low, int high)
    {
        if (!_radioManager.IsConnected)
            return "Not connected to a radio.";

        bool ok = _radioManager.SetFilter(low, high);
        return ok ? $"Filter set to {low}-{high} Hz" : "No active slice.";
    }

    [McpServerTool, Description("Get real-time meter readings from the radio including S-meter, SWR, forward/reflected power, PA temperature, voltage, mic level, ALC, and compression. Values update continuously while connected.")]
    public string GetMeters()
    {
        if (!_radioManager.IsConnected)
            return "Not connected to a radio.";

        var meters = _radioManager.GetMeters();
        return JsonSerializer.Serialize(meters, new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool, Description("Set CW profile values in one operation. All params optional: wpm, pitch, breakIn, iambic.")]
    public string SetCwProfile(int? wpm = null, int? pitch = null, bool? breakIn = null, string? iambic = null)
    {
        var (success, message) = _radioManager.SetCwProfile(wpm, pitch, breakIn, iambic);
        return success ? message : $"Failed: {message}";
    }

    private static DateTime? MaxTimestamp(DateTime? a, DateTime? b)
    {
        if (a == null) return b;
        if (b == null) return a;
        return a > b ? a : b;
    }
}
