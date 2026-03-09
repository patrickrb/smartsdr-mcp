using System.Text.Json;
using SmartSdrMcp.DxHunter;
using SmartSdrMcp.Tx;

namespace SmartSdrMcp.CqCaller;

/// <summary>
/// Looks up amateur radio license class from the FCC ULS API.
/// Caches results in-memory with 1-hour TTL.
/// Non-US callsigns default to Extra class.
/// </summary>
public class FccLicenseLookup
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly Dictionary<string, (LicenseClass Class, DateTime ExpiresAt)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _cacheLock = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

    private const string FccApiUrl = "https://data.fcc.gov/api/license-view/basicSearch/getLicenses";

    public async Task<LicenseClass?> LookupAsync(string callsign, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(callsign))
            return null;

        var call = callsign.Trim().ToUpperInvariant();

        // Check cache first
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(call, out var cached) && cached.ExpiresAt > DateTime.UtcNow)
                return cached.Class;
        }

        // Non-US callsigns default to Extra (no FCC lookup)
        var entity = DxccLookup.GetEntity(call);
        if (!IsUsEntity(entity))
        {
            CacheResult(call, LicenseClass.Extra);
            return LicenseClass.Extra;
        }

        try
        {
            var url = $"{FccApiUrl}?searchValue={Uri.EscapeDataString(call)}&format=json";
            var response = await _http.GetStringAsync(url, ct);
            var licenseClass = ParseFccResponse(response, call);

            if (licenseClass.HasValue)
            {
                CacheResult(call, licenseClass.Value);
                return licenseClass.Value;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FCC] Lookup failed for {call}: {ex.Message}");
        }

        // Default to Extra on failure
        CacheResult(call, LicenseClass.Extra);
        return LicenseClass.Extra;
    }

    private static LicenseClass? ParseFccResponse(string json, string callsign)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("Licenses", out var licenses))
                return null;
            if (!licenses.TryGetProperty("License", out var licenseArray))
                return null;

            foreach (var license in licenseArray.EnumerateArray())
            {
                // Must be Amateur service and Active
                var service = license.TryGetProperty("serviceDesc", out var svc) ? svc.GetString() : null;
                var status = license.TryGetProperty("statusDesc", out var stat) ? stat.GetString() : null;
                var licCall = license.TryGetProperty("callsign", out var lc) ? lc.GetString() : null;

                if (service == null || !service.Contains("Amateur", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (status == null || !status.Equals("Active", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (licCall == null || !licCall.Equals(callsign, StringComparison.OrdinalIgnoreCase))
                    continue;

                var category = license.TryGetProperty("categoryDesc", out var cat) ? cat.GetString() : null;
                if (category == null) continue;

                if (category.Contains("Extra", StringComparison.OrdinalIgnoreCase))
                    return LicenseClass.Extra;
                if (category.Contains("General", StringComparison.OrdinalIgnoreCase))
                    return LicenseClass.General;
                if (category.Contains("Technician", StringComparison.OrdinalIgnoreCase))
                    return LicenseClass.Technician;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FCC] JSON parse error: {ex.Message}");
        }

        return null;
    }

    private void CacheResult(string callsign, LicenseClass licenseClass)
    {
        lock (_cacheLock)
        {
            _cache[callsign] = (licenseClass, DateTime.UtcNow + CacheTtl);
        }
    }

    private static bool IsUsEntity(string entity)
    {
        return entity.Equals("United States", StringComparison.OrdinalIgnoreCase)
            || entity.Equals("Hawaii", StringComparison.OrdinalIgnoreCase)
            || entity.Equals("Alaska", StringComparison.OrdinalIgnoreCase)
            || entity.Equals("Puerto Rico", StringComparison.OrdinalIgnoreCase)
            || entity.Equals("US Virgin Islands", StringComparison.OrdinalIgnoreCase)
            || entity.Equals("Guam", StringComparison.OrdinalIgnoreCase)
            || entity.Equals("American Samoa", StringComparison.OrdinalIgnoreCase);
    }
}
