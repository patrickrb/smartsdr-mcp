using Flex.Smoothlake.FlexLib;
using SmartSdrMcp.Radio;

namespace SmartSdrMcp.Audio;

public record AudioRecordingInfo(
    string Path,
    string Format,
    int SampleRate,
    int SampleCount,
    double DurationSeconds,
    DateTime StartedUtc,
    DateTime EndedUtc);

public class AudioPipeline : IDisposable
{
    private readonly RadioManager _radioManager;
    private DAXRXAudioStream? _audioStream;
    private bool _running;
    private int _consumerCount;
    private int? _currentDaxChannel;

    private readonly object _recordLock = new();
    private bool _isRecording;
    private string _recordFormat = "wav";
    private string? _recordPath;
    private DateTime? _recordStartUtc;
    private readonly List<short> _recordPcm16 = new();
    private Timer? _recordAutoStopTimer;

    public AudioBuffer Buffer { get; } = new();
    public bool IsRunning => _running;
    public int SampleRate { get; } = 24000;
    public int? CurrentDaxChannel => _currentDaxChannel;
    public double LastAudioLevelRms { get; private set; }
    public DateTime? LastAudioSampleUtc { get; private set; }
    public bool IsRecording => _isRecording;
    public AudioRecordingInfo? LastRecordingInfo { get; private set; }

    public event Action<float[]>? AudioDataAvailable;

    public AudioPipeline(RadioManager radioManager)
    {
        _radioManager = radioManager;
    }

    public bool Start(int daxChannel = 1)
    {
        _consumerCount++;

        if (_running) return true;

        var radio = _radioManager.Radio;
        if (radio == null || !radio.Connected)
        {
            _consumerCount--;
            return false;
        }

        _currentDaxChannel = daxChannel;
        radio.DAXRXAudioStreamAdded += OnStreamAdded;
        radio.RequestDAXRXAudioStream(daxChannel);
        _running = true;
        return true;
    }

    public void Stop()
    {
        _consumerCount--;
        if (_consumerCount > 0) return;
        _consumerCount = 0;

        _running = false;
        _currentDaxChannel = null;
        if (_audioStream != null)
        {
            _audioStream.DataReady -= OnDataReady;
            _audioStream.Close();
            _audioStream = null;
        }

        if (_isRecording)
            StopRecording();

        Buffer.Clear();
    }

    public (bool Success, string Message) StartRecording(string format = "wav", int seconds = 30)
    {
        if (!_running)
            return (false, "Audio pipeline is not running.");

        string normalized = (format ?? "wav").Trim().ToLowerInvariant();
        if (normalized != "wav" && normalized != "raw")
            return (false, "Unsupported format. Use 'wav' or 'raw'.");

        lock (_recordLock)
        {
            if (_isRecording)
                return (false, "Audio recording is already active.");

            _isRecording = true;
            _recordFormat = normalized;
            _recordPcm16.Clear();
            _recordStartUtc = DateTime.UtcNow;
            _recordPath = BuildRecordingPath(normalized);
            LastRecordingInfo = null;

            _recordAutoStopTimer?.Dispose();
            _recordAutoStopTimer = null;
            if (seconds > 0)
            {
                _recordAutoStopTimer = new Timer(_ => StopRecording(), null, TimeSpan.FromSeconds(seconds), Timeout.InfiniteTimeSpan);
            }

            return (true, $"Recording started ({normalized}). Output: {_recordPath}");
        }
    }

    public (bool Success, string Message, AudioRecordingInfo? Info) StopRecording()
    {
        RecordingSnapshot? snapshot;
        lock (_recordLock)
        {
            if (!_isRecording)
                return (false, "No active audio recording.", null);

            snapshot = CreateRecordingSnapshot();
            ResetRecordingState();
        }

        var info = PersistRecording(snapshot);
        LastRecordingInfo = info;
        return (true, $"Recording saved to {info.Path}", info);
    }

