using SmartSdrMcp.Cw;

namespace SmartSdrMcp.Qso;

/// <summary>
/// Detects message boundaries in continuous CW decode stream.
/// </summary>
public class MessageSegmenter
{
    private readonly CwPipeline _cwPipeline;
    private readonly string _myCallsign;
    private string _currentSegment = "";
    private DateTime _lastCharTime = DateTime.UtcNow;
    private Timer? _silenceTimer;
    private double _frequencyMHz;

    // Silence threshold to end a message (ms)
    public double SilenceThresholdMs { get; set; } = 3000;

    public event Action<CwMessage>? MessageCompleted;

    public MessageSegmenter(CwPipeline cwPipeline, string myCallsign)
    {
        _cwPipeline = cwPipeline;
        _myCallsign = myCallsign;
    }

    public void Start(double frequencyMHz)
    {
        _frequencyMHz = frequencyMHz;
        _cwPipeline.CharacterReceived += OnCharacter;
        _cwPipeline.WordGapReceived += OnWordGap;
        _silenceTimer = new Timer(CheckSilence, null, 500, 500);
    }

    public void Stop()
    {
        _cwPipeline.CharacterReceived -= OnCharacter;
        _cwPipeline.WordGapReceived -= OnWordGap;
        _silenceTimer?.Dispose();
        _silenceTimer = null;
        FlushSegment();
    }

    public void UpdateFrequency(double frequencyMHz) => _frequencyMHz = frequencyMHz;

    private void OnCharacter(string ch)
    {
        _currentSegment += ch;
        _lastCharTime = DateTime.UtcNow;

        // Check for prosigns that end a transmission
        if (_currentSegment.EndsWith(" K ") || _currentSegment.EndsWith(" KN ") ||
            _currentSegment.EndsWith("<SK>") || _currentSegment.EndsWith("<AR>") ||
            _currentSegment.EndsWith(" BK "))
        {
            FlushSegment();
        }
    }

    private void OnWordGap()
    {
        // A word gap is natural, but don't flush yet — wait for more silence
    }

    private void CheckSilence(object? state)
    {
        if (_currentSegment.Length == 0) return;

        double silenceMs = (DateTime.UtcNow - _lastCharTime).TotalMilliseconds;
        if (silenceMs >= SilenceThresholdMs)
        {
            FlushSegment();
        }
    }

    private void FlushSegment()
    {
        var text = _currentSegment.Trim();
        _currentSegment = "";
        if (string.IsNullOrWhiteSpace(text)) return;

        bool isCq = CallsignDetector.IsCqMessage(text);
        string? callsign = CallsignDetector.ExtractStationCallsign(text, _myCallsign);

        var message = new CwMessage(
            Timestamp: DateTime.UtcNow,
            FrequencyMHz: _frequencyMHz,
            DecodedText: text,
            Confidence: 0.7,
            DetectedCallsign: callsign,
            IsCq: isCq);

        MessageCompleted?.Invoke(message);
    }
}
