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

    public bool SetMode(string mode)
    {
        var slice = GetActiveSlice();
        if (slice == null) return false;
        slice.DemodMode = mode.ToUpper();
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
