using System.Collections.Concurrent;
using Flex.Smoothlake.FlexLib;
using System.Globalization;

namespace SmartSdrMcp.Radio;

public class RadioManager : IDisposable
{
    private Flex.Smoothlake.FlexLib.Radio? _radio;
    private bool _initialized;
    private readonly object _lock = new();
    private readonly ConcurrentDictionary<string, float> _meterValues = new();

    public event Action<Flex.Smoothlake.FlexLib.Radio>? RadioDiscovered;
    public event Action? StateChanged;

    public bool IsConnected => _radio?.Connected ?? false;
    public Flex.Smoothlake.FlexLib.Radio? Radio => _radio;

    public void Initialize()
    {
        if (_initialized) return;
        API.ProgramName = "SmartSdrMcp";
        API.IsGUI = false;
        API.RadioAdded += OnRadioAdded;
        API.Init();
        _initialized = true;
    }

    public List<RadioInfo> DiscoverRadios()
    {
        if (!_initialized) Initialize();

        return API.RadioList
            .Select(r => new RadioInfo(
                r.Nickname ?? r.Model,
                r.Model,
                r.Serial,
                r.IP?.ToString() ?? "unknown",
                r.Status))
            .ToList();
    }

    public bool Connect(string? serial = null, string? ip = null)
    {
        if (!_initialized) Initialize();

        Flex.Smoothlake.FlexLib.Radio? radio;
        if (ip != null)
            radio = API.RadioList.FirstOrDefault(r => r.IP?.ToString() == ip);
        else if (serial != null)
            radio = API.RadioList.FirstOrDefault(r => r.Serial == serial);
        else
            radio = API.RadioList.FirstOrDefault();

        if (radio == null) return false;

        lock (_lock)
        {
            _radio?.Disconnect();
            _radio = radio;
        }

        _radio.PropertyChanged += (_, _) => StateChanged?.Invoke();

        // Subscribe to GUI client updates so we can bind once a GUI client is available
        _radio.GUIClientAdded += OnGuiClientAdded;

        // Subscribe to meter events for caching
        SubscribeMeterEvents(_radio);

        return _radio.Connect();
    }

    private void OnGuiClientAdded(GUIClient client)
    {
        if (_radio == null || string.IsNullOrEmpty(client.ClientID)) return;

        // Bind to the first GUI client we see (SmartSDR)
        _radio.GUIClientAdded -= OnGuiClientAdded;
        _radio.BoundClientID = client.ClientID;
    }

    public void Disconnect()
    {
        lock (_lock)
        {
            _radio?.Disconnect();
            _radio = null;
        }
        StateChanged?.Invoke();
    }

    public RadioState GetState()
    {
        if (_radio == null || !_radio.Connected)
        {
            return new RadioState("N/A", "N/A", false, null, 0, "N/A", false, null);
        }

        var activeSlice = _radio.SliceList.FirstOrDefault(s => s.Active)
                          ?? _radio.SliceList.FirstOrDefault();

        return new RadioState(
            RadioName: _radio.Nickname ?? _radio.Model,
            RadioModel: _radio.Model,
            Connected: _radio.Connected,
            ActiveSlice: activeSlice?.Index.ToString(),
            FrequencyMHz: activeSlice?.Freq ?? 0,
            Mode: activeSlice?.DemodMode ?? "N/A",
            IsTransmitting: _radio.Mox,
            Serial: _radio.Serial,
            CwPitch: _radio.CWPitch > 0 ? _radio.CWPitch : 600);
    }

    public Slice? GetActiveSlice()
    {
        if (_radio == null) return null;
        return _radio.SliceList.FirstOrDefault(s => s.Active)
               ?? _radio.SliceList.FirstOrDefault();
    }

    public bool SetFrequency(double mhz)
    {
        var slice = GetActiveSlice();
        if (slice == null) return false;
        slice.Freq = mhz;
        return true;
    }

    private static readonly HashSet<string> ValidModes = new(StringComparer.OrdinalIgnoreCase)
        { "CW", "USB", "LSB", "AM", "SAM", "FM", "NFM", "DFM", "DIGU", "DIGL", "FDV", "RAW" };

