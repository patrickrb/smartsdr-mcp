using SmartSdrMcp.Audio;
using SmartSdrMcp.Cw.Decoder;
using SmartSdrMcp.Cw.Dsp;
using SmartSdrMcp.Cw.Rescorer;

namespace SmartSdrMcp.Cw;

/// <summary>
/// Orchestrates the 3-stage CW decode pipeline:
/// Stage 1: DSP (filter → AGC → tone detect → envelope)
/// Stage 2: Morse decoder (timing → symbols → characters)
/// Stage 3: Ham-aware rescoring
/// </summary>
public class CwPipeline : IDisposable
{
    private readonly AudioPipeline _audioPipeline;
    private readonly BandPassFilter _filter;
    private readonly AgcProcessor _agc;
    private readonly ToneDetector _toneDetector;
    private readonly EnvelopeDetector _envelopeDetector;
    private readonly WpmEstimator _wpmEstimator;
    private readonly MorseDecoder _morseDecoder;
    private readonly HamRescorer _rescorer;

    private readonly double _sampleRate;
    private readonly object _textLock = new();
    private string _liveBuffer = "";
    private readonly List<DecodedCharacter> _recentChars = new();
    private Timer? _timeoutTimer;
    private bool _running;

    // RMS tracking for UI display (no longer used for gating — squelch is in ToneDetector)
    private double _signalRms;
    private double _rmsAccumulator;
    private int _rmsSampleCount;
    private const int RmsWindowSamples = 240; // 10ms at 24kHz

    public bool IsRunning => _running;
    public double EstimatedWpm => _wpmEstimator.EstimatedWpm;
    public double SignalRms => _signalRms;
    public double ToneMagnitude => _toneDetector.SmoothedMagnitude;
    public double NoiseFloor => _toneDetector.NoiseFloor;
    public double PeakMagnitude => _toneDetector.PeakMagnitude;
    public bool TonePresent => _toneDetector.TonePresent;
    public bool GateOpen => _toneDetector.SignalPresent;

    // Key event diagnostics
    private readonly Queue<string> _keyEventLog = new();
    private const int MaxKeyEventLog = 30;

    public event Action<string>? CharacterReceived;
    public event Action? WordGapReceived;
    public event Action<string>? LiveTextUpdated;

    public CwPipeline(AudioPipeline audioPipeline, double toneFreq = 600, double sampleRate = 24000)
    {
        _audioPipeline = audioPipeline;
        _sampleRate = sampleRate;
        _filter = new BandPassFilter(toneFreq, 80, sampleRate);
        _agc = new AgcProcessor();
        _toneDetector = new ToneDetector(toneFreq, sampleRate, 10);
        _wpmEstimator = new WpmEstimator();
        _envelopeDetector = new EnvelopeDetector(_wpmEstimator);
        _morseDecoder = new MorseDecoder(_wpmEstimator);
        _rescorer = new HamRescorer();

        _envelopeDetector.KeyEventDetected += OnKeyEvent;
        _morseDecoder.CharacterDecoded += OnCharacterDecoded;
        _morseDecoder.WordGapDetected += OnWordGap;
    }

    public void SetToneFrequency(double toneFreq)
    {
        _filter.Configure(toneFreq, 80, _sampleRate);
        _toneDetector.Reconfigure(toneFreq, _sampleRate);
    }

    public void SetFixedWpm(double wpm)
    {
        _wpmEstimator.SetFixedWpm(wpm);
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _audioPipeline.AudioDataAvailable += ProcessAudioBlock;
        _timeoutTimer = new Timer(_ => _morseDecoder.CheckTimeout(), null, 50, 50);
    }

    public void Stop()
    {
        _running = false;
        _audioPipeline.AudioDataAvailable -= ProcessAudioBlock;
        _timeoutTimer?.Dispose();
        _timeoutTimer = null;
    }

    public string GetLiveText()
    {
        lock (_textLock)
        {
            return _rescorer.Rescore(_liveBuffer);
        }
    }

    /// <summary>
    /// Get recent decoded characters with their N-best alternatives for AI rescoring.
    /// </summary>
    public List<DecodedCharacter> GetRecentCharacters(int count = 100)
    {
        lock (_textLock)
        {
            int start = Math.Max(0, _recentChars.Count - count);
            return _recentChars.Skip(start).ToList();
        }
    }

    public void ClearLiveText()
    {
        lock (_textLock) _liveBuffer = "";
    }

    private void ProcessAudioBlock(float[] samples)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            float filtered = _filter.Process(samples[i]);

            // Track RMS for UI display only
            _rmsAccumulator += filtered * filtered;
            _rmsSampleCount++;
            if (_rmsSampleCount >= RmsWindowSamples)
            {
                _signalRms = Math.Sqrt(_rmsAccumulator / _rmsSampleCount);
                _rmsAccumulator = 0;
                _rmsSampleCount = 0;
            }

            // NOTE: AGC removed from tone detection path — it compresses SNR,
            // making the Goertzel's adaptive threshold unable to distinguish signal from noise.
            // The tone detector's own adaptive noise floor handles amplitude variation.

            if (_toneDetector.ProcessSample(filtered))
            {
                var now = DateTime.UtcNow;
                // Squelch is now built into ToneDetector (Tier 1 + Tier 2)
                bool toneState = _toneDetector.TonePresent;
                _envelopeDetector.ProcessToneState(toneState, now);
            }
        }
    }

    private void OnKeyEvent(KeyEvent ke)
    {
        string label = ke.State == KeyState.Up ? "ON" : "gap";
        string entry = $"{label}:{ke.Duration.TotalMilliseconds:F0}ms";
        lock (_keyEventLog)
        {
            _keyEventLog.Enqueue(entry);
            while (_keyEventLog.Count > MaxKeyEventLog)
                _keyEventLog.Dequeue();
        }
        _morseDecoder.ProcessKeyEvent(ke);
    }

    public string GetKeyEventLog()
    {
        lock (_keyEventLog)
        {
            return string.Join(" ", _keyEventLog);
        }
    }

    private void OnCharacterDecoded(DecodedCharacter decoded)
    {
        lock (_textLock)
        {
            _liveBuffer += decoded.Character;
            _recentChars.Add(decoded);

            // Keep buffer manageable
            if (_recentChars.Count > 500)
                _recentChars.RemoveRange(0, 100);
        }

        CharacterReceived?.Invoke(decoded.Character);
        LiveTextUpdated?.Invoke(GetLiveText());
    }

    private void OnWordGap()
    {
        lock (_textLock)
        {
            if (_liveBuffer.Length > 0 && !_liveBuffer.EndsWith(' '))
                _liveBuffer += " ";
        }
        WordGapReceived?.Invoke();
        LiveTextUpdated?.Invoke(GetLiveText());
    }

    public void Reset()
    {
        _filter.Reset();
        _agc.Reset();
        _toneDetector.Reset();
        _envelopeDetector.Reset();
        _wpmEstimator.Reset();
        _morseDecoder.Reset();
        _signalRms = 0;
        _rmsAccumulator = 0;
        _rmsSampleCount = 0;
        lock (_textLock)
        {
            _liveBuffer = "";
            _recentChars.Clear();
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
