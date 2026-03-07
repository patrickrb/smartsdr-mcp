namespace SmartSdrMcp.Cw.Rescorer;

/// <summary>
/// Applies ham radio context to improve CW decode accuracy.
/// </summary>
public class HamRescorer
{
    public string Rescore(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return rawText;

        var result = rawText;

        // Apply common confusion corrections
        foreach (var (wrong, correct) in CwPatterns.CommonConfusions)
        {
            // Only apply at word boundaries
            result = ReplaceAtWordBoundary(result, wrong, correct);
        }

        return result;
    }

    public (string Text, double Confidence) RescoreWithConfidence(string rawText, double baseConfidence)
    {
        var rescored = Rescore(rawText);
        double boost = 0;

        // Boost confidence if we find known patterns
        var words = rescored.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var word in words)
        {
            if (CwPatterns.CommonPatterns.ContainsKey(word))
                boost += 0.05;
            if (CallsignValidator.IsValidCallsign(word))
                boost += 0.1;
        }

        // Check for CQ DE pattern
        if (rescored.Contains("CQ") && rescored.Contains("DE"))
            boost += 0.1;

        double finalConfidence = Math.Min(1.0, baseConfidence + boost);
        return (rescored, finalConfidence);
    }

    private static string ReplaceAtWordBoundary(string text, string wrong, string correct)
    {
        var words = text.Split(' ');
        for (int i = 0; i < words.Length; i++)
        {
            if (words[i].Equals(wrong, StringComparison.OrdinalIgnoreCase))
            {
                words[i] = correct;
            }
        }
        return string.Join(' ', words);
    }
}
