namespace SmartSdrMcp.Qso;

public record CwMessage(
    DateTime Timestamp,
    double FrequencyMHz,
    string DecodedText,
    double Confidence,
    string? DetectedCallsign,
    bool IsCq)
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
}