    public bool SetMode(string mode)
    {
        var slice = GetActiveSlice();
        if (slice == null) return false;
        if (!ValidModes.Contains(mode))
            return false;
        slice.DemodMode = mode.ToUpperInvariant();
        return true;
    }

    public (int Low, int High)? GetFilter()
    {
        var slice = GetActiveSlice();
        if (slice == null) return null;
        return (slice.FilterLow, slice.FilterHigh);
    }

    public bool SetFilter(int low, int high)
    {
        var slice = GetActiveSlice();
        if (slice == null) return false;
        slice.UpdateFilter(low, high);
        return true;
    }

    public bool SetRit(bool? enabled, int? offsetHz)
    {
        var slice = GetActiveSlice();
        if (slice == null) return false;
        if (enabled.HasValue) slice.RITOn = enabled.Value;
        if (offsetHz.HasValue) slice.RITFreq = offsetHz.Value;
        return true;
    }

    public bool SetXit(bool? enabled, int? offsetHz)
    {
        var slice = GetActiveSlice();
        if (slice == null) return false;
        if (enabled.HasValue) slice.XITOn = enabled.Value;
        if (offsetHz.HasValue) slice.XITFreq = offsetHz.Value;
        return true;
    }

    public bool SetActiveSlice(int sliceIndex)
    {
        var radio = _radio;
        if (radio == null || !radio.Connected) return false;

        var target = radio.SliceList.FirstOrDefault(s => s.Index == sliceIndex);
        if (target == null) return false;

        target.Active = true;
        return true;
    }

    public bool StepFrequency(int stepHz, out double newFrequencyMHz)
    {
        newFrequencyMHz = 0;
        var slice = GetActiveSlice();
        if (slice == null) return false;

        newFrequencyMHz = slice.Freq + (stepHz / 1_000_000.0);
        slice.Freq = newFrequencyMHz;
        return true;
    }

    public (bool Success, string Message) SetCwProfile(int? wpm, int? pitch, bool? breakIn, string? iambic)
    {
        var radio = _radio;
        if (radio == null || !radio.Connected)
            return (false, "Radio not connected");

        var cwx = radio.GetCWX();
        if (wpm.HasValue && wpm.Value > 0)
            cwx.Speed = wpm.Value;

        if (pitch.HasValue && pitch.Value > 0)
            radio.CWPitch = pitch.Value;

        if (breakIn.HasValue)
            TrySetBoolProperty(radio, new[] { "BreakInEnabled", "CWBreakInEnabled", "QskEnabled" }, breakIn.Value);

        if (!string.IsNullOrWhiteSpace(iambic))
            TrySetStringProperty(radio, new[] { "IambicMode", "KeyerMode", "CWKeyerMode" }, iambic!);

        string msg = string.Format(
            CultureInfo.InvariantCulture,
            "CW profile set: wpm={0}, pitch={1}, breakIn={2}, iambic={3}",
            wpm?.ToString(CultureInfo.InvariantCulture) ?? "unchanged",
            pitch?.ToString(CultureInfo.InvariantCulture) ?? "unchanged",
            breakIn?.ToString() ?? "unchanged",
            string.IsNullOrWhiteSpace(iambic) ? "unchanged" : iambic);

        return (true, msg);
    }

    private static bool TrySetBoolProperty(object target, IEnumerable<string> propertyNames, bool value)
    {
        foreach (var name in propertyNames)
        {
            var prop = target.GetType().GetProperty(name);
            if (prop?.CanWrite == true && prop.PropertyType == typeof(bool))
            {
                prop.SetValue(target, value);
                return true;
            }
        }

        return false;
    }

    private static bool TrySetStringProperty(object target, IEnumerable<string> propertyNames, string value)
    {
        foreach (var name in propertyNames)
        {
            var prop = target.GetType().GetProperty(name);
            if (prop?.CanWrite != true) continue;

            if (prop.PropertyType == typeof(string))
            {
                prop.SetValue(target, value);
                return true;
            }

            if (prop.PropertyType.IsEnum && Enum.TryParse(prop.PropertyType, value, true, out var enumValue))
            {
                prop.SetValue(target, enumValue);
                return true;
            }
        }

        return false;
    }

