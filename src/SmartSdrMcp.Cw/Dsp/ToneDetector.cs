namespace SmartSdrMcp.Cw.Dsp;

/// <summary>
/// Goertzel algorithm for efficient single-frequency tone detection.
/// Uses percentile-based adaptive thresholding: maintains a sliding window
/// of magnitudes and uses the 25th percentile as noise floor estimate.
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

    // Adaptive threshold: tone must be this many times the noise floor
    public double OnRatio { get; set; } = 3.0;
    public double OffRatio { get; set; } = 2.2;

    // Smoothing factor for signal magnitude — must be high enough to track
    // inter-element gaps so tone turns OFF between dits/dahs
    public double SignalSmoothingFactor { get; set; } = 0.5;

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

            // Smooth the signal magnitude
            _smoothedMagnitude = _smoothedMagnitude * (1.0 - SignalSmoothingFactor) + Magnitude * SignalSmoothingFactor;

            // Add to sliding window
            _magWindow[_magIndex] = _smoothedMagnitude;
            _magIndex = (_magIndex + 1) % MagWindowSize;
            if (_magIndex == 0) _magWindowFull = true;

            // Warmup: wait for enough samples
            if (_warmupBlocks < WarmupBlockCount)
            {
                _warmupBlocks++;
                _signalPeak = _smoothedMagnitude;
                TonePresent = false;

                if (_warmupBlocks == WarmupBlockCount)
                    UpdateNoiseFloor();

                _s0 = _s1 = _s2 = 0;
                _sampleCount = 0;
                return true;
            }

            // Update noise floor every 10 blocks (~100ms) to avoid sorting overhead
            if (_magIndex % 10 == 0)
                UpdateNoiseFloor();

            // Track signal peak (slow decay)
            if (_smoothedMagnitude > _signalPeak)
                _signalPeak = _smoothedMagnitude;
            else
                _signalPeak *= 0.999;

            // Adaptive hysteresis decision
            // Use raw magnitude for OFF decisions (fast response to gaps)
            // Use smoothed magnitude for ON decisions (noise rejection)
            double onThreshold = _noiseFloor * OnRatio;
            double offThreshold = _noiseFloor * OffRatio;

            if (TonePresent)
                TonePresent = Magnitude >= offThreshold;
            else
                TonePresent = _smoothedMagnitude >= onThreshold;

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

        // Copy and sort to find 25th percentile
        var sorted = new double[count];
        Array.Copy(_magWindow, sorted, count);
        Array.Sort(sorted);

        // Use 25th percentile as noise floor estimate
        int q1Index = count / 4;
        _noiseFloor = sorted[q1Index];
    }

    public void Reset()
    {
        _s0 = _s1 = _s2 = 0;
        _sampleCount = 0;
        Magnitude = 0;
        _smoothedMagnitude = 0;
        _noiseFloor = 0;
        _signalPeak = 0;
        _warmupBlocks = 0;
        _magIndex = 0;
        _magWindowFull = false;
        Array.Clear(_magWindow);
        TonePresent = false;
    }
}
