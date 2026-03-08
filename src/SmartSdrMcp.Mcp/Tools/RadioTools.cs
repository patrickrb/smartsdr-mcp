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

    [McpServerTool, Description("Set RIT (Receiver Incremental Tuning). Enable/disable and set offset in Hz.")]
    public string SetRit(bool? enabled = null, int? offsetHz = null)
    {
        if (!_radioManager.IsConnected)
            return "Not connected to a radio.";
        bool ok = _radioManager.SetRit(enabled, offsetHz);
        return ok ? $"RIT set: enabled={enabled?.ToString() ?? "unchanged"}, offset={offsetHz?.ToString() ?? "unchanged"} Hz" : "No active slice.";
    }

    [McpServerTool, Description("Set XIT (Transmitter Incremental Tuning). Enable/disable and set offset in Hz.")]
    public string SetXit(bool? enabled = null, int? offsetHz = null)
    {
        if (!_radioManager.IsConnected)
            return "Not connected to a radio.";
        bool ok = _radioManager.SetXit(enabled, offsetHz);
        return ok ? $"XIT set: enabled={enabled?.ToString() ?? "unchanged"}, offset={offsetHz?.ToString() ?? "unchanged"} Hz" : "No active slice.";
    }

    [McpServerTool, Description("List all receiver slices with their configuration: frequency, mode, filter, antenna, AGC, noise reduction, and more.")]
    public string ListSlices()
    {
        if (!_radioManager.IsConnected)
            return "Not connected to a radio.";

        var slices = _radioManager.ListSlices();
        if (slices.Count == 0)
            return "No slices available.";

        return JsonSerializer.Serialize(slices, new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool, Description("Get GPS/GNSS data from the radio: latitude, longitude, grid square, altitude, satellites, speed, frequency error.")]
    public string GetGps()
    {
        if (!_radioManager.IsConnected)
            return "Not connected to a radio.";
        var gps = _radioManager.GetGps();
        return gps == null ? "GPS data unavailable." : JsonSerializer.Serialize(gps, new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool, Description("List all active Tracking Notch Filters (TNFs) with frequency, depth, bandwidth, and permanent flag.")]
    public string ListTnfs()
    {
        if (!_radioManager.IsConnected)
            return "Not connected to a radio.";
        var tnfs = _radioManager.ListTnfs();
        return tnfs.Count == 0 ? "No TNFs active." : JsonSerializer.Serialize(tnfs, new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool, Description("Add a Tracking Notch Filter at the specified frequency in MHz to null out interference.")]
    public string AddTnf(double frequencyMHz)
    {
        if (!_radioManager.IsConnected)
            return "Not connected to a radio.";
        bool ok = _radioManager.AddTnf(frequencyMHz);
        return ok ? $"TNF added at {frequencyMHz:F6} MHz" : "Failed to add TNF.";
    }

    [McpServerTool, Description("Remove a Tracking Notch Filter by its ID.")]
    public string RemoveTnf(uint tnfId)
    {
        if (!_radioManager.IsConnected)
            return "Not connected to a radio.";
        bool ok = _radioManager.RemoveTnf(tnfId);
        return ok ? $"TNF {tnfId} removed." : $"TNF {tnfId} not found.";
    }

    [McpServerTool, Description("Get current RF power settings: transmit power, tune power, and max power level.")]
    public string GetRfPower()
    {
        if (!_radioManager.IsConnected)
            return "Not connected to a radio.";
        var power = _radioManager.GetRfPower();
        return power == null ? "Power data unavailable." : JsonSerializer.Serialize(power, new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool, Description("Set RF transmit power (0-100) and/or tune power. Example: rfPower=50 for 50W.")]
    public string SetRfPower(int? rfPower = null, int? tunePower = null)
    {
        if (!_radioManager.IsConnected)
            return "Not connected to a radio.";
        bool ok = _radioManager.SetRfPower(rfPower, tunePower);
        return ok ? $"Power set: rfPower={rfPower?.ToString() ?? "unchanged"}, tunePower={tunePower?.ToString() ?? "unchanged"}" : "Failed to set power.";
    }

    [McpServerTool, Description("Get antenna tuner (ATU) status: present, enabled, tuning, bypass, using memory.")]
    public string GetAtuStatus()
    {
        if (!_radioManager.IsConnected)
            return "Not connected to a radio.";
        var status = _radioManager.GetAtuStatus();
        return status == null ? "ATU status unavailable." : JsonSerializer.Serialize(status, new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool, Description("Initiate antenna tuner (ATU) auto-tune cycle.")]
    public string AtuTune()
    {
        if (!_radioManager.IsConnected)
            return "Not connected to a radio.";
        var (success, message) = _radioManager.AtuTune();
        return message;
    }

    [McpServerTool, Description("List all saved memory channels with name, frequency, mode, and group.")]
    public string ListMemories()
    {
        if (!_radioManager.IsConnected)
            return "Not connected to a radio.";
        var memories = _radioManager.ListMemories();
        return memories.Count == 0 ? "No memories saved." : JsonSerializer.Serialize(memories, new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool, Description("Load (recall) a saved memory channel by index, tuning the radio to that frequency and mode.")]
    public string LoadMemory(int index)
    {
        if (!_radioManager.IsConnected)
            return "Not connected to a radio.";
        bool ok = _radioManager.LoadMemory(index);
        return ok ? $"Memory {index} loaded." : $"Memory {index} not found.";
    }

    [McpServerTool, Description("Delete a saved memory channel by index.")]
    public string DeleteMemory(int index)
    {
        if (!_radioManager.IsConnected)
            return "Not connected to a radio.";
        bool ok = _radioManager.DeleteMemory(index);
        return ok ? $"Memory {index} deleted." : $"Memory {index} not found.";
    }

    [McpServerTool, Description("List all DX spots on the radio with callsign, frequency, mode, spotter, comment, and timestamp.")]
    public string ListSpots()
    {
        if (!_radioManager.IsConnected)
            return "Not connected to a radio.";
        var spots = _radioManager.ListSpots();
        return spots.Count == 0 ? "No spots available." : JsonSerializer.Serialize(spots, new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool, Description("Remove a DX spot by callsign.")]
    public string RemoveSpot(string callsign)
    {
        if (!_radioManager.IsConnected)
            return "Not connected to a radio.";
        bool ok = _radioManager.RemoveSpot(callsign);
        return ok ? $"Spot for {callsign} removed." : $"Spot for {callsign} not found.";
    }

    [McpServerTool, Description("Get AGC (Automatic Gain Control) settings for the active slice: mode (off/slow/medium/fast), threshold, off-level.")]
    public string GetAgc()
    {
        if (!_radioManager.IsConnected)
            return "Not connected to a radio.";
        var agc = _radioManager.GetAgc();
        return agc == null ? "No active slice." : JsonSerializer.Serialize(agc, new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool, Description("Set AGC mode (off, slow, medium, fast), threshold, and/or off-level for the active slice.")]
    public string SetAgc(string? mode = null, int? threshold = null, int? offLevel = null)
    {
        if (!_radioManager.IsConnected)
            return "Not connected to a radio.";
        bool ok = _radioManager.SetAgc(mode, threshold, offLevel);
        return ok ? "AGC settings updated." : "No active slice.";
    }

    [McpServerTool, Description("Get the current RX and TX antenna for the active slice, plus available antenna lists.")]
    public string GetAntenna()
    {
        if (!_radioManager.IsConnected) return "Not connected to a radio.";
        var ant = _radioManager.GetAntenna();
        return ant == null ? "No active slice." : JsonSerializer.Serialize(ant, new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool, Description("Set the RX and/or TX antenna for the active slice. Use get_antenna to see available options.")]
    public string SetAntenna(string? rxAnt = null, string? txAnt = null)
    {
        if (!_radioManager.IsConnected) return "Not connected to a radio.";
        bool ok = _radioManager.SetAntenna(rxAnt, txAnt);
        return ok ? $"Antenna set: RX={rxAnt ?? "unchanged"}, TX={txAnt ?? "unchanged"}" : "No active slice.";
    }

    [McpServerTool, Description("Get audio settings (gain, pan, mute) for the active slice.")]
    public string GetSliceAudio()
    {
        if (!_radioManager.IsConnected) return "Not connected to a radio.";
        var audio = _radioManager.GetSliceAudio();
        return audio == null ? "No active slice." : JsonSerializer.Serialize(audio, new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool, Description("Set audio gain (0-100), pan (0-100, 50=center), and/or mute for the active slice.")]
    public string SetSliceAudio(int? audioGain = null, int? audioPan = null, bool? mute = null)
    {
        if (!_radioManager.IsConnected) return "Not connected to a radio.";
        bool ok = _radioManager.SetSliceAudio(audioGain, audioPan, mute);
        return ok ? "Slice audio updated." : "No active slice.";
    }

    [McpServerTool, Description("Get TX state: MOX (PTT), TX tune, TX monitor, TX inhibit.")]
    public string GetTxState()
    {
        if (!_radioManager.IsConnected) return "Not connected to a radio.";
        var tx = _radioManager.GetTxState();
        return tx == null ? "TX state unavailable." : JsonSerializer.Serialize(tx, new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool, Description("Set TX controls: txMonitor (monitor TX audio), txInhibit (safety lockout). Use guarded transmit controls (cw_send_text, contest_voice_test) for RF transmission. MOX and TX tune are read-only here for safety.")]
    public string SetTx(bool? txMonitor = null, bool? txInhibit = null)
    {
        if (!_radioManager.IsConnected) return "Not connected to a radio.";
        bool ok = _radioManager.SetTx(null, null, txMonitor, txInhibit);
        return ok ? "TX settings updated." : "Failed to update TX settings.";
    }

    [McpServerTool, Description("Get equalizer settings for TX or RX. Select: 'tx' or 'rx'. Returns enabled state and all band levels.")]
    public string GetEqualizer(string select)
    {
        if (!_radioManager.IsConnected) return "Not connected to a radio.";
        var eq = _radioManager.GetEqualizer(select);
        return eq == null ? $"Equalizer '{select}' not found. Use 'tx' or 'rx'." : JsonSerializer.Serialize(eq, new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool, Description("Set equalizer for TX or RX. Select: 'tx' or 'rx'. Set enabled and band levels (dB): hz63, hz125, hz250, hz500, hz1000, hz2000, hz4000, hz8000.")]
    public string SetEqualizer(string select, bool? enabled = null, int? hz63 = null, int? hz125 = null, int? hz250 = null, int? hz500 = null, int? hz1000 = null, int? hz2000 = null, int? hz4000 = null, int? hz8000 = null)
    {
        if (!_radioManager.IsConnected) return "Not connected to a radio.";
        bool ok = _radioManager.SetEqualizer(select, enabled, hz63, hz125, hz250, hz500, hz1000, hz2000, hz4000, hz8000);
        return ok ? $"{select.ToUpper()} equalizer updated." : $"Equalizer '{select}' not found. Use 'tx' or 'rx'.";
    }

    [McpServerTool, Description("Get microphone settings: level, boost, bias, input source, and available input list.")]
    public string GetMic()
    {
        if (!_radioManager.IsConnected) return "Not connected to a radio.";
        var mic = _radioManager.GetMic();
        return mic == null ? "Mic data unavailable." : JsonSerializer.Serialize(mic, new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool, Description("Set microphone settings: level (0-100), boost (on/off), bias (on/off), input source. All optional.")]
    public string SetMic(int? micLevel = null, bool? micBoost = null, bool? micBias = null, string? micInput = null)
    {
        if (!_radioManager.IsConnected) return "Not connected to a radio.";
        bool ok = _radioManager.SetMic(micLevel, micBoost, micBias, micInput);
        return ok ? "Mic settings updated." : "Failed to update mic settings.";
    }

    [McpServerTool, Description("Get TX audio processing settings: compander (on/level) and speech processor (enable/level).")]
    public string GetTxAudioProcessing()
    {
        if (!_radioManager.IsConnected) return "Not connected to a radio.";
        var txAudio = _radioManager.GetTxAudioProcessing();
        return txAudio == null ? "TX audio data unavailable." : JsonSerializer.Serialize(txAudio, new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool, Description("Set TX audio processing: companderOn, companderLevel (0-100), speechProcessorEnable, speechProcessorLevel (0-100). All optional.")]
    public string SetTxAudioProcessing(bool? companderOn = null, int? companderLevel = null, bool? speechProcessorEnable = null, uint? speechProcessorLevel = null)
    {
        if (!_radioManager.IsConnected) return "Not connected to a radio.";
        bool ok = _radioManager.SetTxAudioProcessing(companderOn, companderLevel, speechProcessorEnable, speechProcessorLevel);
        return ok ? "TX audio processing updated." : "Failed to update TX audio processing.";
    }

    [McpServerTool, Description("Get VOX (Voice-Operated Transmit) settings: enabled, level, delay.")]
    public string GetVox()
    {
        if (!_radioManager.IsConnected) return "Not connected to a radio.";
        var vox = _radioManager.GetVox();
        return vox == null ? "VOX data unavailable." : JsonSerializer.Serialize(vox, new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool, Description("Set VOX settings: enabled (on/off), level (0-100), delay (ms). All optional.")]
    public string SetVox(bool? enabled = null, int? level = null, int? delay = null)
    {
        if (!_radioManager.IsConnected) return "Not connected to a radio.";
        bool ok = _radioManager.SetVox(enabled, level, delay);
        return ok ? "VOX settings updated." : "Failed to update VOX settings.";
    }

    [McpServerTool, Description("List all panadapters (spectrum displays) with center frequency, bandwidth, dBm range, FPS, and band.")]
    public string ListPanadapters()
    {
        if (!_radioManager.IsConnected) return "Not connected to a radio.";
        var pans = _radioManager.ListPanadapters();
        return pans.Count == 0 ? "No panadapters." : JsonSerializer.Serialize(pans, new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool, Description("Set panadapter display: centerFreq (MHz), bandwidth (MHz), lowDbm, highDbm, fps, average. Requires streamId in hex from list_panadapters.")]
    public string SetPanadapter(string streamId, double? centerFreq = null, double? bandwidth = null, double? lowDbm = null, double? highDbm = null, int? fps = null, int? average = null)
    {
        if (!_radioManager.IsConnected) return "Not connected to a radio.";
        bool ok = _radioManager.SetPanadapter(streamId, centerFreq, bandwidth, lowDbm, highDbm, fps, average);
        return ok ? "Panadapter updated." : "Panadapter not found. Use list_panadapters to get stream IDs.";
    }

    [McpServerTool, Description("Tune the active slice to a DX spot's frequency by callsign.")]
    public string TuneToSpot(string callsign)
    {
        if (!_radioManager.IsConnected) return "Not connected to a radio.";
        var (_, message) = _radioManager.TuneToSpot(callsign);
        return message;
    }

    [McpServerTool, Description("Set CW profile values in one operation. All params optional: wpm, pitch, breakIn, iambic.")]
    public string SetCwProfile(int? wpm = null, int? pitch = null, bool? breakIn = null, string? iambic = null)
    {
        var (success, message) = _radioManager.SetCwProfile(wpm, pitch, breakIn, iambic);
        return success ? message : $"Failed: {message}";
    }

    [McpServerTool, Description("Get noise reduction settings (NR, NB, ANF, WNB) for the active slice.")]
    public string GetNoiseReduction()
    {
        if (!_radioManager.IsConnected)
            return "Not connected to a radio.";

        var nr = _radioManager.GetNoiseReduction();
        return nr == null ? "No active slice." : JsonSerializer.Serialize(nr, new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool, Description("Set noise reduction on the active slice. All params optional: nrOn, nrLevel, nbOn, nbLevel, anfOn, anfLevel, wnbOn, wnbLevel.")]
    public string SetNoiseReduction(bool? nrOn = null, int? nrLevel = null, bool? nbOn = null, int? nbLevel = null, bool? anfOn = null, int? anfLevel = null, bool? wnbOn = null, int? wnbLevel = null)
    {
        if (!_radioManager.IsConnected)
            return "Not connected to a radio.";

        bool ok = _radioManager.SetNoiseReduction(nrOn, nrLevel, nbOn, nbLevel, anfOn, anfLevel, wnbOn, wnbLevel);
        return ok ? "Noise reduction settings updated." : "No active slice.";
    }

    private static DateTime? MaxTimestamp(DateTime? a, DateTime? b)
    {
        if (a == null) return b;
        if (b == null) return a;
        return a > b ? a : b;
    }
}