    public List<object> ListSlices()
    {
        var radio = _radio;
        if (radio == null || !radio.Connected) return [];

        return radio.SliceList.Select(s => (object)new
        {
            s.Index,
            s.Letter,
            s.Active,
            FrequencyMHz = s.Freq,
            Mode = s.DemodMode,
            s.FilterLow,
            s.FilterHigh,
            s.DAXChannel,
            s.RXAnt,
            s.TXAnt,
            s.AGCMode,
            s.AudioGain,
            s.AudioPan,
            s.Mute,
            s.NROn,
            s.NBOn,
            s.ANFOn,
            s.IsTransmitSlice,
            s.RITOn,
            s.RITFreq,
            s.XITOn,
            s.XITFreq
        }).ToList();
    }

    public bool SetNoiseReduction(bool? nrOn = null, int? nrLevel = null, bool? nbOn = null, int? nbLevel = null, bool? anfOn = null, int? anfLevel = null, bool? wnbOn = null, int? wnbLevel = null)
    {
        var slice = GetActiveSlice();
        if (slice == null) return false;
        if (nrOn.HasValue) slice.NROn = nrOn.Value;
        if (nrLevel.HasValue) slice.NRLevel = nrLevel.Value;
        if (nbOn.HasValue) slice.NBOn = nbOn.Value;
        if (nbLevel.HasValue) slice.NBLevel = nbLevel.Value;
        if (anfOn.HasValue) slice.ANFOn = anfOn.Value;
        if (anfLevel.HasValue) slice.ANFLevel = anfLevel.Value;
        if (wnbOn.HasValue) slice.WNBOn = wnbOn.Value;
        if (wnbLevel.HasValue) slice.WNBLevel = wnbLevel.Value;
        return true;
    }

    public object? GetNoiseReduction()
    {
        var slice = GetActiveSlice();
        if (slice == null) return null;
        return new
        {
            NR = new { slice.NROn, slice.NRLevel },
            NB = new { slice.NBOn, slice.NBLevel },
            ANF = new { slice.ANFOn, slice.ANFLevel },
            WNB = new { slice.WNBOn, slice.WNBLevel }
        };
    }

    // --- GPS (#9) ---

    public object? GetGps()
    {
        var radio = _radio;
        if (radio == null || !radio.Connected) return null;
        return new
        {
            radio.GPSInstalled,
            radio.GPSLatitude,
            radio.GPSLongitude,
            radio.GPSGrid,
            radio.GPSAltitude,
            radio.GPSSatellitesTracked,
            radio.GPSSatellitesVisible,
            radio.GPSSpeed,
            radio.GPSFreqError,
            radio.GPSStatus
        };
    }

    // --- TNF (#10) ---

    public List<object> ListTnfs()
    {
        var radio = _radio;
        if (radio == null || !radio.Connected) return [];
        return radio.TNFList.Select(t => (object)new
        {
            t.ID,
            FrequencyMHz = t.Frequency,
            t.Depth,
            BandwidthHz = (int)(t.Bandwidth * 1e6),
            t.Permanent
        }).ToList();
    }

    public bool AddTnf(double frequencyMHz)
    {
        var radio = _radio;
        if (radio == null || !radio.Connected) return false;
        // RequestTNF with panID 0 uses frequency directly when freq != 0
        radio.RequestTNF(frequencyMHz, 0);
        return true;
    }

    public bool RemoveTnf(uint tnfId)
    {
        var radio = _radio;
        if (radio == null || !radio.Connected) return false;
        var tnf = radio.TNFList.FirstOrDefault(t => t.ID == tnfId);
        if (tnf == null) return false;
        tnf.Close();
        return true;
    }

    // --- RF Power (#11) ---

    public object? GetRfPower()
    {
        var radio = _radio;
        if (radio == null || !radio.Connected) return null;
        return new
        {
            radio.RFPower,
            radio.TunePower,
            radio.MaxPowerLevel
        };
    }

