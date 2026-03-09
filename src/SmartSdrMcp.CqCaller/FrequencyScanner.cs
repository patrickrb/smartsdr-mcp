using SmartSdrMcp.Radio;

namespace SmartSdrMcp.CqCaller;

/// <summary>
/// Scans a frequency range to find a clear spot using S-meter readings.
/// </summary>
public class FrequencyScanner
{
    private const double CwStepMHz = 0.0005;      // 500 Hz
    private const double VoiceStepMHz = 0.003;     // 3 kHz
    private const double ClearThresholdDbm = -100;  // ~S3
    private const int SettleDelayMs = 500;
    private const int ConfirmDelayMs = 300;
    private const int ScanTimeoutMs = 30_000;

    public (bool Found, double FrequencyMHz, string Message) FindClearFrequency(
        RadioManager radio, double lowMHz, double highMHz, string mode)
    {
        var step = mode.Equals("CW", StringComparison.OrdinalIgnoreCase)
            ? CwStepMHz : VoiceStepMHz;

        var range = highMHz - lowMHz;
        if (range <= 0)
            return (false, 0, "Invalid frequency range.");

        // Calculate all candidate frequencies
        var steps = (int)(range / step);
        if (steps <= 0)
            return (false, lowMHz, "Range too narrow for scanning.");

        // Start from random offset to avoid always picking same spot
        var rng = new Random();
        var startIdx = rng.Next(steps);

        double quietestFreq = lowMHz;
        double quietestLevel = double.MaxValue;
        var startTime = DateTime.UtcNow;

        for (int i = 0; i < steps; i++)
        {
            if ((DateTime.UtcNow - startTime).TotalMilliseconds > ScanTimeoutMs)
            {
                Console.Error.WriteLine($"[SCANNER] Timeout after {ScanTimeoutMs}ms, using quietest frequency.");
                break;
            }

            var idx = (startIdx + i) % steps;
            var freq = lowMHz + idx * step;

            radio.SetFrequency(freq);
            Thread.Sleep(SettleDelayMs);

            var level = ReadSMeter(radio);

            if (level < quietestLevel)
            {
                quietestLevel = level;
                quietestFreq = freq;
            }

            if (level < ClearThresholdDbm)
            {
                // Double-check: wait and re-read
                Thread.Sleep(ConfirmDelayMs);
                var confirmLevel = ReadSMeter(radio);
                if (confirmLevel < ClearThresholdDbm)
                {
                    Console.Error.WriteLine($"[SCANNER] Clear frequency found: {freq:F3} MHz (S-meter: {confirmLevel:F1} dBm)");
                    return (true, freq, $"Clear frequency at {freq:F3} MHz (S-meter: {confirmLevel:F1} dBm)");
                }
            }
        }

        // No clear spot found — use quietest
        radio.SetFrequency(quietestFreq);
        Console.Error.WriteLine($"[SCANNER] No clear frequency found. Using quietest: {quietestFreq:F3} MHz ({quietestLevel:F1} dBm)");
        return (false, quietestFreq, $"No clear frequency found. Using quietest at {quietestFreq:F3} MHz ({quietestLevel:F1} dBm)");
    }

    private static double ReadSMeter(RadioManager radio)
    {
        var meters = radio.GetMeters();

        // Look for S-meter values (keyed as "S-LVL_SLICEx" or similar)
        foreach (var kvp in meters)
        {
            if (kvp.Key.Contains("S-LVL", StringComparison.OrdinalIgnoreCase) ||
                kvp.Key.Contains("SLVL", StringComparison.OrdinalIgnoreCase) ||
                kvp.Key.Contains("S_LVL", StringComparison.OrdinalIgnoreCase))
            {
                if (kvp.Value is double d) return d;
                if (kvp.Value is float f) return f;
                if (double.TryParse(kvp.Value?.ToString(), out var parsed)) return parsed;
            }
        }

        // Fallback: check for any key containing "S" and a numeric value
        foreach (var kvp in meters)
        {
            if (kvp.Key.StartsWith("S", StringComparison.OrdinalIgnoreCase) && kvp.Key.Length <= 10)
            {
                if (kvp.Value is double d && d < 0) return d;
            }
        }

        // No S-meter available — return very low level so scanning proceeds
        return -140;
    }
}
