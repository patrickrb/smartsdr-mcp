using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SmartSdrMcp.Audio;

namespace SmartSdrMcp.CwNeural;

/// <summary>
/// Neural CW decoder using ONNX CRNN+CTC model from web-deep-cw-decoder.
/// Runs as a parallel decode path alongside the traditional Goertzel decoder.
///
/// Text persistence: the model decodes a rolling 12s window. As the window
/// slides, text scrolls off the front. We detect when a previously-stable
/// prefix disappears from the new decode and commit it to persistent history.
/// </summary>
public class NeuralCwDecoder : IDisposable
{
    private const int ModelSampleRate = 3200;
    private const int InferenceIntervalMs = 250;
    private const int StabilityThreshold = 3; // consecutive identical decodes to confirm

    private readonly AudioPipeline _audioPipeline;
    private readonly string _modelPath;
    private string _myCallsign = "K1AF";

    private InferenceSession? _session;
    private readonly SpectrogramBuilder _spectrogram = new();
    private Thread? _inferenceThread;
    private volatile bool _running;

    private readonly object _textLock = new();
    private string _currentDecode = "";        // latest raw decode of 12s window
    private string _confirmedHistory = "";      // persistent text that scrolled out
    private string _prevDecode = "";            // previous inference result
    private string _stableDecode = "";          // decode that has been stable for N cycles
    private int _stabilityCounter;
    private NeuralParseResult? _latestParse;

    public bool IsRunning => _running;

    public event Action<NeuralParseResult>? MessageParsed;

    public NeuralCwDecoder(AudioPipeline audioPipeline, string modelPath)
    {
        _audioPipeline = audioPipeline;
        _modelPath = modelPath;
    }

    public void SetMyCallsign(string callsign)
    {
        _myCallsign = callsign;
    }

    public NeuralParseResult? GetLatestParse()
    {
        lock (_textLock) return _latestParse;
    }

    public string Start()
    {
        if (_running) return "Neural CW decoder is already running.";

        if (!File.Exists(_modelPath))
        {
            Console.Error.WriteLine("[NeuralCW] ONNX model not found at: " + _modelPath);
            return $"Neural CW model not found. Download model_en.onnx from " +
                   "https://github.com/e04/web-deep-cw-decoder and place it in the 'models' directory.";
        }

        try
        {
            var opts = new SessionOptions();
            opts.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
            _session = new InferenceSession(_modelPath, opts);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[NeuralCW] Failed to load model: {ex.Message}");
            return $"Failed to load neural CW model: {ex.Message}";
        }

        _spectrogram.Clear();
        lock (_textLock)
        {
            _currentDecode = "";
            _confirmedHistory = "";
            _prevDecode = "";
            _stableDecode = "";
            _stabilityCounter = 0;
        }

        _running = true;
        _audioPipeline.AudioDataAvailable += OnAudioData;

        _inferenceThread = new Thread(InferenceLoop)
        {
            IsBackground = true,
            Name = "NeuralCwInference"
        };
        _inferenceThread.Start();

        Console.Error.WriteLine("[NeuralCW] Neural CW decoder started.");
        return "ok";
    }

    public void Stop()
    {
        if (!_running) return;

        _running = false;
        _audioPipeline.AudioDataAvailable -= OnAudioData;

        _inferenceThread?.Join(3000);
        _inferenceThread = null;

        _session?.Dispose();
        _session = null;

        Console.Error.WriteLine("[NeuralCW] Neural CW decoder stopped.");
    }

    public string GetLiveText()
    {
        lock (_textLock)
        {
            var current = CleanupText(_currentDecode);
            if (string.IsNullOrWhiteSpace(_confirmedHistory))
                return string.IsNullOrWhiteSpace(current) ? "(no CW detected)" : current;
            if (string.IsNullOrWhiteSpace(current))
                return _confirmedHistory;
            return _confirmedHistory + " | " + current;
        }
    }

    public void ClearText()
    {
        lock (_textLock)
        {
            _currentDecode = "";
            _confirmedHistory = "";
            _prevDecode = "";
            _stableDecode = "";
            _stabilityCounter = 0;
        }
        _spectrogram.Clear();
    }

    private void OnAudioData(float[] samples)
    {
        if (!_running) return;

        var resampled = Resampler.Resample(samples, _audioPipeline.SampleRate, ModelSampleRate);
        _spectrogram.AddSamples(resampled);
    }

    private void InferenceLoop()
    {
        while (_running)
        {
            Thread.Sleep(InferenceIntervalMs);
            if (!_running || _session == null) break;

            try
            {
                RunInference();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[NeuralCW] Inference error: {ex.Message}");
            }
        }
    }

