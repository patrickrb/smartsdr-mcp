namespace SmartSdrMcp.Cw.Dsp;

/// <summary>
/// Tracks tone on/off transitions and emits key events with timestamps.
/// Uses asymmetric debounce: longer for ON (reject noise spikes),
/// shorter for OFF (preserve gap detection).
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

    /// <summary>
    /// Minimum key-down duration (OFF→ON): reject noise spikes.
    /// 30% of dit, clamped [15, 45] ms.
    /// </summary>
    private double MinKeyDownMs
    {
        get
        {
            if (_wpmEstimator == null) return MinEventDurationMs;
            return Math.Clamp(_wpmEstimator.EstimatedDitMs * 0.30, 15, 45);
        }
    }

    /// <summary>
    /// Minimum gap duration (ON→OFF): shorter to preserve gap detection.
    /// 15% of dit, clamped [5, 25] ms.
    /// </summary>
    private double MinGapMs
    {
        get
        {
            if (_wpmEstimator == null) return MinEventDurationMs * 0.5;
            return Math.Clamp(_wpmEstimator.EstimatedDitMs * 0.15, 5, 25);
        }
    }

    public void ProcessToneState(bool tonePresent, DateTime timestamp)
    {
        if (tonePresent == _lastToneState) return;

        var duration = timestamp - _lastTransitionTime;

        // Asymmetric debounce: use MinGapMs for ON→OFF, MinKeyDownMs for OFF→ON
        double minDuration = _lastToneState ? MinGapMs : MinKeyDownMs;
        if (duration.TotalMilliseconds < minDuration) return;

        if (_lastToneState) // Was ON, now OFF → key-up
        {
            KeyEventDetected?.Invoke(new KeyEvent(
                KeyState.Up,
                _lastTransitionTime,
                timestamp,
                duration));
        }
        else // Was OFF, now ON → key-down (gap)
        {
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
