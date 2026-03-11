namespace SmartSdrMcp.Cw.Dsp;

/// <summary>
/// Goertzel algorithm for efficient single-frequency tone detection.
/// Two-tier detection inspired by CW Skimmer:
///   Tier 1 (Squelch): Is there a signal? Uses percentile noise floor.
///   Tier 2 (Keying): Is key down? Uses peak-relative thresholds with hysteresis.
/// Both tiers use raw Magnitude — no smoothing delay in the decision path.
/// </summary>
public class ToneDetector
{
    private double _coefficient;
    private int _blockSize;
    private double _s0, _s1, _s2;
    private int _sampleCount;
    private double _smoothedMagnitude;

    // Percentile-based noise floor tracking
    private double _noiseFloor;
    private double _signalPeak;
    private readonly double[] _magWindow;
    private int _magIndex;
    private bool _magWindowFull;
    private const int MagWindowSize = 200; // 2 seconds at 10ms blocks

    // Peak-relative keying: recent peak with moderate decay
    private double _recentPeak;

    // Squelch debounce
    private int _signalPresentCount;
    private int _signalAbsentCount;
    private const int SquelchOnBlocks = 5;   // 50ms to open squelch
    private const int SquelchOffBlocks = 20;  // 200ms to close squelch

    // Warmup
    private int _warmupBlocks;
    private const int WarmupBlockCount = 80; // 800ms at 10ms blocks

    public double TargetFrequency { get; private set; }
    public double SampleRate { get; private set; }
    public double Magnitude { get; private set; }
    public double SmoothedMagnitude => _smoothedMagnitude;
    public double PeakMagnitude => _signalPeak;
    public double NoiseFloor => _noiseFloor;
    public bool TonePresent { get; private set; }

    /// <summary>
    /// Tier 1 squelch: is there a CW signal present on this frequency?
    /// Debounced over several blocks to avoid flicker.
    /// </summary>
    public bool SignalPresent { get; private set; }

    // Squelch threshold: signal must exceed noise floor by this ratio
    // Lowered from 3.0 — at S3, CW signal is only ~2.3x the 25th-pctile noise floor
    public double SquelchRatio { get; set; } = 1.8;

    // Peak-relative keying thresholds (Tier 2)
    public double KeyOnRatio { get; set; } = 0.30;   // ON when Magnitude > recentPeak * 0.30
    public double KeyOffRatio { get; set; } = 0.20;   // OFF when Magnitude < recentPeak * 0.20

    // Legacy — kept for diagnostics but not used in decisions
    public double OnRatio { get; set; } = 5.0;
    public double OffRatio { get; set; } = 3.5;
    public double SignalSmoothingFactor { get; set; } = 0.65;

    public ToneDetector(double targetFreq = 600, double sampleRate = 24000, int blockSizeMs = 10)
    {
        _magWindow = new double[MagWindowSize];
        Configure(targetFreq, sampleRate, blockSizeMs);
    }

    public void Reconfigure(double targetFreq, double sampleRate, int blockSizeMs = 10)
    {
        Configure(targetFreq, sampleRate, blockSizeMs);
        Reset();
    }

    private void Configure(double targetFreq, double sampleRate, int blockSizeMs)
    {
        TargetFrequency = targetFreq;
        SampleRate = sampleRate;
        _blockSize = (int)(sampleRate * blockSizeMs / 1000.0);

        int k = (int)(0.5 + _blockSize * targetFreq / sampleRate);
        double omega = 2.0 * Math.PI * k / _blockSize;
        _coefficient = 2.0 * Math.Cos(omega);
    }

