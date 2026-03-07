using SmartSdrMcp.Cw.Dsp;

namespace SmartSdrMcp.Cw.Decoder;

/// <summary>
/// Converts key events (from envelope detector) into Morse symbols and characters.
/// </summary>
public class MorseDecoder
{
    private readonly WpmEstimator _wpmEstimator;
    private readonly List<MorseElement> _currentPattern = new();
    private string _currentDitDah = "";
    private DateTime _lastKeyUpTime = DateTime.UtcNow;
    private bool _inCharacter;

    public event Action<DecodedCharacter>? CharacterDecoded;
    public event Action? WordGapDetected;

    public MorseDecoder(WpmEstimator wpmEstimator)
    {
        _wpmEstimator = wpmEstimator;
    }

    public void ProcessKeyEvent(KeyEvent keyEvent)
    {
        double ditMs = _wpmEstimator.EstimatedDitMs;

        if (keyEvent.State == KeyState.Up)
        {
            // Key was down for this duration → dit or dah
            double durationMs = keyEvent.Duration.TotalMilliseconds;
            _wpmEstimator.AddKeyDownDuration(durationMs);

            double ratio = durationMs / ditMs;
            SymbolCandidate symbol;

            if (ratio < 2.0)
            {
                double conf = 1.0 - Math.Abs(ratio - 1.0) * 0.5;
                symbol = new SymbolCandidate(MorseElement.Dit, Math.Clamp(conf, 0.3, 1.0),
                    MorseElement.Dah, Math.Clamp(1.0 - conf, 0, 0.5));
                _currentDitDah += ".";
            }
            else
            {
                double conf = 1.0 - Math.Abs(ratio - 3.0) / 3.0 * 0.5;
                symbol = new SymbolCandidate(MorseElement.Dah, Math.Clamp(conf, 0.3, 1.0),
                    MorseElement.Dit, Math.Clamp(1.0 - conf, 0, 0.5));
                _currentDitDah += "-";
            }

            _currentPattern.Add(symbol.Element);
            _inCharacter = true;
            _lastKeyUpTime = keyEvent.EndTime;

            // Cap pattern length — longest valid Morse is 8 elements
            if (_currentDitDah.Length >= 8)
                FlushCharacter();
        }
        else // KeyState.Down → gap before this key-down
        {
            if (!_inCharacter) return;

            double gapMs = keyEvent.Duration.TotalMilliseconds;
            double ratio = gapMs / ditMs;

            // Adaptive character gap: use median intra-element gap if available
            double medianGap = _wpmEstimator.MedianGapMs;
            double charGapThreshold;
            double wordGapThreshold;

            if (medianGap > 0)
            {
                // Character gap when gap > 2.5x the typical intra-element gap
                charGapThreshold = medianGap * 2.5;
                wordGapThreshold = medianGap * 6.0;
            }
            else
            {
                // Fallback to dit-based thresholds
                charGapThreshold = ditMs * 2.2;
                wordGapThreshold = ditMs * 5.0;
            }

            // Feed short gaps to WPM estimator as ~1T reference
            if (gapMs < charGapThreshold)
                _wpmEstimator.AddGapDuration(gapMs);

            if (gapMs >= charGapThreshold)
            {
                FlushCharacter();

                if (gapMs >= wordGapThreshold)
                {
                    WordGapDetected?.Invoke();
                }
            }
        }
    }

    /// <summary>
    /// Call periodically to flush if silence exceeds character gap time.
    /// </summary>
    public void CheckTimeout()
    {
        if (!_inCharacter) return;

        double silenceMs = (DateTime.UtcNow - _lastKeyUpTime).TotalMilliseconds;
        double ditMs = _wpmEstimator.EstimatedDitMs;

        if (silenceMs > ditMs * 4)
        {
            FlushCharacter();
            if (silenceMs > ditMs * 8)
            {
                WordGapDetected?.Invoke();
            }
        }
    }

    private void FlushCharacter()
    {
        if (_currentDitDah.Length == 0) return;

        string? decoded = MorseTable.Lookup(_currentDitDah);
        double confidence = decoded != null ? 0.9 : 0.3;

        CharacterDecoded?.Invoke(new DecodedCharacter(
            _currentDitDah,
            decoded ?? $"?({_currentDitDah})",
            confidence,
            DateTime.UtcNow));

        _currentDitDah = "";
        _currentPattern.Clear();
        _inCharacter = false;
    }

    public void Reset()
    {
        _currentDitDah = "";
        _currentPattern.Clear();
        _inCharacter = false;
    }
}

public record DecodedCharacter(
    string DitDahPattern,
    string Character,
    double Confidence,
    DateTime Timestamp);
