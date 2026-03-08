namespace SmartSdrMcp.Tx;

public class TransmitSafety
{
    // Amateur HF band edges in MHz (US Extra class)
    private static readonly (double Low, double High)[] AllowedBands =
    [
        (1.800, 2.000),    // 160m
        (3.500, 4.000),    // 80m
        (5.330, 5.410),    // 60m
        (7.000, 7.300),    // 40m
        (10.100, 10.150),  // 30m
        (14.000, 14.350),  // 20m
        (18.068, 18.168),  // 17m
        (21.000, 21.450),  // 15m
        (24.890, 24.990),  // 12m
        (28.000, 29.700),  // 10m
        (50.000, 54.000),  // 6m
    ];

    public TimeSpan MaxTransmitDuration { get; set; } = TimeSpan.FromSeconds(60);

    public (bool Allowed, string? Reason) CheckTransmitAllowed(double frequencyMHz)
    {
        bool inBand = false;
        foreach (var (low, high) in AllowedBands)
        {
            if (frequencyMHz >= low && frequencyMHz <= high)
            {
                inBand = true;
                break;
            }
        }

        if (!inBand)
            return (false, $"Frequency {frequencyMHz:F3} MHz is outside allowed amateur bands");

        return (true, null);
    }

    public (bool Allowed, string? Reason) CheckTextLength(string text, int wpm)
    {
        double ditMs = 1200.0 / wpm;
        double estimatedDits = text.Length * 8;
        var estimatedDuration = TimeSpan.FromMilliseconds(estimatedDits * ditMs);

        if (estimatedDuration > MaxTransmitDuration)
            return (false, $"Estimated TX duration {estimatedDuration.TotalSeconds:F0}s exceeds max {MaxTransmitDuration.TotalSeconds:F0}s");

        return (true, null);
    }
}