    public bool SetRfPower(int? rfPower, int? tunePower)
    {
        var radio = _radio;
        if (radio == null || !radio.Connected) return false;
        if (rfPower.HasValue) radio.RFPower = rfPower.Value;
        if (tunePower.HasValue) radio.TunePower = tunePower.Value;
        return true;
    }

    // --- ATU (#12) ---

    public object? GetAtuStatus()
    {
        var radio = _radio;
        if (radio == null || !radio.Connected) return null;

        var tuner = radio.TunerList.FirstOrDefault();
        return new
        {
            radio.ATUPresent,
            radio.ATUEnabled,
            radio.ATUMemoriesEnabled,
            radio.ATUUsingMemory,
            IsTuning = tuner?.IsTuning ?? false,
            IsBypass = tuner?.IsBypass ?? false
        };
    }

    public (bool Success, string Message) AtuTune()
    {
        var radio = _radio;
        if (radio == null || !radio.Connected) return (false, "Radio not connected");
        if (!radio.ATUPresent) return (false, "No ATU present");

        var tuner = radio.TunerList.FirstOrDefault();
        if (tuner == null) return (false, "No tuner found");

        tuner.AutoTune();
        return (true, "ATU tune initiated");
    }

    // --- Memories (#13) ---

    public List<object> ListMemories()
    {
        var radio = _radio;
        if (radio == null || !radio.Connected) return [];
        return radio.MemoryList.Select(m => (object)new
        {
            m.Index,
            m.Name,
            m.Group,
            FrequencyMHz = m.Freq,
            m.Mode
        }).ToList();
    }

    public bool LoadMemory(int index)
    {
        var radio = _radio;
        if (radio == null || !radio.Connected) return false;
        var mem = radio.MemoryList.FirstOrDefault(m => m.Index == index);
        if (mem == null) return false;
        mem.Select();
        return true;
    }

    public bool DeleteMemory(int index)
    {
        var radio = _radio;
        if (radio == null || !radio.Connected) return false;
        var mem = radio.MemoryList.FirstOrDefault(m => m.Index == index);
        if (mem == null) return false;
        mem.Remove();
        return true;
    }

    // --- DX Spots (#14) ---

    public List<object> ListSpots()
    {
        var radio = _radio;
        if (radio == null || !radio.Connected) return [];
        return radio.SpotsList.Select(s => (object)new
        {
            s.Callsign,
            s.SpotterCallsign,
            FrequencyMHz = s.RXFrequency,
            s.Mode,
            s.Source,
            s.Comment,
            s.Timestamp
        }).ToList();
    }

    public bool RemoveSpot(string callsign)
    {
        var radio = _radio;
        if (radio == null || !radio.Connected) return false;
        var spot = radio.SpotsList.FirstOrDefault(s =>
            string.Equals(s.Callsign, callsign, StringComparison.OrdinalIgnoreCase));
        if (spot == null) return false;
        spot.Remove();
        return true;
    }

    // --- AGC (#15) ---

    public object? GetAgc()
    {
        var slice = GetActiveSlice();
        if (slice == null) return null;
        return new
        {
            Mode = slice.AGCMode.ToString(),
            slice.AGCThreshold,
            slice.AGCOffLevel
        };
    }

    public bool SetAgc(string? mode, int? threshold, int? offLevel)
    {
        var slice = GetActiveSlice();
        if (slice == null) return false;
        if (mode != null)
        {
            var normalizedMode = mode;
            if (normalizedMode.Equals("med", StringComparison.OrdinalIgnoreCase))
                normalizedMode = "Medium";

            if (Enum.TryParse<AGCMode>(normalizedMode, true, out var agcMode))
                slice.AGCMode = agcMode;
            else
                return false;
        }
        if (threshold.HasValue) slice.AGCThreshold = threshold.Value;
        if (offLevel.HasValue) slice.AGCOffLevel = offLevel.Value;
        return true;
    }

    // --- Antenna (#22) ---

    public object? GetAntenna()
    {
        var slice = GetActiveSlice();
        if (slice == null) return null;
        return new
        {
            slice.RXAnt,
            slice.TXAnt,
            slice.RXAntList,
            slice.TXAntList
        };
    }

