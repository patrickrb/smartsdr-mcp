namespace SmartSdrMcp.Cw.Dsp;

/// <summary>
/// Adaptive WPM estimation using clustering of key-down durations.
/// Morse code has two distinct element lengths: dits (1 unit) and dahs (3 units).
/// By collecting durations and finding the two clusters, we can determine speed.
/// WPM = 1200 / dit_duration_ms (PARIS standard).
/// </summary>
public class WpmEstimator
{
    private readonly Queue<double> _allDurations = new();
    private readonly Queue<double> _gapDurations = new();
    private const int MaxSamples = 100;
    private const int MinSamplesForEstimate = 8;
    private bool _calibrated;
    private bool _fixedMode;

    public double EstimatedWpm { get; private set; } = 20;
    public double EstimatedDitMs { get; private set; } = 60; // 20 WPM default

    /// <summary>
    /// Lock the WPM to a fixed value. The estimator will not update.
    /// </summary>
    public void SetFixedWpm(double wpm)
    {
        EstimatedWpm = Math.Clamp(wpm, 12, 60);
        EstimatedDitMs = 1200.0 / EstimatedWpm;
        _fixedMode = true;
        _calibrated = true;
    }

    /// <summary>
    /// Feed a key-down duration (dit or dah). The estimator collects all
    /// durations and uses clustering to find the dit/dah boundary.
    /// </summary>
    public void AddKeyDownDuration(double durationMs)
    {
        if (_fixedMode) return;

        // Reject obviously invalid durations (noise or stuck key)
        if (durationMs < 30 || durationMs > 600) return;

        _allDurations.Enqueue(durationMs);
        while (_allDurations.Count > MaxSamples)
            _allDurations.Dequeue();

        if (_allDurations.Count >= MinSamplesForEstimate)
            UpdateEstimate();
    }

    /// <summary>
    /// Feed an intra-element gap duration (~1T) for cross-checking dit estimate.
    /// </summary>
    public void AddGapDuration(double gapMs)
    {
        if (_fixedMode) return;
        if (gapMs < 10 || gapMs > 300) return;

        _gapDurations.Enqueue(gapMs);
        while (_gapDurations.Count > MaxSamples)
            _gapDurations.Dequeue();
    }

    /// <summary>
    /// Returns the median intra-element gap duration, or -1 if insufficient data.
    /// Used by MorseDecoder for adaptive character gap threshold.
    /// </summary>
    public double MedianGapMs
    {
        get
        {
            if (_gapDurations.Count < 5) return -1;
            var sorted = _gapDurations.OrderBy(g => g).ToList();
            return sorted[sorted.Count / 2];
        }
    }

    private void UpdateEstimate()
    {
        var durations = _allDurations.ToList();

        // Outlier rejection using MAD (median absolute deviation)
        durations.Sort();
        double median = durations[durations.Count / 2];
        var deviations = durations.Select(d => Math.Abs(d - median)).OrderBy(d => d).ToList();
        double mad = deviations[deviations.Count / 2];
        if (mad < 1.0) mad = 1.0; // avoid division issues

        durations = durations.Where(d => Math.Abs(d - median) <= 3.0 * mad).ToList();
        if (durations.Count < MinSamplesForEstimate) return;

        // Find the best split point that separates dits from dahs.
        double bestScore = double.MaxValue;
        double bestDitAvg = EstimatedDitMs;

        for (int split = 2; split < durations.Count - 1; split++)
        {
            var shortGroup = durations.Take(split).ToList();
            var longGroup = durations.Skip(split).ToList();

            double shortAvg = shortGroup.Average();
            double longAvg = longGroup.Average();

            // The ratio of long to short should be close to 3.0
            double ratio = longAvg / shortAvg;
            double ratioError = Math.Abs(ratio - 3.0);

            // Compute variance within each group (normalized)
            double shortVar = shortGroup.Sum(d => (d - shortAvg) * (d - shortAvg)) / shortGroup.Count;
            double longVar = longGroup.Sum(d => (d - longAvg) * (d - longAvg)) / longGroup.Count;
            double totalVar = (shortVar + longVar) / (shortAvg * shortAvg);

            double score = ratioError * 2.0 + totalVar;

            if (score < bestScore)
            {
                bestScore = score;
                bestDitAvg = shortAvg;
            }
        }

        if (bestScore < 4.0)
        {
            double newDit = bestDitAvg;

            // Cross-check against gap durations if available
            if (_gapDurations.Count >= 5)
            {
                var gaps = _gapDurations.OrderBy(g => g).ToList();
                double gapMedian = gaps[gaps.Count / 2];
                // Average key-down estimate with gap estimate (gaps ~= 1T)
                newDit = newDit * 0.7 + gapMedian * 0.3;
            }

            // Rate-limit WPM changes: clamp to max 20% change per update
            if (_calibrated)
            {
                double maxChange = EstimatedDitMs * 0.2;
                newDit = Math.Clamp(newDit, EstimatedDitMs - maxChange, EstimatedDitMs + maxChange);
                // Smooth the transition
                newDit = EstimatedDitMs * 0.7 + newDit * 0.3;
            }

            EstimatedDitMs = newDit;
            EstimatedWpm = 1200.0 / EstimatedDitMs;
            EstimatedWpm = Math.Clamp(EstimatedWpm, 12, 60);
            EstimatedDitMs = 1200.0 / EstimatedWpm;
            _calibrated = true;
        }
    }

    public void Reset()
    {
        _allDurations.Clear();
        _gapDurations.Clear();
        _calibrated = false;
        EstimatedWpm = 20;
        EstimatedDitMs = 60;
    }
}
