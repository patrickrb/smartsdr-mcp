namespace SmartSdrMcp.CwNeural;

public record NeuralParseResult(
    string CleanedText,
    List<string> DetectedCallsigns,
    bool IsCq,
    string? StationCallsign,
    NeuralMessageType MessageType);

public enum NeuralMessageType { Unknown, CqCall, DirectCall, Exchange, Closing }
