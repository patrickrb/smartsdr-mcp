using Flex.Smoothlake.FlexLib;
using SmartSdrMcp.Radio;

namespace SmartSdrMcp.Audio;

public class AudioPipeline : IDisposable
{
    private readonly RadioManager _radioManager;
    private DAXRXAudioStream? _audioStream;
    private bool _running;

    public AudioBuffer Buffer { get; } = new();
    public bool IsRunning => _running;
    public int SampleRate { get; } = 24000; // 24kHz per channel stereo → 24kHz mono after de-interleave

    public event Action<float[]>? AudioDataAvailable;

    public AudioPipeline(RadioManager radioManager)
    {
        _radioManager = radioManager;
    }

    public bool Start(int daxChannel = 1)
    {
        var radio = _radioManager.Radio;
        if (radio == null || !radio.Connected) return false;

        radio.DAXRXAudioStreamAdded += OnStreamAdded;
        radio.RequestDAXRXAudioStream(daxChannel);
        _running = true;
        return true;
    }

    public void Stop()
    {
        _running = false;
        if (_audioStream != null)
        {
            _audioStream.DataReady -= OnDataReady;
            _audioStream.Close();
            _audioStream = null;
        }
        Buffer.Clear();
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

        // DAX audio is interleaved stereo (L, R, L, R, ...) - extract left channel only
        var mono = new float[rxData.Length / 2];
        for (int i = 0; i < mono.Length; i++)
            mono[i] = rxData[i * 2];

        Buffer.Write(mono);
        AudioDataAvailable?.Invoke(mono);
    }

    public void Dispose()
    {
        Stop();
    }
}
