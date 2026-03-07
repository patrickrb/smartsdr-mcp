namespace SmartSdrMcp.Cw.Decoder;

public enum MorseElement { Dit, Dah, CharGap, WordGap }

public record SymbolCandidate(
    MorseElement Element,
    double Confidence,
    MorseElement? Alternative = null,
    double AltConfidence = 0);
