namespace SmartSdrMcp.Cw.Dsp;

/// <summary>
/// Tracks tone on/off transitions and emits key events with timestamps.
/// </summary>
public class EnvelopeDetector
{
    private readonly WpmEstimator? _wpmEstimator;
    private bool _lastToneState;
    private DateTime _lastTransitionTime;
    private DateTime _lastEventTime;

    public event Action<KeyEvent>? KeyEventDetected;

    // Fixed fallback minimum key-down/key-up duration (ms)
    public double MinEventDurationMs { get; set; } = 35;

    public EnvelopeDetector(WpmEstimator? wpmEstimator = null)
    {
        _wpmEstimator = wpmEstimator;
        _lastTransitionTime = DateTime.UtcNow;
        _lastEventTime = DateTime.UtcNow;
    }

    private double EffectiveMinDuration
    {
        get
        {
            if (_wpmEstimator == null) return MinEventDurationMs;
            // Scale debounce to 40% of estimated dit, clamped to [15, 60] ms
            return Math.Clamp(_wpmEstimator.EstimatedDitMs * 0.4, 15, 60);
        }
    }

    public void ProcessToneState(bool tonePresent, DateTime timestamp)
    {
        if (tonePresent == _lastToneState) return;

        var duration = timestamp - _lastTransitionTime;
        if (duration.TotalMilliseconds < EffectiveMinDuration) return; // Debounce

        if (_lastToneState) // Was ON, now OFF → key-up
        {
            KeyEventDetected?.Invoke(new KeyEvent(
                KeyState.Up,
                _lastTransitionTime,
                timestamp,
                duration));
        }
        else // Was OFF, now ON → key-down
        {
            // Emit gap duration
            KeyEventDetected?.Invoke(new KeyEvent(
                KeyState.Down,
                _lastTransitionTime,
                timestamp,
                duration));
        }

        _lastToneState = tonePresent;
        _lastTransitionTime = timestamp;
    }

    public void Reset()
    {
        _lastToneState = false;
        _lastTransitionTime = DateTime.UtcNow;
    }
}

public enum KeyState { Down, Up }

public record KeyEvent(
    KeyState State,
    DateTime StartTime,
    DateTime EndTime,
    TimeSpan Duration);
