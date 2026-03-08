using System.Globalization;
using System.Net.Http;

namespace SmartSdrMcp.Contest;

public record ClusterSpot(
    string Spotter,
    double FrequencyKhz,
    string DxCall,
    string? Comment,
    DateTime SpottedUtc,
    string? Band,
    string? Country);

public class ClusterClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private const string BaseUrl = "https://www.hamqth.com/dxc_csv.php";

    /// <summary>
    /// Query DX cluster spots and find stations near the given frequency.
    /// </summary>
    /// <param name="frequencyMhz">Current VFO frequency in MHz</param>
    /// <param name="toleranceKhz">How close a spot must be to match (default 2 kHz)</param>
    /// <returns>Matching spots sorted by most recent first</returns>
    public async Task<List<ClusterSpot>> GetSpotsNearFrequencyAsync(double frequencyMhz, double toleranceKhz = 2.0)
    {
        var band = FrequencyToBand(frequencyMhz);
        if (band == null)
            return [];

        var frequencyKhz = frequencyMhz * 1000.0;

        try
        {
            var url = $"{BaseUrl}?limit=50&band={band}";
            var response = await Http.GetStringAsync(url);
            var spots = ParseSpots(response);

            return spots
                .Where(s => Math.Abs(s.FrequencyKhz - frequencyKhz) <= toleranceKhz)
                .OrderByDescending(s => s.SpottedUtc)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static List<ClusterSpot> ParseSpots(string csv)
    {
        var spots = new List<ClusterSpot>();

        foreach (var line in csv.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split('^');
            if (fields.Length < 5) continue;

            if (!double.TryParse(fields[1], CultureInfo.InvariantCulture, out var freq)) continue;

            var call = fields[2].Trim();
            if (string.IsNullOrEmpty(call)) continue;

            // Parse datetime: "0053 2026-03-08"
            DateTime spotted = DateTime.UtcNow;
            var dtParts = fields[4].Trim().Split(' ');
            if (dtParts.Length == 2 && dtParts[0].Length == 4)
            {
                if (DateTime.TryParse(dtParts[1], out var date) &&
                    int.TryParse(dtParts[0][..2], out var hour) &&
                    int.TryParse(dtParts[0][2..], out var min))
                {
                    spotted = date.AddHours(hour).AddMinutes(min);
                }
            }

            spots.Add(new ClusterSpot(
                Spotter: fields[0].Trim(),
                FrequencyKhz: freq,
                DxCall: call,
                Comment: fields.Length > 3 ? fields[3].Trim() : null,
                SpottedUtc: spotted,
                Band: fields.Length > 8 ? fields[8].Trim() : null,
                Country: fields.Length > 9 ? fields[9].Trim() : null));
        }

        return spots;
    }

    private static string? FrequencyToBand(double mhz) => mhz switch
    {
        >= 1.8 and < 2.0 => "160m",
        >= 3.5 and < 4.0 => "80m",
        >= 5.0 and < 6.0 => "60m",
        >= 7.0 and < 7.3 => "40m",
        >= 10.1 and < 10.15 => "30m",
        >= 14.0 and < 14.35 => "20m",
        >= 18.068 and < 18.168 => "17m",
        >= 21.0 and < 21.45 => "15m",
        >= 24.89 and < 24.99 => "12m",
        >= 28.0 and < 29.7 => "10m",
        >= 50.0 and < 54.0 => "6m",
        _ => null
    };
}
