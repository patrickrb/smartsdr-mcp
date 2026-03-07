namespace SmartSdrMcp.Cw.Dsp;

/// <summary>
/// Simple peak-tracking AGC to normalize signal amplitude.
/// </summary>
public class AgcProcessor
{
    private double _gain = 1.0;
    private double _peakLevel;

    public double AttackRate { get; set; } = 0.01;  // Fast attack
    public double DecayRate { get; set; } = 0.0001;  // Slow decay
    public double TargetLevel { get; set; } = 0.5;
    public double MaxGain { get; set; } = 100.0;
    public double MinGain { get; set; } = 0.1;

    public float Process(float sample)
    {
        double abs = Math.Abs(sample);

        // Track peak level
        if (abs > _peakLevel)
            _peakLevel += (_peakLevel > 0 ? AttackRate : 1.0) * (abs - _peakLevel);
        else
            _peakLevel *= (1.0 - DecayRate);

        // Compute gain
        if (_peakLevel > 0.0001)
            _gain = TargetLevel / _peakLevel;

        _gain = Math.Clamp(_gain, MinGain, MaxGain);

        return (float)(sample * _gain);
    }

    public void Process(float[] samples, float[] output)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            output[i] = Process(samples[i]);
        }
    }

    public void Reset()
    {
        _gain = 1.0;
        _peakLevel = 0;
    }
}
