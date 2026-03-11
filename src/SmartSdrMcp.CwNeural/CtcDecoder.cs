using System.Text;

namespace SmartSdrMcp.CwNeural;

/// <summary>
/// Greedy CTC decoder with vocabulary matching web-deep-cw-decoder const.ts.
/// Index 0 = CTC blank, index 46 = space.
/// </summary>
public static class CtcDecoder
{
    // Vocabulary: 47 tokens (index 0 = blank for CTC)
    // [UNK], /, 0-9, ?, A-Z, <AR>, <BT>, <HH>, <KN>, <SK>, <BK>, <UR>, (space)
    private static readonly string[] Vocabulary =
    {
        "",    // 0: CTC blank
        "/",   // 1
        "0", "1", "2", "3", "4", "5", "6", "7", "8", "9", // 2-11
        "?",   // 12
        "A", "B", "C", "D", "E", "F", "G", "H", "I", "J", // 13-22
        "K", "L", "M", "N", "O", "P", "Q", "R", "S", "T", // 23-32
        "U", "V", "W", "X", "Y", "Z",                      // 33-38
        "AR",  // 39 - prosign <AR>
        "BT",  // 40 - prosign <BT>
        "HH",  // 41 - prosign <HH> (error)
        "KN",  // 42 - prosign <KN>
        "SK",  // 43 - prosign <SK>
        "BK",  // 44 - prosign <BK>
        "UR",  // 45 - prosign <UR>
        " "    // 46 - space
    };

    public const int VocabSize = 47;

    /// <summary>
    /// Greedy CTC decode: argmax per timestep, collapse duplicates, remove blanks.
    /// </summary>
    public static string Decode(float[] output, int timeSteps, int vocabSize)
    {
        if (vocabSize != VocabSize)
            throw new ArgumentException($"Expected vocab size {VocabSize}, got {vocabSize}");

        var sb = new StringBuilder();
        int prevToken = -1;

        for (int t = 0; t < timeSteps; t++)
        {
            // Argmax for this timestep
            int bestIdx = 0;
            float bestVal = output[t * vocabSize];
            for (int v = 1; v < vocabSize; v++)
            {
                float val = output[t * vocabSize + v];
                if (val > bestVal)
                {
                    bestVal = val;
                    bestIdx = v;
                }
            }

            // Skip blank and consecutive duplicates
            if (bestIdx == 0 || bestIdx == prevToken)
            {
                if (bestIdx != prevToken)
                    prevToken = bestIdx;
                continue;
            }

            prevToken = bestIdx;
            sb.Append(Vocabulary[bestIdx]);
        }

        return ReplaceConsecutiveChars(sb.ToString());
    }

    /// <summary>
    /// Raw CTC decode without consecutive-char replacement.
    /// Preserves the space-separated character output for post-processing.
    /// </summary>
    public static string DecodeRaw(float[] output, int timeSteps, int vocabSize)
    {
        if (vocabSize != VocabSize)
            throw new ArgumentException($"Expected vocab size {VocabSize}, got {vocabSize}");

        var sb = new StringBuilder();
        int prevToken = -1;

        for (int t = 0; t < timeSteps; t++)
        {
            int bestIdx = 0;
            float bestVal = output[t * vocabSize];
            for (int v = 1; v < vocabSize; v++)
            {
                float val = output[t * vocabSize + v];
                if (val > bestVal)
                {
                    bestVal = val;
                    bestIdx = v;
                }
            }

            if (bestIdx == 0 || bestIdx == prevToken)
            {
                if (bestIdx != prevToken)
                    prevToken = bestIdx;
                continue;
            }

            prevToken = bestIdx;
            sb.Append(Vocabulary[bestIdx]);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Collapse repeated non-space characters (e.g., "KK" → "K ").
    /// Matches web-deep-cw-decoder replaceConsecutiveChars behavior.
    /// </summary>
    private static string ReplaceConsecutiveChars(string text)
    {
        if (text.Length < 2) return text;

        var sb = new StringBuilder();
        sb.Append(text[0]);

        for (int i = 1; i < text.Length; i++)
        {
            if (text[i] == text[i - 1] && text[i] != ' ')
            {
                sb.Append(' ');
            }
            else
            {
                sb.Append(text[i]);
            }
        }

        return sb.ToString();
    }
}
