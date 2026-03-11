using System.Text.RegularExpressions;
using SmartSdrMcp.Audio;
using Whisper.net;

namespace SmartSdrMcp.Ssb;

public class SsbPipeline : IDisposable
{
    private readonly AudioPipeline _audioPipeline;
    private readonly string _modelPath;
    private const int TargetSampleRate = 16000;
    private const int BufferSeconds = 3;
    private const int BufferSize = TargetSampleRate * BufferSeconds;

    private const string ContestPrompt =
        "Ham radio SSB contest QSO. Stations exchange callsigns using NATO phonetics " +
        "and signal reports. Common phrases: CQ contest, QRZ, you're 59, copy, roger, " +
        "73, thanks for the QSO, again, over. Q-codes: QSL, QTH, QRM, QRN, QSY, QSO, QRP. " +
        "Terms: kilowatt, barefoot, pileup, DX, twenty meters, fifteen meters, ten meters. " +
        "Callsign prefixes: Whiskey, Kilo, November, Victor Echo, Delta Lima, Juliet Alpha. " +
        "Phonetic alphabet: Alpha, Bravo, Charlie, Delta, Echo, Foxtrot, Golf, Hotel, " +
        "India, Juliett, Kilo, Lima, Mike, November, Oscar, Papa, Quebec, Romeo, " +
        "Sierra, Tango, Uniform, Victor, Whiskey, Xray, Yankee, Zulu.";


    /// <summary>
    /// Whisper prompt used for transcription context. Defaults to ContestPrompt.
    /// Can be overridden to optimize for specific use cases (e.g., phonetic callsigns).
    /// </summary>
    public string Prompt { get; set; } = ContestPrompt;

    private WhisperFactory? _factory;
    private bool _running;

    private readonly object _audioLock = new();
    private readonly List<float> _audioBuffer = new();

    private readonly object _textLock = new();
    private readonly List<TranscribedSegment> _segments = new();
    private string _currentPartial = "";
    private string _lastLoggedPartial = "";

    private Thread? _transcribeThread;

    public bool IsRunning => _running;

    public SsbPipeline(AudioPipeline audioPipeline, string modelPath)
    {
        _audioPipeline = audioPipeline;
        _modelPath = modelPath;
    }

    public string Start()
    {
        if (_running) return "SSB listener is already running.";

        if (!File.Exists(_modelPath))
        {
            Console.Error.WriteLine("[SSB] Whisper model not found. Ensure a Whisper model .bin file is available in the 'models' directory.");
            return "Whisper model not found. Download a .bin model from https://huggingface.co/ggerganov/whisper.cpp/tree/main " +
                   "and place it in the 'models' directory next to the application.";
        }

        try
        {
            _factory = WhisperFactory.FromPath(_modelPath);
        }
        catch (Exception ex)
        {
            return $"Failed to load Whisper model: {ex.Message}";
        }

        ClearLiveText();
        _running = true;
        _audioPipeline.AudioDataAvailable += ProcessAudioBlock;

        _transcribeThread = new Thread(TranscribeLoop)
        {
            IsBackground = true,
            Name = "SsbWhisperTranscribe"
        };
        _transcribeThread.Start();

        return "ok";
    }

    public void Stop()
    {
        if (!_running) return;

        _running = false;
        _audioPipeline.AudioDataAvailable -= ProcessAudioBlock;

        _transcribeThread?.Join(5000);
        _transcribeThread = null;

        // Transcribe any remaining audio
        TranscribeBufferedAudio();

        _factory?.Dispose();
        _factory = null;
    }

    public string GetLiveText()
    {
        lock (_textLock)
        {
            var finals = string.Join(" ", _segments
                .Where(s => s.IsFinal && !string.IsNullOrWhiteSpace(s.Text))
                .Select(s => s.Text));

            if (!string.IsNullOrWhiteSpace(_currentPartial))
            {
                if (string.IsNullOrWhiteSpace(finals))
                    return _currentPartial;
                return finals + " " + _currentPartial;
            }

            return string.IsNullOrWhiteSpace(finals) ? "(no speech detected)" : finals;
        }
    }

