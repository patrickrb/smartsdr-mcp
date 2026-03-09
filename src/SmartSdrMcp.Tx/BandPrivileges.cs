namespace SmartSdrMcp.Tx;

public enum LicenseClass { Technician, General, Extra }

/// <summary>
/// US amateur band privileges per license class and mode.
/// Returns allowed sub-band ranges for CW and voice on HF bands (160m–6m).
/// </summary>
public static class BandPrivileges
{
    private record struct Privilege(double LowMHz, double HighMHz);

    // Key: (LicenseClass, band, mode) → allowed range
    // mode is "CW" or "VOICE"
    private static readonly Dictionary<(LicenseClass, string, string), Privilege> Privileges = new()
    {
        // 160m (1.8–2.0 MHz)
        { (LicenseClass.Extra, "160m", "CW"), new(1.800, 2.000) },
        { (LicenseClass.Extra, "160m", "VOICE"), new(1.800, 2.000) },
        { (LicenseClass.General, "160m", "CW"), new(1.800, 2.000) },
        { (LicenseClass.General, "160m", "VOICE"), new(1.800, 2.000) },

        // 80m (3.5–4.0 MHz)
        { (LicenseClass.Extra, "80m", "CW"), new(3.500, 3.600) },
        { (LicenseClass.Extra, "80m", "VOICE"), new(3.600, 4.000) },
        { (LicenseClass.General, "80m", "CW"), new(3.500, 3.600) },
        { (LicenseClass.General, "80m", "VOICE"), new(3.800, 4.000) },

        // 60m (5.3 MHz) — channelized, same for all with privileges
        { (LicenseClass.Extra, "60m", "CW"), new(5.330, 5.410) },
        { (LicenseClass.Extra, "60m", "VOICE"), new(5.330, 5.410) },
        { (LicenseClass.General, "60m", "CW"), new(5.330, 5.410) },
        { (LicenseClass.General, "60m", "VOICE"), new(5.330, 5.410) },

        // 40m (7.0–7.3 MHz)
        { (LicenseClass.Extra, "40m", "CW"), new(7.000, 7.125) },
        { (LicenseClass.Extra, "40m", "VOICE"), new(7.125, 7.300) },
        { (LicenseClass.General, "40m", "CW"), new(7.000, 7.125) },
        { (LicenseClass.General, "40m", "VOICE"), new(7.175, 7.300) },

        // 30m (10.1–10.15 MHz) — CW/data only, no voice
        { (LicenseClass.Extra, "30m", "CW"), new(10.100, 10.150) },
        { (LicenseClass.General, "30m", "CW"), new(10.100, 10.150) },

        // 20m (14.0–14.35 MHz)
        { (LicenseClass.Extra, "20m", "CW"), new(14.000, 14.150) },
        { (LicenseClass.Extra, "20m", "VOICE"), new(14.150, 14.350) },
        { (LicenseClass.General, "20m", "CW"), new(14.000, 14.150) },
        { (LicenseClass.General, "20m", "VOICE"), new(14.225, 14.350) },

        // 17m (18.068–18.168 MHz)
        { (LicenseClass.Extra, "17m", "CW"), new(18.068, 18.110) },
        { (LicenseClass.Extra, "17m", "VOICE"), new(18.110, 18.168) },
        { (LicenseClass.General, "17m", "CW"), new(18.068, 18.110) },
        { (LicenseClass.General, "17m", "VOICE"), new(18.110, 18.168) },

        // 15m (21.0–21.45 MHz)
        { (LicenseClass.Extra, "15m", "CW"), new(21.000, 21.200) },
        { (LicenseClass.Extra, "15m", "VOICE"), new(21.200, 21.450) },
        { (LicenseClass.General, "15m", "CW"), new(21.000, 21.200) },
        { (LicenseClass.General, "15m", "VOICE"), new(21.275, 21.450) },

        // 12m (24.89–24.99 MHz)
        { (LicenseClass.Extra, "12m", "CW"), new(24.890, 24.930) },
        { (LicenseClass.Extra, "12m", "VOICE"), new(24.930, 24.990) },
        { (LicenseClass.General, "12m", "CW"), new(24.890, 24.930) },
        { (LicenseClass.General, "12m", "VOICE"), new(24.930, 24.990) },

        // 10m (28.0–29.7 MHz) — all classes including Technician
        { (LicenseClass.Extra, "10m", "CW"), new(28.000, 28.300) },
        { (LicenseClass.Extra, "10m", "VOICE"), new(28.300, 29.700) },
        { (LicenseClass.General, "10m", "CW"), new(28.000, 28.300) },
        { (LicenseClass.General, "10m", "VOICE"), new(28.300, 29.700) },
        { (LicenseClass.Technician, "10m", "CW"), new(28.000, 28.300) },
        { (LicenseClass.Technician, "10m", "VOICE"), new(28.300, 28.500) },

        // 6m (50.0–54.0 MHz) — all classes including Technician
        { (LicenseClass.Extra, "6m", "CW"), new(50.000, 50.100) },
        { (LicenseClass.Extra, "6m", "VOICE"), new(50.100, 54.000) },
        { (LicenseClass.General, "6m", "CW"), new(50.000, 50.100) },
        { (LicenseClass.General, "6m", "VOICE"), new(50.100, 54.000) },
        { (LicenseClass.Technician, "6m", "CW"), new(50.000, 50.100) },
        { (LicenseClass.Technician, "6m", "VOICE"), new(50.100, 54.000) },
    };

    /// <summary>
    /// Returns the allowed sub-band for the given license class, band, and mode.
    /// Returns null if no privileges exist for that combination.
    /// </summary>
    public static (double LowMHz, double HighMHz)? GetRange(LicenseClass license, string band, string mode)
    {
        var normalizedMode = mode.ToUpperInvariant() switch
        {
            "CW" => "CW",
            "USB" or "LSB" or "SSB" or "VOICE" or "AM" or "FM" => "VOICE",
            _ => mode.ToUpperInvariant()
        };

        if (Privileges.TryGetValue((license, band, normalizedMode), out var priv))
            return (priv.LowMHz, priv.HighMHz);

        return null;
    }

    /// <summary>
    /// Check if transmission is allowed at the given frequency for the license class and mode.
    /// </summary>
    public static bool IsAllowed(LicenseClass license, double frequencyMHz, string mode)
    {
        var normalizedMode = mode.ToUpperInvariant() switch
        {
            "CW" => "CW",
            "USB" or "LSB" or "SSB" or "VOICE" or "AM" or "FM" => "VOICE",
            _ => mode.ToUpperInvariant()
        };

        foreach (var kvp in Privileges)
        {
            if (kvp.Key.Item1 == license && kvp.Key.Item3 == normalizedMode)
            {
                if (frequencyMHz >= kvp.Value.LowMHz && frequencyMHz <= kvp.Value.HighMHz)
                    return true;
            }
        }

        return false;
    }
}
