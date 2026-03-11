namespace SmartSdrMcp.Ssb;

/// <summary>
/// Simple voice fingerprint computed from audio samples.
/// Uses spectral centroid, zero-crossing rate, RMS energy, and pitch estimate
/// to distinguish between different speakers.
/// </summary>
public record VoiceFingerprint(
    float SpectralCentroid,
    float ZeroCrossingRate,
    float RmsEnergy,
    float PitchEstimateHz)
{
    /// <summary>
    /// Compute a voice fingerprint from 16kHz mono audio samples.
    /// </summary>
    public static VoiceFingerprint Compute(float[] samples, int sampleRate = 16000)
    {
        if (samples.Length == 0)
            return new VoiceFingerprint(0, 0, 0, 0);

        // RMS energy
        double energy = 0;
        for (int i = 0; i < samples.Length; i++)
            energy += samples[i] * samples[i];
        float rms = (float)Math.Sqrt(energy / samples.Length);

        // Zero-crossing rate (correlates with pitch/voicing)
        int crossings = 0;
        for (int i = 1; i < samples.Length; i++)
        {
            if ((samples[i] >= 0 && samples[i - 1] < 0) ||
                (samples[i] < 0 && samples[i - 1] >= 0))
                crossings++;
        }
        float zcr = crossings / (float)(samples.Length - 1);

        // Spectral centroid via FFT approximation using magnitude spectrum
        // Use a simple DFT on a windowed chunk for efficiency
        int fftSize = Math.Min(2048, samples.Length);
        int offset = Math.Max(0, (samples.Length - fftSize) / 2); // center chunk
        float centroid = ComputeSpectralCentroid(samples, offset, fftSize, sampleRate);

        // Pitch estimate via autocorrelation
        float pitch = EstimatePitch(samples, sampleRate);

        return new VoiceFingerprint(centroid, zcr, rms, pitch);
    }

    /// <summary>
    /// Compute similarity between two fingerprints (0 = different, 1 = identical).
    /// </summary>
    public float SimilarityTo(VoiceFingerprint other)
    {
        if (RmsEnergy < 0.001f || other.RmsEnergy < 0.001f)
            return 0; // silence — can't compare

        // Normalize each feature difference to 0-1 range, then average
        float centroidDiff = Math.Abs(SpectralCentroid - other.SpectralCentroid) / Math.Max(1f, Math.Max(SpectralCentroid, other.SpectralCentroid));
        float zcrDiff = Math.Abs(ZeroCrossingRate - other.ZeroCrossingRate) / Math.Max(0.001f, Math.Max(ZeroCrossingRate, other.ZeroCrossingRate));

        // Pitch comparison — if both have valid pitch estimates
        float pitchDiff;
        if (PitchEstimateHz > 50 && other.PitchEstimateHz > 50)
            pitchDiff = Math.Abs(PitchEstimateHz - other.PitchEstimateHz) / Math.Max(PitchEstimateHz, other.PitchEstimateHz);
        else
            pitchDiff = 0.5f; // unknown pitch — neutral

        // Weighted combination: pitch is most discriminative, then centroid, then ZCR
        float similarity = 1f - (pitchDiff * 0.45f + centroidDiff * 0.35f + zcrDiff * 0.20f);
        return Math.Clamp(similarity, 0f, 1f);
    }

    private static float ComputeSpectralCentroid(float[] samples, int offset, int size, int sampleRate)
    {
        // Simple magnitude spectrum via DFT (only compute magnitude, not phase)
        // For efficiency, only compute first half (Nyquist)
        int halfSize = size / 2;
        double weightedSum = 0;
        double magnitudeSum = 0;

        for (int k = 1; k < halfSize; k++)
        {
            double re = 0, im = 0;
            double freqBin = 2.0 * Math.PI * k / size;

            for (int n = 0; n < size; n++)
            {
                int idx = offset + n;
                if (idx >= samples.Length) break;
                // Apply Hann window
                double window = 0.5 * (1 - Math.Cos(2.0 * Math.PI * n / (size - 1)));
                double sample = samples[idx] * window;
                re += sample * Math.Cos(freqBin * n);
                im += sample * Math.Sin(freqBin * n);
            }

            double magnitude = Math.Sqrt(re * re + im * im);
            double frequency = (double)k * sampleRate / size;
            weightedSum += frequency * magnitude;
            magnitudeSum += magnitude;
        }

        return magnitudeSum > 0 ? (float)(weightedSum / magnitudeSum) : 0;
    }

    private static float EstimatePitch(float[] samples, int sampleRate)
    {
        // Autocorrelation-based pitch detection
        // Look for fundamental frequency between 80Hz (deep male) and 400Hz (high female)
        int minLag = sampleRate / 400; // 40 samples at 16kHz
        int maxLag = sampleRate / 80;  // 200 samples at 16kHz

        if (samples.Length < maxLag * 2)
            return 0;

        // Use center portion of audio
        int analysisLength = Math.Min(4096, samples.Length);
        int analysisOffset = Math.Max(0, (samples.Length - analysisLength) / 2);

        double bestCorrelation = 0;
        int bestLag = 0;

        // Compute energy for normalization
        double energy = 0;
        for (int i = analysisOffset; i < analysisOffset + analysisLength; i++)
            energy += samples[i] * samples[i];
        if (energy < 0.0001) return 0;

        for (int lag = minLag; lag <= maxLag && lag < analysisLength; lag++)
        {
            double correlation = 0;
            int count = Math.Min(analysisLength - lag, 2048);
            for (int i = 0; i < count; i++)
            {
                correlation += samples[analysisOffset + i] * samples[analysisOffset + i + lag];
            }
            correlation /= count;

            if (correlation > bestCorrelation)
            {
                bestCorrelation = correlation;
                bestLag = lag;
            }
        }

        if (bestLag == 0 || bestCorrelation < 0.01)
            return 0;

        return (float)sampleRate / bestLag;
    }
}