    public bool SetAntenna(string? rxAnt, string? txAnt)
    {
        var slice = GetActiveSlice();
        if (slice == null) return false;
        if (rxAnt != null) slice.RXAnt = rxAnt;
        if (txAnt != null) slice.TXAnt = txAnt;
        return true;
    }

    // --- Slice Audio (#23) ---

    public object? GetSliceAudio()
    {
        var slice = GetActiveSlice();
        if (slice == null) return null;
        return new
        {
            slice.AudioGain,
            slice.AudioPan,
            slice.Mute
        };
    }

    public bool SetSliceAudio(int? audioGain, int? audioPan, bool? mute)
    {
        var slice = GetActiveSlice();
        if (slice == null) return false;
        if (audioGain.HasValue) slice.AudioGain = audioGain.Value;
        if (audioPan.HasValue) slice.AudioPan = audioPan.Value;
        if (mute.HasValue) slice.Mute = mute.Value;
        return true;
    }

    // --- TX Control (#24) ---

    public object? GetTxState()
    {
        var radio = _radio;
        if (radio == null || !radio.Connected) return null;
        return new
        {
            radio.Mox,
            radio.TXTune,
            radio.TXMonitor,
            radio.TXInhibit
        };
    }

    public bool SetTx(bool? mox, bool? txTune, bool? txMonitor, bool? txInhibit)
    {
        var radio = _radio;
        if (radio == null || !radio.Connected) return false;
        if (mox.HasValue) radio.Mox = mox.Value;
        if (txTune.HasValue) radio.TXTune = txTune.Value;
        if (txMonitor.HasValue) radio.TXMonitor = txMonitor.Value;
        if (txInhibit.HasValue) radio.TXInhibit = txInhibit.Value;
        return true;
    }

    // --- Equalizer (#25) ---

    public object? GetEqualizer(string select)
    {
        var radio = _radio;
        if (radio == null || !radio.Connected) return null;
        if (!Enum.TryParse<EqualizerSelect>(select, true, out var eqSelect) || eqSelect == EqualizerSelect.None)
            return null;

        var eq = radio.FindEqualizerByEQSelect(eqSelect);
        if (eq == null)
        {
            eq = radio.CreateEqualizer(eqSelect);
            if (eq == null) return null;
        }

        return new
        {
            Select = eq.EQ_select.ToString(),
            eq.EQ_enabled,
            eq.level_63Hz,
            eq.level_125Hz,
            eq.level_250Hz,
            eq.level_500Hz,
            eq.level_1000Hz,
            eq.level_2000Hz,
            eq.level_4000Hz,
            eq.level_8000Hz
        };
    }

    public bool SetEqualizer(string select, bool? enabled, int? hz63, int? hz125, int? hz250, int? hz500, int? hz1000, int? hz2000, int? hz4000, int? hz8000)
    {
        var radio = _radio;
        if (radio == null || !radio.Connected) return false;
        if (!Enum.TryParse<EqualizerSelect>(select, true, out var eqSelect) || eqSelect == EqualizerSelect.None)
            return false;

        var eq = radio.FindEqualizerByEQSelect(eqSelect);
        if (eq == null)
        {
            eq = radio.CreateEqualizer(eqSelect);
            if (eq == null) return false;
        }

        if (enabled.HasValue) eq.EQ_enabled = enabled.Value;
        if (hz63.HasValue) eq.level_63Hz = hz63.Value;
        if (hz125.HasValue) eq.level_125Hz = hz125.Value;
        if (hz250.HasValue) eq.level_250Hz = hz250.Value;
        if (hz500.HasValue) eq.level_500Hz = hz500.Value;
        if (hz1000.HasValue) eq.level_1000Hz = hz1000.Value;
        if (hz2000.HasValue) eq.level_2000Hz = hz2000.Value;
        if (hz4000.HasValue) eq.level_4000Hz = hz4000.Value;
        if (hz8000.HasValue) eq.level_8000Hz = hz8000.Value;
        return true;
    }

    // --- Mic/TX Audio (#26) ---

