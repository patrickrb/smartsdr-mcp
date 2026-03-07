namespace SmartSdrMcp.Audio;

public class AudioBuffer
{
    private readonly float[] _buffer;
    private int _writePos;
    private int _count;
    private readonly object _lock = new();

    public int Capacity { get; }
    public int Count { get { lock (_lock) return _count; } }

    public AudioBuffer(int capacity = 240_000) // 10 seconds @ 24kHz
    {
        Capacity = capacity;
        _buffer = new float[capacity];
    }

    public void Write(float[] samples)
    {
        lock (_lock)
        {
            foreach (var sample in samples)
            {
                _buffer[_writePos] = sample;
                _writePos = (_writePos + 1) % Capacity;
                if (_count < Capacity) _count++;
            }
        }
    }

    public float[] Read(int count)
    {
        lock (_lock)
        {
            var toRead = Math.Min(count, _count);
            var result = new float[toRead];
            var readPos = (_writePos - _count + Capacity) % Capacity;
            for (int i = 0; i < toRead; i++)
            {
                result[i] = _buffer[(readPos + i) % Capacity];
            }
            return result;
        }
    }

    public float[] ReadLatest(int count)
    {
        lock (_lock)
        {
            var toRead = Math.Min(count, _count);
            var result = new float[toRead];
            var startPos = (_writePos - toRead + Capacity) % Capacity;
            for (int i = 0; i < toRead; i++)
            {
                result[i] = _buffer[(startPos + i) % Capacity];
            }
            return result;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _writePos = 0;
            _count = 0;
        }
    }
}
