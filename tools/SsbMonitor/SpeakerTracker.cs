using System.Text.RegularExpressions;
using SmartSdrMcp.Qso;
using SmartSdrMcp.Ssb;

namespace SsbMonitor;

/// <summary>
/// Tracks speakers using voice fingerprint comparison and callsign detection.
/// When a gap is detected between segments, compares the new segment's voice
/// fingerprint against known speakers to determine if it's the same person
/// or someone new.
/// </summary>
public class SpeakerTracker
{
    private const float SimilarityThreshold = 0.70f;
    private readonly TimeSpan _speakerChangeGap;
    private readonly string? _myCallsign;

    private int _nextSpeakerId = 1;
    private int _currentSpeakerId;
    private DateTime _lastSegmentTime = DateTime.MinValue;

    // Speaker ID → running average fingerprint
    private readonly Dictionary<int, VoiceFingerprint> _speakerFingerprints = new();

    // Speaker ID → callsign (null if unknown)
    private readonly Dictionary<int, string?> _speakerCallsigns = new();

    // Callsign → speaker ID (reverse lookup)
    private readonly Dictionary<string, int> _callsignToSpeaker = new(StringComparer.OrdinalIgnoreCase);

    // Track how many segments we've processed
    private int _processedSegmentCount;

    // Pattern to detect self-identification
    private static readonly Regex SelfIdentPattern = new(
        @"(?:THIS\s+IS|MY\s+CALL(?:\s*SIGN)?\s+IS|I(?:'M|\s+AM)|DE)\s+(.+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public SpeakerTracker(TimeSpan? speakerChangeGap = null, string? myCallsign = null)
    {
        _speakerChangeGap = speakerChangeGap ?? TimeSpan.FromSeconds(2);
        _myCallsign = myCallsign?.ToUpperInvariant();
    }

    /// <summary>
    /// Process new segments and return formatted lines with speaker labels.
    /// </summary>
    public List<SpeakerLine> ProcessNewSegments(List<TranscribedSegment> allSegments)
    {
        var results = new List<SpeakerLine>();

        var newSegments = allSegments.Skip(_processedSegmentCount).ToList();
        _processedSegmentCount = allSegments.Count;

        foreach (var segment in newSegments)
        {
            if (!segment.IsFinal || string.IsNullOrWhiteSpace(segment.Text))
                continue;

            bool gapDetected = _lastSegmentTime != DateTime.MinValue &&
                               segment.Timestamp - _lastSegmentTime > _speakerChangeGap;

            if (_currentSpeakerId == 0)
            {
                // First speaker ever
                _currentSpeakerId = CreateSpeaker(segment.Fingerprint);
            }
            else if (gapDetected && segment.Fingerprint != null)
            {
                // Gap detected — use fingerprint to decide if same or different speaker
                var matchedSpeaker = FindMatchingSpeaker(segment.Fingerprint);
                if (matchedSpeaker.HasValue)
                {
                    _currentSpeakerId = matchedSpeaker.Value;
                    // Update their average fingerprint
                    UpdateSpeakerFingerprint(_currentSpeakerId, segment.Fingerprint);
                }
                else
                {
                    // New voice — new speaker
                    _currentSpeakerId = CreateSpeaker(segment.Fingerprint);
                }
            }
            else if (segment.Fingerprint != null && _speakerFingerprints.ContainsKey(_currentSpeakerId))
            {
                // No gap — but check if the voice dramatically changed (someone jumped in)
                var currentFp = _speakerFingerprints[_currentSpeakerId];
                var similarity = currentFp.SimilarityTo(segment.Fingerprint);
                if (similarity < SimilarityThreshold * 0.8f) // Stricter threshold for mid-stream change
                {
                    var matchedSpeaker = FindMatchingSpeaker(segment.Fingerprint);
                    if (matchedSpeaker.HasValue)
                        _currentSpeakerId = matchedSpeaker.Value;
                    else
                        _currentSpeakerId = CreateSpeaker(segment.Fingerprint);
                }
                else
                {
                    // Same speaker — update fingerprint
                    UpdateSpeakerFingerprint(_currentSpeakerId, segment.Fingerprint);
                }
            }

            _lastSegmentTime = segment.Timestamp;

            // Try to detect callsign and associate with current speaker
            TryAssociateCallsign(segment.Text, _currentSpeakerId);

            var label = GetSpeakerLabel(_currentSpeakerId);
            var color = GetSpeakerColor(_currentSpeakerId);
            results.Add(new SpeakerLine(segment.Timestamp, label, segment.Text, color));
        }

        return results;
    }

    private int CreateSpeaker(VoiceFingerprint? fingerprint)
    {
        int id = _nextSpeakerId++;
        _speakerCallsigns[id] = null;
        if (fingerprint != null)
            _speakerFingerprints[id] = fingerprint;
        return id;
    }

    /// <summary>
    /// Find the best matching known speaker for the given fingerprint.
    /// Returns null if no speaker matches above the similarity threshold.
    /// </summary>
    private int? FindMatchingSpeaker(VoiceFingerprint fingerprint)
    {
        int? bestSpeaker = null;
        float bestSimilarity = SimilarityThreshold;

        foreach (var (speakerId, knownFp) in _speakerFingerprints)
        {
            var similarity = knownFp.SimilarityTo(fingerprint);
            if (similarity > bestSimilarity)
            {
                bestSimilarity = similarity;
                bestSpeaker = speakerId;
            }
        }

        return bestSpeaker;
    }

    /// <summary>
    /// Update a speaker's fingerprint using exponential moving average.
    /// </summary>
    private void UpdateSpeakerFingerprint(int speakerId, VoiceFingerprint newFp)
    {
        if (!_speakerFingerprints.TryGetValue(speakerId, out var existing))
        {
            _speakerFingerprints[speakerId] = newFp;
            return;
        }

        // Exponential moving average (alpha = 0.3 for new samples)
        const float alpha = 0.3f;
        _speakerFingerprints[speakerId] = new VoiceFingerprint(
            existing.SpectralCentroid * (1 - alpha) + newFp.SpectralCentroid * alpha,
            existing.ZeroCrossingRate * (1 - alpha) + newFp.ZeroCrossingRate * alpha,
            existing.RmsEnergy * (1 - alpha) + newFp.RmsEnergy * alpha,
            existing.PitchEstimateHz * (1 - alpha) + newFp.PitchEstimateHz * alpha);
    }

    private void TryAssociateCallsign(string text, int speakerId)
    {
        // Already identified this speaker
        if (_speakerCallsigns.TryGetValue(speakerId, out var existing) && existing != null)
            return;

        string? callsign = null;

        // Check for self-identification patterns
        var match = SelfIdentPattern.Match(text);
        if (match.Success)
        {
            var afterPattern = match.Groups[1].Value;
            var candidates = CallsignDetector.ExtractCallsignsWithPhonetics(afterPattern);
            if (candidates.Count > 0)
                callsign = candidates[0];
        }

        // Also try direct extraction when text contains self-ID phrases
        if (callsign == null)
        {
            var directCalls = CallsignDetector.ExtractCallsignsWithPhonetics(text);
            if (directCalls.Count == 1)
            {
                var upper = text.ToUpperInvariant();
                if (upper.Contains("THIS IS") || upper.Contains("I'M ") || upper.Contains("I AM ") ||
                    upper.Contains(" DE ") || upper.Contains("MY CALL"))
                {
                    callsign = directCalls[0];
                }
            }
        }

        if (callsign == null) return;

        // Skip our own callsign
        if (_myCallsign != null && callsign.Equals(_myCallsign, StringComparison.OrdinalIgnoreCase))
            return;

        // Don't reassign if callsign already belongs to another speaker
        if (_callsignToSpeaker.TryGetValue(callsign, out var existingSpeaker) && existingSpeaker != speakerId)
            return;

        _speakerCallsigns[speakerId] = callsign;
        _callsignToSpeaker[callsign] = speakerId;
    }

    public string GetSpeakerLabel(int speakerId)
    {
        if (_speakerCallsigns.TryGetValue(speakerId, out var call) && call != null)
            return call;
        return $"Speaker{speakerId}";
    }

    private static ConsoleColor GetSpeakerColor(int speakerId)
    {
        return (speakerId % 6) switch
        {
            1 => ConsoleColor.Green,
            2 => ConsoleColor.Yellow,
            3 => ConsoleColor.Cyan,
            4 => ConsoleColor.Magenta,
            5 => ConsoleColor.Blue,
            0 => ConsoleColor.Red,
            _ => ConsoleColor.White
        };
    }

    public Dictionary<string, string?> GetSpeakerSummary()
    {
        return _speakerCallsigns.ToDictionary(
            kv => $"Speaker{kv.Key}",
            kv => kv.Value);
    }
}

public record SpeakerLine(DateTime Timestamp, string Speaker, string Text, ConsoleColor Color);