    public bool ProcessSample(float sample)
    {
        _s0 = sample + _coefficient * _s1 - _s2;
        _s2 = _s1;
        _s1 = _s0;
        _sampleCount++;

        if (_sampleCount >= _blockSize)
        {
            // Compute raw magnitude
            double power = _s1 * _s1 + _s2 * _s2 - _coefficient * _s1 * _s2;
            Magnitude = Math.Sqrt(Math.Abs(power)) / _blockSize;

            // Smoothed magnitude — for diagnostics only, not used in keying decisions
            _smoothedMagnitude = _smoothedMagnitude * (1.0 - SignalSmoothingFactor) + Magnitude * SignalSmoothingFactor;

            // Add raw magnitude to sliding window for noise floor estimation
            _magWindow[_magIndex] = Magnitude;
            _magIndex = (_magIndex + 1) % MagWindowSize;
            if (_magIndex == 0) _magWindowFull = true;

            // Warmup: wait for enough samples
            if (_warmupBlocks < WarmupBlockCount)
            {
                _warmupBlocks++;
                _signalPeak = _smoothedMagnitude;
                TonePresent = false;
                SignalPresent = false;

                if (_warmupBlocks == WarmupBlockCount)
                    UpdateNoiseFloor();

                _s0 = _s1 = _s2 = 0;
                _sampleCount = 0;
                return true;
            }

            // Update noise floor every 10 blocks (~100ms)
            if (_magIndex % 10 == 0)
                UpdateNoiseFloor();

            // Track overall signal peak (slow decay — for diagnostics)
            if (_smoothedMagnitude > _signalPeak)
                _signalPeak = _smoothedMagnitude;
            else
                _signalPeak *= 0.999;

            // --- Tier 1: Squelch (debounced) ---
            bool signalAboveSquelch = Magnitude > _noiseFloor * SquelchRatio;
            if (signalAboveSquelch)
            {
                _signalPresentCount++;
                _signalAbsentCount = 0;
                if (_signalPresentCount >= SquelchOnBlocks)
                    SignalPresent = true;
            }
            else
            {
                _signalAbsentCount++;
                _signalPresentCount = 0;
                if (_signalAbsentCount >= SquelchOffBlocks)
                    SignalPresent = false;
            }

            // --- Tier 2: Peak-relative keying (only when signal present) ---
            if (SignalPresent)
            {
                // Track recent peak: instant rise, moderate decay (~1s half-life at 10ms blocks)
                if (Magnitude > _recentPeak)
                    _recentPeak = Magnitude;
                else
                    _recentPeak *= 0.993;

                // Prevent recentPeak from decaying below noise floor
                if (_recentPeak < _noiseFloor * SquelchRatio)
                    _recentPeak = _noiseFloor * SquelchRatio;

                // Symmetric keying: both ON and OFF use raw Magnitude
                if (TonePresent)
                    TonePresent = Magnitude >= _recentPeak * KeyOffRatio;
                else
                    TonePresent = Magnitude >= _recentPeak * KeyOnRatio;
            }
            else
            {
                TonePresent = false;
                // Let recent peak decay while no signal
                _recentPeak *= 0.993;
            }

            // Reset Goertzel for next block
            _s0 = _s1 = _s2 = 0;
            _sampleCount = 0;
            return true;
        }

        return false;
    }

    private void UpdateNoiseFloor()
    {
        int count = _magWindowFull ? MagWindowSize : _magIndex;
        if (count < 10) return;

        var sorted = new double[count];
        Array.Copy(_magWindow, sorted, count);
        Array.Sort(sorted);

        // Use 10th percentile as noise floor estimate
        // (25th percentile gets inflated during active CW at weak signal levels)
        int pIndex = count / 10;
        _noiseFloor = sorted[pIndex];
    }

    public void Reset()
    {
        _s0 = _s1 = _s2 = 0;
        _sampleCount = 0;
        Magnitude = 0;
        _smoothedMagnitude = 0;
        _noiseFloor = 0;
        _signalPeak = 0;
        _recentPeak = 0;
        _warmupBlocks = 0;
        _magIndex = 0;
        _magWindowFull = false;
        _signalPresentCount = 0;
        _signalAbsentCount = 0;
        Array.Clear(_magWindow);
        TonePresent = false;
        SignalPresent = false;
    }
}