    private void RunInference()
    {
        var (data, timeSteps, freqBins) = _spectrogram.Build();
        if (timeSteps == 0) return;

        // Build input tensor [1, timeSteps, freqBins, 1]
        var tensor = new DenseTensor<float>(new[] { 1, timeSteps, freqBins, 1 });
        for (int t = 0; t < timeSteps; t++)
            for (int f = 0; f < freqBins; f++)
                tensor[0, t, f, 0] = data[t * freqBins + f];

        var inputName = _session!.InputMetadata.Keys.First();
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(inputName, tensor)
        };

        using var results = _session.Run(inputs);
        var outputTensor = results.First().AsTensor<float>();

        var dims = outputTensor.Dimensions;
        int outTimeSteps = dims[1];
        int vocabSize = dims[2];

        var outputData = new float[outTimeSteps * vocabSize];
        for (int t = 0; t < outTimeSteps; t++)
            for (int v = 0; v < vocabSize; v++)
                outputData[t * vocabSize + v] = outputTensor[0, t, v];

        var rawDecoded = CtcDecoder.DecodeRaw(outputData, outTimeSteps, vocabSize);
        var coalesced = CtcPostProcessor.CoalesceText(rawDecoded);
        var cleaned = CleanupText(coalesced);

        NeuralParseResult? newParse = null;

        lock (_textLock)
        {
            _currentDecode = coalesced;

            // Track stability
            if (cleaned == CleanupText(_prevDecode))
            {
                _stabilityCounter++;
            }
            else
            {
                _stabilityCounter = 1;
            }

            // Once stable for N cycles, mark as confirmed
            if (_stabilityCounter >= StabilityThreshold && !string.IsNullOrWhiteSpace(cleaned))
            {
                // Check if text has scrolled off the front compared to the last
                // confirmed stable decode. The window slides forward, so the new
                // decode's content should be a suffix of what was previously stable,
                // possibly with new content appended at the end.
                if (!string.IsNullOrEmpty(_stableDecode))
                {
                    var scrolledOff = FindScrolledOffPrefix(_stableDecode, cleaned);
                    if (!string.IsNullOrWhiteSpace(scrolledOff))
                    {
                        if (_confirmedHistory.Length > 0)
                            _confirmedHistory += " " + scrolledOff;
                        else
                            _confirmedHistory = scrolledOff;

                        // Trim history to prevent unbounded growth
                        if (_confirmedHistory.Length > 2000)
                            _confirmedHistory = _confirmedHistory[^1500..];
                    }
                }
                _stableDecode = cleaned;

                // Parse stable decode for callsigns and message type
                var parseResult = CtcPostProcessor.Parse(coalesced, _myCallsign);
                if (parseResult.DetectedCallsigns.Count > 0 ||
                    parseResult.MessageType != NeuralMessageType.Unknown)
                {
                    _latestParse = parseResult;
                    newParse = parseResult;
                }
            }

            _prevDecode = coalesced;
        }

        // Fire event outside the lock
        if (newParse != null)
            MessageParsed?.Invoke(newParse);
    }

    /// <summary>
    /// Find text that was at the front of the previous stable decode but is
    /// no longer present in the new decode (it scrolled out of the 12s window).
    /// Uses longest common substring matching to find where the new decode
    /// overlaps with the old, then returns the prefix before the overlap.
    /// </summary>
    private static string FindScrolledOffPrefix(string oldStable, string newDecode)
    {
        if (string.IsNullOrEmpty(oldStable) || string.IsNullOrEmpty(newDecode))
            return "";

        // Work on word-level tokens for robustness against character-level jitter
        var oldWords = oldStable.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var newWords = newDecode.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (oldWords.Length == 0 || newWords.Length == 0)
            return "";

        // Find where the new decode starts overlapping with the old decode.
        // The new decode should contain a suffix of the old decode (the part
        // still in the 12s window) plus possibly new content at the end.
        // Search for the longest overlap: old[i..] matches new[0..].
        int bestOverlapStart = -1;
        for (int i = 0; i < oldWords.Length; i++)
        {
            int matchLen = 0;
            for (int j = 0; j < newWords.Length && (i + matchLen) < oldWords.Length; j++)
            {
                if (oldWords[i + matchLen] == newWords[j])
                {
                    matchLen++;
                    if (matchLen >= 2) // require at least 2 matching words
                    {
                        bestOverlapStart = i;
                        break;
                    }
                }
                else
                {
                    break;
                }
            }
            if (bestOverlapStart >= 0) break;
        }

        // If we found an overlap starting at position i, words 0..i-1 scrolled off
        if (bestOverlapStart > 0)
            return string.Join(" ", oldWords[..bestOverlapStart]);

        // If no overlap found and the decodes are completely different,
        // the entire old decode scrolled off
        if (bestOverlapStart < 0 && oldStable != newDecode)
            return oldStable;

        return "";
    }

    private static string CleanupText(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "";

        var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", parts);
    }

    public void Dispose()
    {
        Stop();
    }
}
