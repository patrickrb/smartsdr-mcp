using System.Text;
using System.Text.RegularExpressions;

namespace SmartSdrMcp.Qso;

public static partial class CallsignDetector
{
    [GeneratedRegex(@"\b(\d?[A-Z]{1,2}\d+[A-Z]{1,4})\b")]
    private static partial Regex CallsignRegex();

    /// <summary>
    /// NATO phonetics and common Whisper mishearings → character mapping.
    /// </summary>
    private static readonly Dictionary<string, char> PhoneticToChar = new(StringComparer.OrdinalIgnoreCase)
    {
        // Standard NATO
        ["ALPHA"] = 'A', ["BRAVO"] = 'B', ["CHARLIE"] = 'C', ["DELTA"] = 'D',
        ["ECHO"] = 'E', ["FOXTROT"] = 'F', ["GOLF"] = 'G', ["HOTEL"] = 'H',
        ["INDIA"] = 'I', ["JULIETT"] = 'J', ["JULIET"] = 'J', ["KILO"] = 'K',
        ["LIMA"] = 'L', ["MIKE"] = 'M', ["NOVEMBER"] = 'N', ["OSCAR"] = 'O',
        ["PAPA"] = 'P', ["QUEBEC"] = 'Q', ["ROMEO"] = 'R', ["SIERRA"] = 'S',
        ["TANGO"] = 'T', ["UNIFORM"] = 'U', ["VICTOR"] = 'V', ["WHISKEY"] = 'W',
        ["WHISKY"] = 'W', ["XRAY"] = 'X', ["X-RAY"] = 'X', ["YANKEE"] = 'Y',
        ["ZULU"] = 'Z',

        // Common Whisper mishearings / alternate spellings
        ["ALFA"] = 'A', ["BROVO"] = 'B', ["CHARLEY"] = 'C',
        ["FOXTRAP"] = 'F', ["FOXTROTT"] = 'F',
        ["INDYA"] = 'I', ["JULLIETT"] = 'J',
        ["KILLOH"] = 'K', ["KEELO"] = 'K',
        ["LEEMA"] = 'L', ["LEMA"] = 'L',
        ["OSKAR"] = 'O', ["PAPPA"] = 'P',
        ["KEBEC"] = 'Q', ["KWEE-BEC"] = 'Q',
        ["SIERA"] = 'S', ["SEARA"] = 'S',
        ["VIKTORIA"] = 'V', ["WISKY"] = 'W',
        ["EKKO"] = 'E', ["GOLFF"] = 'G',

        // Number words
        ["ZERO"] = '0', ["OH"] = '0',
        ["ONE"] = '1', ["WUN"] = '1',
        ["TWO"] = '2', ["TOO"] = '2',
        ["THREE"] = '3', ["TREE"] = '3',
        ["FOUR"] = '4', ["FOWER"] = '4',
        ["FIVE"] = '5', ["FIFE"] = '5',
        ["SIX"] = '6',
        ["SEVEN"] = '7',
        ["EIGHT"] = '8', ["AIT"] = '8',
        ["NINE"] = '9', ["NINER"] = '9',
    };

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
    /// Convert a string containing NATO phonetic words into a condensed callsign string.
    /// E.g. "Kilo One Alpha Foxtrot" → "K1AF"
    /// </summary>
    public static string ConvertPhoneticsToCallsign(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        var sb = new StringBuilder();
        foreach (var token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            // Strip punctuation from token edges
            var clean = token.Trim(',', '.', '!', '?', ';', ':');
            if (PhoneticToChar.TryGetValue(clean, out var ch))
                sb.Append(ch);
            else if (clean.Length == 1 && char.IsLetterOrDigit(clean[0]))
                sb.Append(char.ToUpperInvariant(clean[0]));
            // Skip unrecognized multi-char tokens (filler words like "the", "is", etc.)
        }

        return sb.ToString();
    }

    /// <summary>
    /// Extract callsigns using both raw regex and phonetic conversion.
    /// Tries direct regex first; if nothing found, converts phonetics then retries.
    /// </summary>
    public static List<string> ExtractCallsignsWithPhonetics(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        // Stage 1: direct regex on raw text
        var direct = ExtractCallsigns(text);
        if (direct.Count > 0) return direct;

        // Stage 2: convert phonetics then try regex
        var converted = ConvertPhoneticsToCallsign(text);
        if (converted.Length >= 3)
        {
            var fromPhonetic = ExtractCallsigns(converted);
            if (fromPhonetic.Count > 0) return fromPhonetic;
        }

        return [];
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

    /// <summary>
    /// Reverse mapping: character → NATO phonetic word (for speaking back).
    /// </summary>
    private static readonly Dictionary<char, string> CharToPhonetic = new()
    {
        ['A'] = "Alpha", ['B'] = "Bravo", ['C'] = "Charlie", ['D'] = "Delta",
        ['E'] = "Echo", ['F'] = "Foxtrot", ['G'] = "Golf", ['H'] = "Hotel",
        ['I'] = "India", ['J'] = "Juliett", ['K'] = "Kilo", ['L'] = "Lima",
        ['M'] = "Mike", ['N'] = "November", ['O'] = "Oscar", ['P'] = "Papa",
        ['Q'] = "Quebec", ['R'] = "Romeo", ['S'] = "Sierra", ['T'] = "Tango",
        ['U'] = "Uniform", ['V'] = "Victor", ['W'] = "Whiskey", ['X'] = "Xray",
        ['Y'] = "Yankee", ['Z'] = "Zulu",
        ['0'] = "Zero", ['1'] = "One", ['2'] = "Two", ['3'] = "Three",
        ['4'] = "Four", ['5'] = "Five", ['6'] = "Six", ['7'] = "Seven",
        ['8'] = "Eight", ['9'] = "Niner",
    };

    /// <summary>
    /// Extract whatever phonetic characters we can from the text, even if they
    /// don't form a valid callsign. Returns the partial characters as a string
    /// and their phonetic pronunciation.
    /// E.g. "Kilo something One" → ("K1", "Kilo One")
    /// </summary>
    public static (string Chars, string Phonetic) ExtractPartialPhonetics(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return ("", "");

        var chars = new StringBuilder();
        var phonetic = new StringBuilder();

        foreach (var token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var clean = token.Trim(',', '.', '!', '?', ';', ':');
            if (PhoneticToChar.TryGetValue(clean, out var ch))
            {
                chars.Append(ch);
                if (phonetic.Length > 0) phonetic.Append(' ');
                // Use the canonical NATO word for speaking back
                if (CharToPhonetic.TryGetValue(ch, out var canonical))
                    phonetic.Append(canonical);
                else
                    phonetic.Append(ch);
            }
            else if (clean.Length == 1 && char.IsLetterOrDigit(clean[0]))
            {
                var c = char.ToUpperInvariant(clean[0]);
                chars.Append(c);
                if (phonetic.Length > 0) phonetic.Append(' ');
                if (CharToPhonetic.TryGetValue(c, out var canonical))
                    phonetic.Append(canonical);
                else
                    phonetic.Append(c);
            }
        }

        return (chars.ToString(), phonetic.ToString());
    }

    public static bool IsCqMessage(string text)
    {
        return text.ToUpper().TrimStart().StartsWith("CQ");
    }
}
