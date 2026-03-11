using System.Text;
using System.Text.RegularExpressions;
using SmartSdrMcp.Qso;

namespace SmartSdrMcp.CwNeural;

/// <summary>
/// Post-processes raw CTC decoder output to coalesce fragmented characters
/// back into CW words and callsigns.
/// </summary>
public static partial class CtcPostProcessor
{
    // 2-char CW keywords (order doesn't matter — matched greedily)
    private static readonly HashSet<string> TwoCharKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "CQ", "DE", "ES", "FB", "GA", "GE", "GM", "HW", "HR", "OM",
        "TU", "UR", "WX", "BK", "SK", "KN", "AR", "BT", "HH"
    };

    // 3+ char CW keywords
    private static readonly HashSet<string> LongKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "QTH", "RST", "QSL", "QRZ", "QSO", "QRP", "QRM", "QRN", "QSY",
        "AGN", "PSE", "CPY", "NAME", "RIG"
    };

    // Common number groups that should stay as-is
    private static readonly HashSet<string> NumberGroups = new() { "73", "88", "599", "5NN" };

    [GeneratedRegex(@"^\d?[A-Z]{1,2}\d+[A-Z]{1,4}$")]
    private static partial Regex CallsignPattern();

    [GeneratedRegex(@"^[1-5][1-9][1-9]$")]
    private static partial Regex SignalReportPattern();

    /// <summary>
    /// Coalesce fragmented CTC output back into proper CW words.
    /// E.g. "C Q C Q D E W B 1 E C P K" → "CQ CQ DE WB1ECP K"
    /// </summary>
    public static string CoalesceText(string rawCtcOutput)
    {
        if (string.IsNullOrWhiteSpace(rawCtcOutput))
            return "";

        var tokens = rawCtcOutput.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return "";

        var result = new List<string>();
        int i = 0;

        while (i < tokens.Length)
        {
            // Try matching known keywords starting from longest
            string? matched = TryMatchKeyword(tokens, i);
            if (matched != null)
            {
                result.Add(matched);
                i += matched.Length; // each char was a separate token
                continue;
            }

            // Try matching signal report (3 consecutive digit tokens)
            if (i + 2 < tokens.Length &&
                tokens[i].Length == 1 && char.IsDigit(tokens[i][0]) &&
                tokens[i + 1].Length == 1 && char.IsDigit(tokens[i + 1][0]) &&
                tokens[i + 2].Length == 1 && char.IsDigit(tokens[i + 2][0]))
            {
                var report = tokens[i] + tokens[i + 1] + tokens[i + 2];
                if (SignalReportPattern().IsMatch(report))
                {
                    result.Add(report);
                    i += 3;
                    continue;
                }
            }

            // Check if current token is already a multi-char word (not fragmented)
            if (tokens[i].Length > 1)
            {
                result.Add(tokens[i]);
                i++;
                continue;
            }

            // Single-char token: coalesce consecutive single-char tokens
            var coalesced = new StringBuilder();
            int start = i;
            while (i < tokens.Length && tokens[i].Length == 1)
            {
                // Before blindly consuming, check if upcoming chars form a keyword
                string? upcoming = TryMatchKeyword(tokens, i);
                if (upcoming != null && coalesced.Length > 0)
                {
                    // Flush what we have so far, then let the keyword match happen
                    break;
                }
                if (upcoming != null && coalesced.Length == 0)
                {
                    result.Add(upcoming);
                    i += upcoming.Length;
                    goto continueOuter;
                }

                coalesced.Append(tokens[i]);
                i++;
            }

            if (coalesced.Length > 0)
            {
                var candidate = coalesced.ToString();
                // Check if it's a callsign
                if (CallsignPattern().IsMatch(candidate) ||
                    NumberGroups.Contains(candidate))
                {
                    result.Add(candidate);
                }
                else
                {
                    // May be a partial callsign or garbled text — emit as-is
                    result.Add(candidate);
                }
            }

            continueOuter:;
        }

        return string.Join(" ", result);
    }

    /// <summary>
    /// Try to match a known CW keyword starting at position i in the token array.
    /// Returns the keyword if matched, null otherwise.
    /// </summary>
    private static string? TryMatchKeyword(string[] tokens, int i)
    {
        // Try longest keywords first (up to 4 chars)
        for (int len = Math.Min(4, tokens.Length - i); len >= 2; len--)
        {
            // All tokens in this span must be single characters
            bool allSingle = true;
            for (int j = i; j < i + len; j++)
            {
                if (tokens[j].Length != 1) { allSingle = false; break; }
            }
            if (!allSingle) continue;

            var candidate = string.Concat(tokens.Skip(i).Take(len));

            if (len >= 3 && LongKeywords.Contains(candidate))
                return candidate;
            if (len == 2 && TwoCharKeywords.Contains(candidate))
                return candidate;
        }

        return null;
    }

    /// <summary>
    /// Full parse: coalesce text, extract callsigns, determine message type.
    /// </summary>
    public static NeuralParseResult Parse(string rawCtcOutput, string myCallsign)
    {
        var cleaned = CoalesceText(rawCtcOutput);
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return new NeuralParseResult(
                "", [], false, null, NeuralMessageType.Unknown);
        }

        var upper = cleaned.ToUpper();
        var callsigns = CallsignDetector.ExtractCallsigns(upper);
        var isCq = upper.TrimStart().StartsWith("CQ");
        var stationCallsign = CallsignDetector.ExtractStationCallsign(upper, myCallsign);

        var messageType = ClassifyMessage(upper, myCallsign);

        return new NeuralParseResult(
            cleaned, callsigns, isCq, stationCallsign, messageType);
    }

    private static NeuralMessageType ClassifyMessage(string upper, string myCallsign)
    {
        if (upper.TrimStart().StartsWith("CQ"))
            return NeuralMessageType.CqCall;

        if (upper.Contains(myCallsign, StringComparison.OrdinalIgnoreCase))
            return NeuralMessageType.DirectCall;

        // Exchange indicators: RST pattern, NAME, QTH
        if (SignalReportPattern().IsMatch(ExtractFirstThreeDigits(upper)) ||
            upper.Contains("NAME") || upper.Contains("QTH") ||
            upper.Contains("RST"))
            return NeuralMessageType.Exchange;

        if (upper.Contains("73") || upper.Contains("SK") || upper.Contains("TU"))
            return NeuralMessageType.Closing;

        return NeuralMessageType.Unknown;
    }

    private static string ExtractFirstThreeDigits(string text)
    {
        var sb = new StringBuilder();
        foreach (var c in text)
        {
            if (char.IsDigit(c))
            {
                sb.Append(c);
                if (sb.Length == 3) return sb.ToString();
            }
            else if (sb.Length > 0)
            {
                sb.Clear(); // reset — digits must be consecutive
            }
        }
        return "";
    }
}
