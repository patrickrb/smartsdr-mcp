namespace SmartSdrMcp.Ssb;

public record TranscribedSegment(
    DateTime Timestamp,
    string Text,
    bool IsFinal,
    VoiceFingerprint? Fingerprint = null);