    private void OnStreamAdded(DAXRXAudioStream stream)
    {
        _audioStream = stream;
        stream.RXGain = 50;
        stream.DataReady += OnDataReady;
    }

    private void OnDataReady(RXAudioStream stream, float[] rxData)
    {
        if (!_running) return;

        var mono = new float[rxData.Length / 2];
        double energy = 0;
        for (int i = 0; i < mono.Length; i++)
        {
            float sample = rxData[i * 2];
            mono[i] = sample;
            energy += sample * sample;
        }

        LastAudioSampleUtc = DateTime.UtcNow;
        LastAudioLevelRms = mono.Length > 0 ? Math.Sqrt(energy / mono.Length) : 0;

        lock (_recordLock)
        {
            if (_isRecording)
            {
                for (int i = 0; i < mono.Length; i++)
                {
                    short pcm = (short)(Math.Clamp(mono[i], -1f, 1f) * short.MaxValue);
                    _recordPcm16.Add(pcm);
                }
            }
        }

        Buffer.Write(mono);
        AudioDataAvailable?.Invoke(mono);
    }

    private RecordingSnapshot CreateRecordingSnapshot()
    {
        var started = _recordStartUtc ?? DateTime.UtcNow;
        var ended = DateTime.UtcNow;
        return new RecordingSnapshot(
            _recordPath ?? BuildRecordingPath(_recordFormat),
            _recordFormat,
            _recordPcm16.ToArray(),
            started,
            ended);
    }

    private void ResetRecordingState()
    {
        _recordAutoStopTimer?.Dispose();
        _recordAutoStopTimer = null;
        _isRecording = false;
        _recordPcm16.Clear();
        _recordStartUtc = null;
        _recordPath = null;
    }

    private AudioRecordingInfo PersistRecording(RecordingSnapshot snapshot)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(snapshot.Path)!);

        if (snapshot.Format == "wav")
            WriteWav(snapshot.Path, snapshot.Samples, SampleRate);
        else
            WriteRaw(snapshot.Path, snapshot.Samples);

        double duration = snapshot.Samples.Length / (double)SampleRate;
        return new AudioRecordingInfo(
            snapshot.Path,
            snapshot.Format,
            SampleRate,
            snapshot.Samples.Length,
            duration,
            snapshot.StartedUtc,
            snapshot.EndedUtc);
    }

    private string BuildRecordingPath(string format)
    {
        string recordingsDir = Path.Combine(AppContext.BaseDirectory, "recordings");
        string ext = format == "raw" ? "pcm" : format;
        string fileName = $"dax-{DateTime.UtcNow:yyyyMMdd-HHmmss}.{ext}";
        return Path.Combine(recordingsDir, fileName);
    }

    private static void WriteRaw(string path, short[] samples)
    {
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        for (int i = 0; i < samples.Length; i++)
            bw.Write(samples[i]);
    }

    private static void WriteWav(string path, short[] samples, int sampleRate)
    {
        int dataLength = samples.Length * sizeof(short);

        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);

        bw.Write("RIFF"u8.ToArray());
        bw.Write(36 + dataLength);
        bw.Write("WAVE"u8.ToArray());

        bw.Write("fmt "u8.ToArray());
        bw.Write(16);
        bw.Write((short)1);
        bw.Write((short)1);
        bw.Write(sampleRate);
        bw.Write(sampleRate * sizeof(short));
        bw.Write((short)sizeof(short));
        bw.Write((short)16);

        bw.Write("data"u8.ToArray());
        bw.Write(dataLength);
        for (int i = 0; i < samples.Length; i++)
            bw.Write(samples[i]);
    }

    public void Dispose()
    {
        Stop();
    }

    private sealed record RecordingSnapshot(
        string Path,
        string Format,
        short[] Samples,
        DateTime StartedUtc,
        DateTime EndedUtc);
}