    public object? GetMic()
    {
        var radio = _radio;
        if (radio == null || !radio.Connected) return null;
        return new
        {
            radio.MicLevel,
            radio.MicBoost,
            radio.MicBias,
            radio.MicInput,
            radio.MicInputList
        };
    }

    public bool SetMic(int? micLevel, bool? micBoost, bool? micBias, string? micInput)
    {
        var radio = _radio;
        if (radio == null || !radio.Connected) return false;
        if (micLevel.HasValue) radio.MicLevel = micLevel.Value;
        if (micBoost.HasValue) radio.MicBoost = micBoost.Value;
        if (micBias.HasValue) radio.MicBias = micBias.Value;
        if (micInput != null) radio.MicInput = micInput;
        return true;
    }

    public object? GetTxAudioProcessing()
    {
        var radio = _radio;
        if (radio == null || !radio.Connected) return null;
        return new
        {
            radio.CompanderOn,
            radio.CompanderLevel,
            radio.SpeechProcessorEnable,
            radio.SpeechProcessorLevel
        };
    }

    public bool SetTxAudioProcessing(bool? companderOn, int? companderLevel, bool? speechProcessorEnable, uint? speechProcessorLevel)
    {
        var radio = _radio;
        if (radio == null || !radio.Connected) return false;
        if (companderOn.HasValue) radio.CompanderOn = companderOn.Value;
        if (companderLevel.HasValue) radio.CompanderLevel = companderLevel.Value;
        if (speechProcessorEnable.HasValue) radio.SpeechProcessorEnable = speechProcessorEnable.Value;
        if (speechProcessorLevel.HasValue) radio.SpeechProcessorLevel = speechProcessorLevel.Value;
        return true;
    }

    // --- VOX (#27) ---

    public object? GetVox()
    {
        var radio = _radio;
        if (radio == null || !radio.Connected) return null;
        return new
        {
            radio.SimpleVOXEnable,
            radio.SimpleVOXLevel,
            radio.SimpleVOXDelay
        };
    }

    public bool SetVox(bool? enabled, int? level, int? delay)
    {
        var radio = _radio;
        if (radio == null || !radio.Connected) return false;
        if (enabled.HasValue) radio.SimpleVOXEnable = enabled.Value;
        if (level.HasValue) radio.SimpleVOXLevel = level.Value;
        if (delay.HasValue) radio.SimpleVOXDelay = delay.Value;
        return true;
    }

    // --- Panadapter (#28) ---

    public List<object> ListPanadapters()
    {
        var radio = _radio;
        if (radio == null || !radio.Connected) return [];
        return radio.PanadapterList.Select(p => (object)new
        {
            StreamID = p.StreamID.ToString("X"),
            p.CenterFreq,
            p.Bandwidth,
            p.MinBandwidth,
            p.MaxBandwidth,
            p.LowDbm,
            p.HighDbm,
            p.FPS,
            p.Average,
            p.WeightedAverage,
            p.Band,
            p.RXAnt
        }).ToList();
    }

    public bool SetPanadapter(string streamIdHex, double? centerFreq, double? bandwidth, double? lowDbm, double? highDbm, int? fps, int? average)
    {
        var radio = _radio;
        if (radio == null || !radio.Connected) return false;
        if (!uint.TryParse(streamIdHex, System.Globalization.NumberStyles.HexNumber, null, out var streamId))
            return false;

        var pan = radio.PanadapterList.FirstOrDefault(p => p.StreamID == streamId);
        if (pan == null) return false;

        if (centerFreq.HasValue) pan.CenterFreq = centerFreq.Value;
        if (bandwidth.HasValue) pan.Bandwidth = bandwidth.Value;
        if (lowDbm.HasValue) pan.LowDbm = lowDbm.Value;
        if (highDbm.HasValue) pan.HighDbm = highDbm.Value;
        if (fps.HasValue) pan.FPS = fps.Value;
        if (average.HasValue) pan.Average = average.Value;
        return true;
    }

    // --- Add Spot (#30) ---

