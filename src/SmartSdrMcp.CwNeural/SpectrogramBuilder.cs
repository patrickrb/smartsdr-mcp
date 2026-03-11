namespace SmartSdrMcp.CwNeural;

/// <summary>
/// Builds STFT spectrograms matching the web-deep-cw-decoder training pipeline:
/// 256-point FFT, hop 64, Hanning window, raw magnitude, middle 50% frequency bins.
/// </summary>
public class SpectrogramBuilder
{
    private const int FftSize = 256;
    private const int HopSize = 64;
    private const int FreqBins = FftSize / 2 + 1; // 129
    private const int CropStart = FreqBins / 4;    // 32
    private const int CropEnd = CropStart + FreqBins / 2 + 1; // 97 → 65 bins
    private const int CroppedBins = CropEnd - CropStart; // 65
    private const int SampleRate = 3200;
    private const int BufferSeconds = 12;
    private const int MaxSamples = SampleRate * BufferSeconds; // 38400

    private static readonly float[] HanningWindow;

    static SpectrogramBuilder()
    {
        HanningWindow = new float[FftSize];
        for (int i = 0; i < FftSize; i++)
            HanningWindow[i] = 0.5f * (1f - MathF.Cos(2f * MathF.PI * i / (FftSize - 1)));
    }

    private readonly object _lock = new();
    private readonly List<float> _buffer = new();

    public void AddSamples(float[] samples)
    {
        lock (_lock)
        {
            _buffer.AddRange(samples);
            if (_buffer.Count > MaxSamples)
                _buffer.RemoveRange(0, _buffer.Count - MaxSamples);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _buffer.Clear();
        }
    }

    /// <summary>
    /// Compute STFT spectrogram. Returns [timeSteps, croppedBins] magnitude array
    /// and the number of time steps.
    /// </summary>
    public (float[] data, int timeSteps, int freqBins) Build()
    {
        float[] samples;
        lock (_lock)
        {
            samples = _buffer.ToArray();
        }

        if (samples.Length < FftSize)
            return (Array.Empty<float>(), 0, CroppedBins);

        int timeSteps = (samples.Length - FftSize) / HopSize + 1;
        var result = new float[timeSteps * CroppedBins];
        var fftBuf = new float[FftSize * 2]; // interleaved re/im

        for (int t = 0; t < timeSteps; t++)
        {
            int offset = t * HopSize;

            // Fill FFT buffer: windowed real samples, zero imaginary
            for (int i = 0; i < FftSize; i++)
            {
                fftBuf[2 * i] = samples[offset + i] * HanningWindow[i];
                fftBuf[2 * i + 1] = 0f;
            }

            Fft.Forward(fftBuf);

            // Magnitude of cropped bins
            for (int b = 0; b < CroppedBins; b++)
            {
                int bin = CropStart + b;
                float re = fftBuf[2 * bin];
                float im = fftBuf[2 * bin + 1];
                result[t * CroppedBins + b] = MathF.Sqrt(re * re + im * im);
            }
        }

        return (result, timeSteps, CroppedBins);
    }
}
