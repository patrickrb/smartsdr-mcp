using System.Text.RegularExpressions;

namespace SmartSdrMcp.Qso;

public static partial class CallsignDetector
{
    [GeneratedRegex(@"\b(\d?[A-Z]{1,2}\d+[A-Z]{1,4})\b")]
    private static partial Regex CallsignRegex();

    public static List<string> ExtractCallsigns(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        return CallsignRegex().Matches(text.ToUpper())
            .Select(m => m.Groups[1].Value)
            .Where(c => c.Length >= 3 && c.Length <= 8)
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// Extract the "other station" callsign from a CQ or DE pattern.
    /// </summary>
    public static string? ExtractStationCallsign(string text, string myCallsign)
    {
        var upper = text.ToUpper();
        var callsigns = ExtractCallsigns(upper);

        // Remove our own callsign
        callsigns.RemoveAll(c => c.Equals(myCallsign, StringComparison.OrdinalIgnoreCase));

        // Prefer callsign after "DE"
        var deIndex = upper.IndexOf("DE ", StringComparison.Ordinal);
        if (deIndex >= 0)
        {
            var afterDe = upper[(deIndex + 3)..].TrimStart();
            var deCallsigns = ExtractCallsigns(afterDe);
            if (deCallsigns.Count > 0) return deCallsigns[0];
        }

        return callsigns.FirstOrDefault();
    }

    public static bool IsCqMessage(string text)
    {
        return text.ToUpper().TrimStart().StartsWith("CQ");
    }
}
