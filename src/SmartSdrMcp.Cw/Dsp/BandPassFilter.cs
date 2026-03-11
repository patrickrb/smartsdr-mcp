namespace SmartSdrMcp.Cw.Dsp;

/// <summary>
/// 2nd-order IIR biquad bandpass filter for isolating CW tone.
/// </summary>
public class BandPassFilter
{
    private double _a0, _a1, _a2, _b1, _b2;
    private double _x1, _x2, _y1, _y2;

    public double CenterFrequency { get; private set; }
    public double Bandwidth { get; private set; }
    public double SampleRate { get; private set; }

    public BandPassFilter(double centerFreq = 600, double bandwidth = 80, double sampleRate = 24000)
    {
        Configure(centerFreq, bandwidth, sampleRate);
    }

    public void Configure(double centerFreq, double bandwidth, double sampleRate)
    {
        CenterFrequency = centerFreq;
        Bandwidth = bandwidth;
        SampleRate = sampleRate;

        double omega = 2.0 * Math.PI * centerFreq / sampleRate;
        double sinOmega = Math.Sin(omega);
        double cosOmega = Math.Cos(omega);
        double alpha = sinOmega * Math.Sinh(Math.Log(2) / 2.0 * (bandwidth / centerFreq) * (omega / sinOmega));

        double norm = 1.0 + alpha;
        _a0 = alpha / norm;
        _a1 = 0;
        _a2 = -alpha / norm;
        _b1 = -2.0 * cosOmega / norm;
        _b2 = (1.0 - alpha) / norm;

        Reset();
    }

    public float Process(float sample)
    {
        double x0 = sample;
        double y0 = _a0 * x0 + _a1 * _x1 + _a2 * _x2 - _b1 * _y1 - _b2 * _y2;

        _x2 = _x1;
        _x1 = x0;
        _y2 = _y1;
        _y1 = y0;

        return (float)y0;
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
        _x1 = _x2 = _y1 = _y2 = 0;
    }
}
