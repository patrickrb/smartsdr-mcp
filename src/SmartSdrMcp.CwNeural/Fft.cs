namespace SmartSdrMcp.CwNeural;

/// <summary>
/// Radix-2 Cooley-Tukey FFT. Operates on interleaved real/imag arrays.
/// </summary>
public static class Fft
{
    /// <summary>
    /// In-place radix-2 FFT. <paramref name="data"/> is interleaved [re0, im0, re1, im1, ...].
    /// Length must be 2*N where N is a power of 2.
    /// </summary>
    public static void Forward(float[] data)
    {
        int n = data.Length / 2;
        if (n == 0 || (n & (n - 1)) != 0)
            throw new ArgumentException("FFT length must be a power of 2.");

        // Bit-reversal permutation
        int j = 0;
        for (int i = 0; i < n - 1; i++)
        {
            if (i < j)
            {
                (data[2 * i], data[2 * j]) = (data[2 * j], data[2 * i]);
                (data[2 * i + 1], data[2 * j + 1]) = (data[2 * j + 1], data[2 * i + 1]);
            }
            int m = n >> 1;
            while (m >= 1 && j >= m)
            {
                j -= m;
                m >>= 1;
            }
            j += m;
        }

        // Butterfly passes
        for (int step = 1; step < n; step <<= 1)
        {
            double angle = -Math.PI / step;
            double wRe = Math.Cos(angle);
            double wIm = Math.Sin(angle);

            for (int group = 0; group < n; group += step << 1)
            {
                double curRe = 1.0, curIm = 0.0;
                for (int pair = 0; pair < step; pair++)
                {
                    int a = 2 * (group + pair);
                    int b = 2 * (group + pair + step);

                    float tRe = (float)(curRe * data[b] - curIm * data[b + 1]);
                    float tIm = (float)(curRe * data[b + 1] + curIm * data[b]);

                    data[b] = data[a] - tRe;
                    data[b + 1] = data[a + 1] - tIm;
                    data[a] += tRe;
                    data[a + 1] += tIm;

                    double newRe = curRe * wRe - curIm * wIm;
                    curIm = curRe * wIm + curIm * wRe;
                    curRe = newRe;
                }
            }
        }
    }
}