    public bool AddSpot(string callsign, double frequencyMHz, string? mode = null,
        string? color = null, string? backgroundColor = null,
        string? spotterCallsign = null, string? source = null,
        string? comment = null, int lifetimeSeconds = 600)
    {
        var radio = _radio;
        if (radio == null || !radio.Connected) return false;
        if (string.IsNullOrWhiteSpace(callsign)) return false;
        if (frequencyMHz <= 0) return false;
        lifetimeSeconds = Math.Clamp(lifetimeSeconds, 0, 86400); // cap at 24h

        var spot = new Flex.Smoothlake.FlexLib.Spot
        {
            Callsign = callsign,
            RXFrequency = frequencyMHz,
            LifetimeSeconds = lifetimeSeconds
        };

        if (mode != null) spot.Mode = mode;
        if (color != null) spot.Color = color;
        if (backgroundColor != null) spot.BackgroundColor = backgroundColor;
        if (spotterCallsign != null) spot.SpotterCallsign = spotterCallsign;
        if (source != null) spot.Source = source;
        if (comment != null) spot.Comment = comment;
        spot.Timestamp = DateTime.UtcNow;

        radio.RequestSpot(spot);
        return true;
    }

    public void ClearAllSpots()
    {
        var radio = _radio;
        if (radio == null || !radio.Connected) return;
        radio.ClearAllSpots();
    }

    // --- Tune to Spot (#29) ---

    public (bool Success, string Message) TuneToSpot(string callsign)
    {
        var radio = _radio;
        if (radio == null || !radio.Connected) return (false, "Radio not connected");

        var spot = radio.SpotsList.FirstOrDefault(s =>
            string.Equals(s.Callsign, callsign, StringComparison.OrdinalIgnoreCase));
        if (spot == null) return (false, $"Spot for {callsign} not found");

        var slice = GetActiveSlice();
        if (slice == null) return (false, "No active slice");

        slice.Freq = spot.RXFrequency;
        return (true, $"Tuned to {callsign} at {spot.RXFrequency:F6} MHz");
    }

    public Dictionary<string, object> GetMeters()
    {
        var radio = _radio;
        if (radio == null || !radio.Connected)
            return new Dictionary<string, object> { ["error"] = "Radio not connected" };

        // Also subscribe to any slice S-meter that we haven't caught yet
        SubscribeSliceMeters(radio);

        var result = new Dictionary<string, object>();

        foreach (var kvp in _meterValues)
            result[kvp.Key] = Math.Round(kvp.Value, 2);

        return result;
    }

    private void SubscribeMeterEvents(Flex.Smoothlake.FlexLib.Radio radio)
    {
        _meterValues.Clear();

        radio.ForwardPowerDataReady += data => _meterValues["FWDPWR"] = data;
        radio.ReflectedPowerDataReady += data => _meterValues["REFPWR"] = data;
        radio.SWRDataReady += data => _meterValues["SWR"] = data;
        radio.PATempDataReady += data => _meterValues["PATEMP"] = data;
        radio.VoltsDataReady += data => _meterValues["VOLTS"] = data;
        radio.MicDataReady += data => _meterValues["MIC"] = data;
        radio.MicPeakDataReady += data => _meterValues["MICPEAK"] = data;
        radio.CompPeakDataReady += data => _meterValues["COMPPEAK"] = data;
        radio.HWAlcDataReady += data => _meterValues["HWALC"] = data;

        // Subscribe to S-meters on existing slices
        SubscribeSliceMeters(radio);
    }

    private readonly HashSet<int> _subscribedSlices = [];

    private void SubscribeSliceMeters(Flex.Smoothlake.FlexLib.Radio radio)
    {
        foreach (var slice in radio.SliceList)
        {
            if (_subscribedSlices.Add(slice.Index))
            {
                var sliceIndex = slice.Index;
                slice.SMeterDataReady += data => _meterValues[$"S-METER_SLC{sliceIndex}"] = data;
            }
        }
    }

    private void OnRadioAdded(Flex.Smoothlake.FlexLib.Radio radio)
    {
        RadioDiscovered?.Invoke(radio);
    }

    public void Dispose()
    {
        Disconnect();
        API.CloseSession();
    }
}

public record RadioInfo(string Name, string Model, string Serial, string IpAddress, string Status);
