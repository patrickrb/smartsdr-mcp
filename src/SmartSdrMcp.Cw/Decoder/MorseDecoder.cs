using SmartSdrMcp.Cw.Dsp;

namespace SmartSdrMcp.Cw.Decoder;

/// <summary>
/// Converts key events (from envelope detector) into Morse symbols and characters.
/// </summary>
public class MorseDecoder
{
    private readonly WpmEstimator _wpmEstimator;
    private readonly List<MorseElement> _currentPattern = new();
    private readonly List<SymbolCandidate> _currentCandidates = new();
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

            // Adaptive sigmoid: center at midpoint between dit and dah clusters
            double dahMs = _wpmEstimator.EstimatedDahMs;
            double sigmoidCenter = (ditMs + dahMs) / 2.0;
            double ratio = durationMs / sigmoidCenter;
            double x = (ratio - 1.0) * 4.0;
            double pDah = 1.0 / (1.0 + Math.Exp(-x));
            double pDit = 1.0 - pDah;

            SymbolCandidate symbol;
            if (pDit >= pDah)
            {
                symbol = new SymbolCandidate(MorseElement.Dit, pDit,
                    MorseElement.Dah, pDah);
                _currentDitDah += ".";
            }
            else
            {
                symbol = new SymbolCandidate(MorseElement.Dah, pDah,
                    MorseElement.Dit, pDit);
                _currentDitDah += "-";
            }

            _currentPattern.Add(symbol.Element);
            _currentCandidates.Add(symbol);
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

            // Feed ALL gaps to clustering — breaks self-contamination loop
            _wpmEstimator.AddRawGapDuration(gapMs);

            // Use clustered gap threshold (data-driven from WpmEstimator)
            double charGapThreshold = _wpmEstimator.CharGapThresholdMs;
            double wordGapThreshold = charGapThreshold * 2.5;

            // Also feed short gaps for legacy cross-check
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

        // Generate N-best alternative characters from ambiguous elements
        var alternatives = GenerateAlternatives();

        CharacterDecoded?.Invoke(new DecodedCharacter(
            _currentDitDah,
            decoded ?? $"?({_currentDitDah})",
            confidence,
            DateTime.UtcNow,
            alternatives.Count > 0 ? alternatives : null));

        _currentDitDah = "";
        _currentPattern.Clear();
        _currentCandidates.Clear();
        _inCharacter = false;
    }

    /// <summary>
    /// Generate alternative character interpretations by flipping ambiguous elements.
    /// Only considers elements where the alternative confidence > 0.15.
    /// Returns up to 5 candidates sorted by score.
    /// </summary>
    private List<CharacterCandidate> GenerateAlternatives()
    {
        if (_currentCandidates.Count == 0) return new();

        // Find ambiguous positions (where alternative is plausible)
        var ambiguous = new List<int>();
        for (int i = 0; i < _currentCandidates.Count; i++)
        {
            if (_currentCandidates[i].Alternative.HasValue && _currentCandidates[i].AltConfidence > 0.15)
                ambiguous.Add(i);
        }

        if (ambiguous.Count == 0) return new();

        // Limit to the 3 most ambiguous positions to keep combinations manageable
        if (ambiguous.Count > 3)
        {
            ambiguous = ambiguous
                .OrderByDescending(i => _currentCandidates[i].AltConfidence)
                .Take(3)
                .ToList();
        }

        // Precompute position → ambiguous index mapping to avoid O(n²) IndexOf
        var ambiguousIndex = new int[_currentCandidates.Count];
        Array.Fill(ambiguousIndex, -1);
        for (int ai = 0; ai < ambiguous.Count; ai++)
            ambiguousIndex[ambiguous[ai]] = ai;

        var results = new List<CharacterCandidate>();
        int combos = 1 << ambiguous.Count;

        for (int mask = 1; mask < combos; mask++)
        {
            var pattern = new char[_currentCandidates.Count];
            double score = 1.0;

            for (int i = 0; i < _currentCandidates.Count; i++)
            {
                int ambIdx = ambiguousIndex[i];
                bool flip = ambIdx >= 0 && (mask & (1 << ambIdx)) != 0;

                if (flip)
                {
                    pattern[i] = _currentCandidates[i].Alternative == MorseElement.Dit ? '.' : '-';
                    score *= _currentCandidates[i].AltConfidence;
                }
                else
                {
                    pattern[i] = _currentCandidates[i].Element == MorseElement.Dit ? '.' : '-';
                    score *= _currentCandidates[i].Confidence;
                }
            }

            string patternStr = new(pattern);
            if (patternStr == _currentDitDah) continue;

            string? ch = MorseTable.Lookup(patternStr);
            if (ch != null)
                results.Add(new CharacterCandidate(patternStr, ch, score));
        }

        return results.OrderByDescending(c => c.Score).Take(5).ToList();
    }

    public void Reset()
    {
        _currentDitDah = "";
        _currentPattern.Clear();
        _currentCandidates.Clear();
        _inCharacter = false;
    }
}

public record DecodedCharacter(
    string DitDahPattern,
    string Character,
    double Confidence,
    DateTime Timestamp,
    List<CharacterCandidate>? Alternatives = null);
