namespace SmartSdrMcp.CwNeural;

public static class Resampler
{
    public static float[] Resample(float[] input, int srcRate, int dstRate)
    {
        if (srcRate == dstRate) return input;

        double ratio = (double)srcRate / dstRate;
        int outputLen = (int)(input.Length / ratio);
        var output = new float[outputLen];

        for (int i = 0; i < outputLen; i++)
        {
            double srcIndex = i * ratio;
            int idx = (int)srcIndex;
            double frac = srcIndex - idx;

            if (idx + 1 < input.Length)
                output[i] = (float)(input[idx] * (1 - frac) + input[idx + 1] * frac);
            else
                output[i] = input[idx];
        }

        return output;
    }
}
