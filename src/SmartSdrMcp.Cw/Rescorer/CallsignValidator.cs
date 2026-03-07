using System.Text.RegularExpressions;

namespace SmartSdrMcp.Cw.Rescorer;

public static partial class CallsignValidator
{
    // International callsign format: 1-2 letter prefix + digit + 1-3 letter suffix
    // Examples: W1ABC, K1AF, VE3XYZ, G4ABC, JA1XX, DL1ABC
    [GeneratedRegex(@"^[A-Z]{1,2}\d[A-Z]{1,3}$")]
    private static partial Regex StandardCallsignRegex();

    // Extended format with number prefix: 2E0ABC, 3DA0XYZ
    [GeneratedRegex(@"^(\d?[A-Z]{1,2}\d+[A-Z]{1,4})$")]
    private static partial Regex ExtendedCallsignRegex();

    // Portable suffixes: W1ABC/P, W1ABC/M, W1ABC/QRP
    [GeneratedRegex(@"^(\d?[A-Z]{1,2}\d+[A-Z]{1,4})(/[A-Z0-9]{1,4})?$")]
    private static partial Regex PortableCallsignRegex();

    public static bool IsValidCallsign(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 3 || text.Length > 10)
            return false;

        return PortableCallsignRegex().IsMatch(text.ToUpper());
    }

    public static double ScoreCallsign(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;

        text = text.ToUpper().Trim();
        if (text.Length < 3) return 0;

        if (StandardCallsignRegex().IsMatch(text)) return 1.0;
        if (ExtendedCallsignRegex().IsMatch(text)) return 0.9;
        if (PortableCallsignRegex().IsMatch(text)) return 0.85;

        return 0;
    }
}