    public List<TranscribedSegment> GetRecentSegments(int count)
    {
        lock (_textLock)
        {
            var recent = _segments
                .Where(s => s.IsFinal && !string.IsNullOrWhiteSpace(s.Text))
                .TakeLast(count)
                .ToList();
            return recent;
        }
    }

    public void ClearLiveText()
    {
        lock (_textLock)
        {
            _segments.Clear();
            _currentPartial = "";
            _lastLoggedPartial = "";
        }
        lock (_audioLock)
        {
            _audioBuffer.Clear();
        }
    }

    private void ProcessAudioBlock(float[] samples)
    {
        if (!_running) return;

        // Resample 24kHz → 16kHz (Whisper expects 16kHz float samples)
        var resampled = LinearResampler.Resample(samples, _audioPipeline.SampleRate, TargetSampleRate);

        lock (_audioLock)
        {
            _audioBuffer.AddRange(resampled);
        }
    }

    private void TranscribeLoop()
    {
        while (_running)
        {
            Thread.Sleep(100);

            int buffered;
            lock (_audioLock)
            {
                buffered = _audioBuffer.Count;
            }

            if (buffered >= BufferSize)
            {
                TranscribeBufferedAudio();
            }
        }
    }

    private void TranscribeBufferedAudio()
    {
        float[] samples;
        lock (_audioLock)
        {
            if (_audioBuffer.Count == 0) return;
            samples = _audioBuffer.ToArray();
            _audioBuffer.Clear();
        }

        if (_factory == null) return;

        try
        {
            var decoded = new List<string>();

            using var processor = _factory.CreateBuilder()
                .WithLanguage("en")
                .WithNoContext()
                .WithPrompt(Prompt)
                .WithSegmentEventHandler((segment) =>
                {
                    var text = StripParenthesized(segment.Text).Trim();
                    if (!string.IsNullOrWhiteSpace(text) && !IsHallucination(text))
                        decoded.Add(text);
                })
                .Build();

            processor.Process(samples);

            var fullText = string.Join(" ", decoded);
            if (string.IsNullOrWhiteSpace(fullText)) return;

            // Compute voice fingerprint from the audio samples
            var fingerprint = VoiceFingerprint.Compute(samples, TargetSampleRate);

            lock (_textLock)
            {
                _segments.Add(new TranscribedSegment(DateTime.UtcNow, fullText, true, fingerprint));
                _currentPartial = "";
                _lastLoggedPartial = "";

                if (_segments.Count > 500)
                    _segments.RemoveRange(0, 100);
            }

            Console.Error.WriteLine($"[SSB] {fullText}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SSB ERROR] {ex.Message}");
        }
    }

    private static readonly HashSet<string> HallucinationPatterns = new(StringComparer.OrdinalIgnoreCase)
    {
        "(applause)", "(music)", "(laughter)", "(silence)", "(noise)",
        "(buzzing)", "(static)", "(beeping)", "(clicking)", "(humming)",
        "(sighs)", "(coughing)", "(breathing)", "(wind)", "(cheering)",
        "Thank you.", "Thanks for watching.", "Bye.", "Bye bye.",
        "Subscribe to my channel.", "Like and subscribe.",
        "Thank you for watching.", "See you next time.",
    };

    private static bool IsHallucination(string text)
    {
        var cleaned = StripParenthesized(text).Trim();
        if (string.IsNullOrWhiteSpace(cleaned))
            return true;
        if (HallucinationPatterns.Contains(cleaned))
            return true;
        return false;
    }

    private static readonly Regex ParenthesizedPattern =
        new(@"[\(\[][^\)\]]*[\)\]]", RegexOptions.Compiled);

    private static string StripParenthesized(string text)
    {
        // Remove all (content) and [content] — Whisper hallucinations
        return ParenthesizedPattern.Replace(text, "");
    }


    public void Dispose()
    {
        Stop();
    }
}

