namespace SmartSdrMcp.Cw.Decoder;

public enum MorseElement { Dit, Dah, CharGap, WordGap }

public record SymbolCandidate(
    MorseElement Element,
    double Confidence,
    MorseElement? Alternative = null,
    double AltConfidence = 0);

/// <summary>
/// An alternative character interpretation with its dit-dah pattern and score.
/// </summary>
public record CharacterCandidate(
    string DitDahPattern,
    string Character,
    double Score);
