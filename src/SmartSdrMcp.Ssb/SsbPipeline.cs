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
            return $"Whisper model not found at '{_modelPath}'. Download it:\n" +
                   "Download a .bin model from https://huggingface.co/ggerganov/whisper.cpp/tree/main\n" +
                   $"Place at: {_modelPath}";

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
                .WithSegmentEventHandler((segment) =>
                {
                    var text = segment.Text.Trim();
                    if (!string.IsNullOrWhiteSpace(text) && !IsHallucination(text))
                        decoded.Add(text);
                })
                .Build();

            processor.Process(samples);

            var fullText = string.Join(" ", decoded);
            if (string.IsNullOrWhiteSpace(fullText)) return;

            lock (_textLock)
            {
                _segments.Add(new TranscribedSegment(DateTime.UtcNow, fullText, true));
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

    public void Dispose()
    {
        Stop();
    }
}

