namespace SmartSdrMcp.Cw.Dsp;

/// <summary>
/// Adaptive WPM estimation using clustering of key-down durations.
/// Also clusters gap durations into intra-element (~1T) and inter-character (~3T)
/// groups, providing a data-driven character gap threshold.
/// </summary>
public class WpmEstimator
{
    private readonly Queue<double> _allDurations = new();
    private readonly Queue<double> _gapDurations = new();
    private readonly Queue<double> _allGapDurations = new();
    private const int MaxSamples = 100;
    private const int MinSamplesForEstimate = 8;
    private const int MinGapSamplesForCluster = 10;
    private bool _calibrated;
    private bool _fixedMode;

    public double EstimatedWpm { get; private set; } = 20;
    public double EstimatedDitMs { get; private set; } = 60; // 20 WPM default
    public double EstimatedDahMs { get; private set; } = 180; // 20 WPM default

    /// <summary>
    /// Data-driven character gap threshold from gap clustering.
    /// Midpoint between the intra-element and inter-character gap clusters.
    /// Falls back to EstimatedDitMs * 2.0 when insufficient gap data.
    /// </summary>
    public double CharGapThresholdMs { get; private set; } = 120; // 20 WPM default

    /// <summary>
    /// Lock the WPM to a fixed value. The estimator will not update.
    /// </summary>
    public void SetFixedWpm(double wpm)
    {
        EstimatedWpm = Math.Clamp(wpm, 12, 60);
        EstimatedDitMs = 1200.0 / EstimatedWpm;
        EstimatedDahMs = EstimatedDitMs * 3.0;
        CharGapThresholdMs = EstimatedDitMs * 2.0;
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
    /// Feed ALL gap durations for clustering analysis.
    /// Accepts both intra-element and inter-character gaps.
    /// </summary>
    public void AddRawGapDuration(double gapMs)
    {
        if (_fixedMode) return;
        if (gapMs < 10 || gapMs > 1500) return;

        _allGapDurations.Enqueue(gapMs);
        while (_allGapDurations.Count > MaxSamples)
            _allGapDurations.Dequeue();

        if (_allGapDurations.Count >= MinGapSamplesForCluster)
            UpdateGapEstimate();
    }

    /// <summary>
    /// Feed an intra-element gap duration (~1T) for cross-checking dit estimate.
    /// Legacy method — still called but AddRawGapDuration is the primary path.
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
        if (mad < 1.0) mad = 1.0;

        durations = durations.Where(d => Math.Abs(d - median) <= 3.0 * mad).ToList();
        if (durations.Count < MinSamplesForEstimate) return;

        // Find the best split point that separates dits from dahs
        double bestScore = double.MaxValue;
        double bestDitAvg = EstimatedDitMs;
        double bestDahAvg = EstimatedDahMs;

        for (int split = 2; split < durations.Count - 1; split++)
        {
            var shortGroup = durations.Take(split).ToList();
            var longGroup = durations.Skip(split).ToList();

            double shortAvg = shortGroup.Average();
            double longAvg = longGroup.Average();

            double ratio = longAvg / shortAvg;
            double ratioError = Math.Abs(ratio - 3.0);

            double shortVar = shortGroup.Sum(d => (d - shortAvg) * (d - shortAvg)) / shortGroup.Count;
            double longVar = longGroup.Sum(d => (d - longAvg) * (d - longAvg)) / longGroup.Count;
            double totalVar = (shortVar + longVar) / (shortAvg * shortAvg);

            double score = ratioError * 2.0 + totalVar;

            if (score < bestScore)
            {
                bestScore = score;
                bestDitAvg = shortAvg;
                bestDahAvg = longAvg;
            }
        }

        if (bestScore < 6.0)
        {
            double newDit = bestDitAvg;

            // Cross-check against gap durations if available
            if (_gapDurations.Count >= 5)
            {
                var gaps = _gapDurations.OrderBy(g => g).ToList();
                double gapMedian = gaps[gaps.Count / 2];
                newDit = newDit * 0.85 + gapMedian * 0.15;
            }

            // Rate-limit WPM changes
            if (_calibrated)
            {
                double maxChange = EstimatedDitMs * 0.30;
                newDit = Math.Clamp(newDit, EstimatedDitMs - maxChange, EstimatedDitMs + maxChange);
                newDit = EstimatedDitMs * 0.6 + newDit * 0.4;
            }

            EstimatedDitMs = newDit;
            EstimatedWpm = 1200.0 / EstimatedDitMs;
            EstimatedWpm = Math.Clamp(EstimatedWpm, 12, 60);
            EstimatedDitMs = 1200.0 / EstimatedWpm;
            EstimatedDahMs = bestDahAvg;
            _calibrated = true;

            // Update fallback CharGapThreshold if no gap clustering yet
            if (_allGapDurations.Count < MinGapSamplesForCluster)
                CharGapThresholdMs = EstimatedDitMs * 2.0;
        }
    }

    /// <summary>
    /// Cluster all gap durations into two groups:
    ///   Short cluster = intra-element gaps (~1T)
    ///   Long cluster = inter-character gaps (~3T)
    /// Uses same split-point scoring as dit/dah clustering.
    /// </summary>
    private void UpdateGapEstimate()
    {
        var gaps = _allGapDurations.ToList();
        gaps.Sort();

        // Outlier rejection using MAD
        double median = gaps[gaps.Count / 2];
        var deviations = gaps.Select(g => Math.Abs(g - median)).OrderBy(d => d).ToList();
        double mad = deviations[deviations.Count / 2];
        if (mad < 1.0) mad = 1.0;

        gaps = gaps.Where(g => Math.Abs(g - median) <= 4.0 * mad).ToList();
        if (gaps.Count < MinGapSamplesForCluster) return;

        double bestScore = double.MaxValue;
        double bestShortAvg = -1;
        double bestLongAvg = -1;

        for (int split = 2; split < gaps.Count - 1; split++)
        {
            var shortGroup = gaps.Take(split).ToList();
            var longGroup = gaps.Skip(split).ToList();

            double shortAvg = shortGroup.Average();
            double longAvg = longGroup.Average();

            double ratio = longAvg / shortAvg;
            double ratioError = Math.Abs(ratio - 3.0);

            double shortVar = shortGroup.Sum(g => (g - shortAvg) * (g - shortAvg)) / shortGroup.Count;
            double longVar = longGroup.Sum(g => (g - longAvg) * (g - longAvg)) / longGroup.Count;
            double totalVar = (shortVar + longVar) / (shortAvg * shortAvg);

            double score = ratioError * 2.0 + totalVar;

            if (score < bestScore)
            {
                bestScore = score;
                bestShortAvg = shortAvg;
                bestLongAvg = longAvg;
            }
        }

        // Only use clustering result if it looks reasonable
        if (bestScore < 6.0 && bestShortAvg > 0 && bestLongAvg > bestShortAvg)
        {
            CharGapThresholdMs = (bestShortAvg + bestLongAvg) / 2.0;
        }
        else
        {
            // Fallback
            CharGapThresholdMs = EstimatedDitMs * 2.0;
        }
    }

    public void Reset()
    {
        _allDurations.Clear();
        _gapDurations.Clear();
        _allGapDurations.Clear();
        _calibrated = false;
        EstimatedWpm = 20;
        EstimatedDitMs = 60;
        EstimatedDahMs = 180;
        CharGapThresholdMs = 120;
    }
}
