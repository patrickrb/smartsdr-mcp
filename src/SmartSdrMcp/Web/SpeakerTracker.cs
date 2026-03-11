using System.Text.RegularExpressions;
using SmartSdrMcp.Qso;
using SmartSdrMcp.Ssb;

namespace SmartSdrMcp.Web;

public class SpeakerTracker
{
    private const float SimilarityThreshold = 0.70f;
    private readonly TimeSpan _speakerChangeGap;
    private readonly string? _myCallsign;

    private int _nextSpeakerId = 1;
    private int _currentSpeakerId;
    private DateTime _lastSegmentTime = DateTime.MinValue;

    private readonly Dictionary<int, VoiceFingerprint> _speakerFingerprints = new();
    private readonly Dictionary<int, string?> _speakerCallsigns = new();
    private readonly Dictionary<string, int> _callsignToSpeaker = new(StringComparer.OrdinalIgnoreCase);

    private int _processedSegmentCount;

    private static readonly Regex SelfIdentPattern = new(
        @"(?:THIS\s+IS|MY\s+CALL(?:\s*SIGN)?\s+IS|I(?:'M|\s+AM)|DE)\s+(.+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public SpeakerTracker(TimeSpan? speakerChangeGap = null, string? myCallsign = null)
    {
        _speakerChangeGap = speakerChangeGap ?? TimeSpan.FromSeconds(2);
        _myCallsign = myCallsign?.ToUpperInvariant();
    }

    public List<(DateTime Timestamp, string Speaker, string Text)> ProcessNewSegments(List<TranscribedSegment> allSegments)
    {
        var results = new List<(DateTime, string, string)>();

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
                _currentSpeakerId = CreateSpeaker(segment.Fingerprint);
            }
            else if (gapDetected && segment.Fingerprint != null)
            {
                var matchedSpeaker = FindMatchingSpeaker(segment.Fingerprint);
                if (matchedSpeaker.HasValue)
                {
                    _currentSpeakerId = matchedSpeaker.Value;
                    UpdateSpeakerFingerprint(_currentSpeakerId, segment.Fingerprint);
                }
                else
                {
                    _currentSpeakerId = CreateSpeaker(segment.Fingerprint);
                }
            }
            else if (segment.Fingerprint != null && _speakerFingerprints.ContainsKey(_currentSpeakerId))
            {
                var currentFp = _speakerFingerprints[_currentSpeakerId];
                var similarity = currentFp.SimilarityTo(segment.Fingerprint);
                if (similarity < SimilarityThreshold * 0.8f)
                {
                    var matchedSpeaker = FindMatchingSpeaker(segment.Fingerprint);
                    if (matchedSpeaker.HasValue)
                        _currentSpeakerId = matchedSpeaker.Value;
                    else
                        _currentSpeakerId = CreateSpeaker(segment.Fingerprint);
                }
                else
                {
                    UpdateSpeakerFingerprint(_currentSpeakerId, segment.Fingerprint);
                }
            }

            _lastSegmentTime = segment.Timestamp;
            TryAssociateCallsign(segment.Text, _currentSpeakerId);

            var label = GetSpeakerLabel(_currentSpeakerId);
            results.Add((segment.Timestamp, label, segment.Text));
        }

        return results;
    }

    /// <summary>
    /// Get speaker label for a segment by matching its fingerprint against known speakers.
    /// Used for bulk labeling without tracking new/processed state.
    /// </summary>
    public string LabelSegment(TranscribedSegment segment)
    {
        if (segment.Fingerprint == null)
            return "Unknown";

        var match = FindMatchingSpeaker(segment.Fingerprint);
        if (match.HasValue)
            return GetSpeakerLabel(match.Value);

        return "Unknown";
    }

    private int CreateSpeaker(VoiceFingerprint? fingerprint)
    {
        int id = _nextSpeakerId++;
        _speakerCallsigns[id] = null;
        if (fingerprint != null)
            _speakerFingerprints[id] = fingerprint;
        return id;
    }

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

    private void UpdateSpeakerFingerprint(int speakerId, VoiceFingerprint newFp)
    {
        if (!_speakerFingerprints.TryGetValue(speakerId, out var existing))
        {
            _speakerFingerprints[speakerId] = newFp;
            return;
        }

        const float alpha = 0.3f;
        _speakerFingerprints[speakerId] = new VoiceFingerprint(
            existing.SpectralCentroid * (1 - alpha) + newFp.SpectralCentroid * alpha,
            existing.ZeroCrossingRate * (1 - alpha) + newFp.ZeroCrossingRate * alpha,
            existing.RmsEnergy * (1 - alpha) + newFp.RmsEnergy * alpha,
            existing.PitchEstimateHz * (1 - alpha) + newFp.PitchEstimateHz * alpha);
    }

    private void TryAssociateCallsign(string text, int speakerId)
    {
        if (_speakerCallsigns.TryGetValue(speakerId, out var existing) && existing != null)
            return;

        string? callsign = null;

        var match = SelfIdentPattern.Match(text);
        if (match.Success)
        {
            var afterPattern = match.Groups[1].Value;
            var candidates = CallsignDetector.ExtractCallsignsWithPhonetics(afterPattern);
            if (candidates.Count > 0)
                callsign = candidates[0];
        }

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

        if (_myCallsign != null && callsign.Equals(_myCallsign, StringComparison.OrdinalIgnoreCase))
            return;

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
}
