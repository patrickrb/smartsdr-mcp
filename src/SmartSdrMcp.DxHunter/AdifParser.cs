using System.Globalization;
using System.Text.RegularExpressions;

namespace SmartSdrMcp.DxHunter;

/// <summary>
/// Parses ADIF (.adi) files to extract worked callsigns, bands, and modes.
/// </summary>
public static partial class AdifParser
{
    [GeneratedRegex(@"<(\w+):(\d+)(?::\w+)?>", RegexOptions.IgnoreCase)]
    private static partial Regex FieldRegex();

    [GeneratedRegex(@"<EOR>", RegexOptions.IgnoreCase)]
    private static partial Regex EorRegex();

    [GeneratedRegex(@"<EOH>", RegexOptions.IgnoreCase)]
    private static partial Regex EohRegex();

    public static List<AdifRecord> Parse(string adifContent)
    {
        var records = new List<AdifRecord>();

        // Skip header (everything before <EOH>)
        var eohMatch = EohRegex().Match(adifContent);
        int startPos = eohMatch.Success ? eohMatch.Index + eohMatch.Length : 0;

        // Split by <EOR>
        var eorMatches = EorRegex().Matches(adifContent[startPos..]);
        int lastEnd = 0;

        foreach (Match eor in eorMatches)
        {
            var recordText = adifContent.Substring(startPos + lastEnd, eor.Index - lastEnd);
            lastEnd = eor.Index + eor.Length;

            var fields = ParseFields(recordText);
            if (fields.Count == 0) continue;

            var callsign = GetField(fields, "CALL");
            if (string.IsNullOrWhiteSpace(callsign)) continue;

            var freq = GetField(fields, "FREQ");
            var band = GetField(fields, "BAND");
            var mode = GetField(fields, "MODE");
            var dateStr = GetField(fields, "QSO_DATE");
            var dxcc = GetField(fields, "DXCC");

            double freqMHz = 0;
            if (!string.IsNullOrEmpty(freq))
                double.TryParse(freq, CultureInfo.InvariantCulture, out freqMHz);

            DateTime? qsoDate = null;
            if (!string.IsNullOrEmpty(dateStr) && dateStr.Length >= 8)
                DateTime.TryParseExact(dateStr[..8], "yyyyMMdd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var d).Equals(true).ToString();
            if (!string.IsNullOrEmpty(dateStr) && dateStr.Length >= 8 &&
                DateTime.TryParseExact(dateStr[..8], "yyyyMMdd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date))
                qsoDate = date;

            int dxccNum = 0;
            if (!string.IsNullOrEmpty(dxcc))
                int.TryParse(dxcc, out dxccNum);

            records.Add(new AdifRecord(
                Callsign: callsign.ToUpperInvariant(),
                FrequencyMHz: freqMHz,
                Band: NormalizeBand(band, freqMHz),
                Mode: NormalizeMode(mode),
                QsoDate: qsoDate,
                DxccNumber: dxccNum));
        }

        return records;
    }

    private const long MaxFileSizeBytes = 100 * 1024 * 1024; // 100 MB

    public static List<AdifRecord> ParseFile(string filePath)
    {
        var fileInfo = new FileInfo(filePath);
        if (fileInfo.Length > MaxFileSizeBytes)
            throw new InvalidOperationException($"ADIF file is too large ({fileInfo.Length / (1024 * 1024)} MB). Maximum is {MaxFileSizeBytes / (1024 * 1024)} MB.");

        var content = File.ReadAllText(filePath);
        return Parse(content);
    }

    private static Dictionary<string, string> ParseFields(string text)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var matches = FieldRegex().Matches(text);

        foreach (Match match in matches)
        {
            var name = match.Groups[1].Value.ToUpperInvariant();
            var length = int.Parse(match.Groups[2].Value);
            var valueStart = match.Index + match.Length;

            if (valueStart + length <= text.Length)
            {
                fields[name] = text.Substring(valueStart, length);
            }
        }

        return fields;
    }

    private static string GetField(Dictionary<string, string> fields, string name) =>
        fields.TryGetValue(name, out var val) ? val.Trim() : "";

    private static string NormalizeBand(string? band, double freqMHz)
    {
        if (!string.IsNullOrEmpty(band))
            return band.ToUpperInvariant().Replace("M", "m");

        // Derive from frequency
        return freqMHz switch
        {
            >= 1.8 and <= 2.0 => "160m",
            >= 3.5 and <= 4.0 => "80m",
            >= 5.3 and <= 5.5 => "60m",
            >= 7.0 and <= 7.3 => "40m",
            >= 10.1 and <= 10.15 => "30m",
            >= 14.0 and <= 14.35 => "20m",
            >= 18.068 and <= 18.168 => "17m",
            >= 21.0 and <= 21.45 => "15m",
            >= 24.89 and <= 24.99 => "12m",
            >= 28.0 and <= 29.7 => "10m",
            >= 50.0 and <= 54.0 => "6m",
            _ => "?"
        };
    }

    private static string NormalizeMode(string? mode)
    {
        if (string.IsNullOrEmpty(mode)) return "?";
        return mode.ToUpperInvariant() switch
        {
            "SSB" or "USB" or "LSB" or "PHONE" => "SSB",
            "CW" or "CWR" => "CW",
            "FT8" or "FT4" or "JS8" or "RTTY" or "PSK31" or "PSK63" => mode.ToUpperInvariant(),
            _ => mode.ToUpperInvariant()
        };
    }
}

public record AdifRecord(
    string Callsign,
    double FrequencyMHz,
    string Band,
    string Mode,
    DateTime? QsoDate,
    int DxccNumber);
