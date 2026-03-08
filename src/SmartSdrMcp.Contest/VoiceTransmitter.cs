using System.Diagnostics;
using System.Runtime.InteropServices;
using Flex.Smoothlake.FlexLib;
using SmartSdrMcp.Radio;

namespace SmartSdrMcp.Contest;

public class VoiceTransmitter
{
    private readonly RadioManager _radioManager;
    private const int DaxSampleRate = 24000;
    private const int TtsSampleRate = 48000; // Generate at native SAPI rate, downsample to DAX
    private const int SamplesPerPacket = 128;
    private const string Voice = "Microsoft Zira Desktop";
    private const float AudioScale = 0.25f; // Prevent SSB clipping

    public VoiceTransmitter(RadioManager radioManager)
    {
        _radioManager = radioManager;
    }

    /// <summary>Send a 1kHz sine wave for 2 seconds to test DAX TX audio path.</summary>
    public async Task<(bool Success, string Message)> SendToneAsync()
    {
        var radio = _radioManager.Radio;
        if (radio == null || !radio.Connected)
            return (false, "Radio not connected");

        // Generate 2s of 1kHz sine at DaxSampleRate
        int numSamples = DaxSampleRate * 2;
        var samples = new float[numSamples];
        for (int i = 0; i < numSamples; i++)
            samples[i] = (float)(Math.Sin(2.0 * Math.PI * 1000.0 * i / DaxSampleRate) * AudioScale);

        return await StreamToRadio(radio, samples, "1kHz tone (2s)");
    }

    public async Task<(bool Success, string Message)> SpeakAsync(string text)
    {
        var radio = _radioManager.Radio;
        if (radio == null || !radio.Connected)
            return (false, "Radio not connected");

        // Generate WAV via Windows SAPI
        var wavPath = Path.Combine(Path.GetTempPath(), $"smartsdr_tts_{Guid.NewGuid():N}.wav");
        try
        {
            var generated = await GenerateWavAsync(text, wavPath);
            if (!generated)
                return (false, "TTS generation failed");

            // Load and convert audio
            var samples = LoadAndConvert(wavPath);
            if (samples == null || samples.Length == 0)
                return (false, "Failed to load TTS audio");

            // Save debug WAV so operator can compare DAX TX vs raw audio
            SaveDebugWav(samples, Path.Combine(Path.GetTempPath(), "smartsdr_tts_debug.wav"));
            Console.Error.WriteLine($"[VOICE TX] Debug WAV saved to {Path.Combine(Path.GetTempPath(), "smartsdr_tts_debug.wav")}");

            return await StreamToRadio(radio, samples, $"Transmitted: {text}");
        }
        finally
        {
            try { File.Delete(wavPath); } catch { }
        }
    }

    private async Task<(bool Success, string Message)> StreamToRadio(Flex.Smoothlake.FlexLib.Radio radio, float[] samples, string successMessage)
    {
        // Get or create DAX TX stream
        DAXTXAudioStream? txStream = null;
        var tcs = new TaskCompletionSource<DAXTXAudioStream>();

        void onAdded(DAXTXAudioStream s) => tcs.TrySetResult(s);

        lock (radio)
        {
            var field = radio.GetType().GetField("_daxTXAudioStreams",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (field?.GetValue(radio) is List<DAXTXAudioStream> streams && streams.Count > 0)
                txStream = streams[0];
        }

        if (txStream == null)
        {
            radio.DAXTXAudioStreamAdded += onAdded;
            radio.RequestDAXTXAudioStream();
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(5000));
            radio.DAXTXAudioStreamAdded -= onAdded;
            if (completed != tcs.Task)
                return (false, "Timeout waiting for DAX TX audio stream");
            txStream = tcs.Task.Result;
        }

        radio.Mox = true;
        var txReady = SpinWait.SpinUntil(() => txStream.Transmit, TimeSpan.FromMilliseconds(2000));
        if (!txReady)
        {
            radio.Mox = false;
            return (false, "Timeout waiting for TX stream to become active");
        }

        await Task.Delay(200); // TX relay settling

        try
        {
            var packets = BuildPackets(samples);
            Console.Error.WriteLine($"[VOICE TX] Sending {packets.Count} packets ({samples.Length} samples, {samples.Length / (double)DaxSampleRate:F2}s)");

            // Pacing: send each packet slightly before its playback time.
            // Too much lead overflows the radio's buffer; too little causes
            // underruns. 30ms (~5 packets) is a small cushion that absorbs
            // OS scheduling jitter without overflowing.
            const double usPerPacket = 1_000_000.0 * SamplesPerPacket / DaxSampleRate;
            const double leadTimeUs = 30_000; // stay up to 30ms ahead
            var sw = Stopwatch.StartNew();

            for (int i = 0; i < packets.Count; i++)
            {
                double dueUs = i * usPerPacket;
                while (sw.Elapsed.TotalMicroseconds < dueUs - leadTimeUs)
                    Thread.SpinWait(10);
                txStream.AddTXData(packets[i]);
            }

            var silence = new float[SamplesPerPacket * 2];
            for (int i = 0; i < 10; i++)
            {
                double dueUs = (packets.Count + i) * usPerPacket;
                while (sw.Elapsed.TotalMicroseconds < dueUs - leadTimeUs)
                    Thread.SpinWait(10);
                txStream.AddTXData(silence);
            }

            double totalUs = (packets.Count + 10) * usPerPacket;
            while (sw.Elapsed.TotalMicroseconds < totalUs)
                Thread.SpinWait(100);
        }
        finally
        {
            radio.Mox = false;
        }

        return (true, successMessage);
    }

    private static List<float[]> BuildPackets(float[] samples)
    {
        var packets = new List<float[]>();
        int offset = 0;

        while (offset < samples.Length)
        {
            var packet = new float[SamplesPerPacket * 2];
            int count = Math.Min(SamplesPerPacket, samples.Length - offset);

            for (int i = 0; i < count; i++)
            {
                float s = samples[offset + i] * AudioScale;
                packet[i * 2] = s;       // Left
                packet[i * 2 + 1] = s;   // Right
            }

            packets.Add(packet);
            offset += count;
        }

        return packets;
    }

    private static async Task<bool> GenerateWavAsync(string text, string wavPath)
    {
        // Write a temp PowerShell script to avoid escaping issues
        var psPath = Path.Combine(Path.GetTempPath(), $"smartsdr_tts_{Guid.NewGuid():N}.ps1");
        try
        {
            var escapedText = text.Replace("'", "''");
            var escapedPath = wavPath.Replace("\\", "\\\\");
            var script = $@"
Add-Type -AssemblyName System.Speech
$s = New-Object System.Speech.Synthesis.SpeechSynthesizer
$s.SelectVoice('{Voice}')
$s.Rate = 5
$fmt = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo({TtsSampleRate}, [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen, [System.Speech.AudioFormat.AudioChannel]::Mono)
$s.SetOutputToWaveFile('{escapedPath}', $fmt)
$s.Speak('{escapedText}')
$s.Dispose()
";
            await File.WriteAllTextAsync(psPath, script);

            var psi = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-ExecutionPolicy Bypass -File \"{psPath}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return false;

            await proc.WaitForExitAsync();
            return proc.ExitCode == 0 && File.Exists(wavPath);
        }
        finally
        {
            try { File.Delete(psPath); } catch { }
        }
    }

    private static float[]? LoadAndConvert(string wavPath)
    {
        try
        {
            using var fs = File.OpenRead(wavPath);
            using var br = new BinaryReader(fs);

            // Parse WAV header
            var riff = new string(br.ReadChars(4));
            if (riff != "RIFF") return null;
            br.ReadInt32(); // file size
            var wave = new string(br.ReadChars(4));
            if (wave != "WAVE") return null;

            int sampleRate = 0;
            int bitsPerSample = 0;
            int channels = 0;
            float[]? rawSamples = null;

            while (fs.Position < fs.Length)
            {
                var chunkId = new string(br.ReadChars(4));
                int chunkSize = br.ReadInt32();

                if (chunkId == "fmt ")
                {
                    br.ReadInt16(); // audio format
                    channels = br.ReadInt16();
                    sampleRate = br.ReadInt32();
                    br.ReadInt32(); // byte rate
                    br.ReadInt16(); // block align
                    bitsPerSample = br.ReadInt16();
                    if (chunkSize > 16)
                        br.ReadBytes(chunkSize - 16);
                }
                else if (chunkId == "data")
                {
                    int numSamples = chunkSize / (bitsPerSample / 8) / channels;
                    rawSamples = new float[numSamples];

                    for (int i = 0; i < numSamples; i++)
                    {
                        if (bitsPerSample == 16)
                        {
                            short s = br.ReadInt16();
                            rawSamples[i] = s / 32768f;
                            // Skip extra channels
                            for (int c = 1; c < channels; c++)
                                br.ReadInt16();
                        }
                        else
                        {
                            break;
                        }
                    }
                }
                else
                {
                    br.ReadBytes(chunkSize);
                }
            }

            if (rawSamples == null || sampleRate == 0) return null;

            // Resample to 24kHz
            if (sampleRate != DaxSampleRate)
            {
                double ratio = (double)DaxSampleRate / sampleRate;
                int newLen = (int)(rawSamples.Length * ratio);
                var resampled = new float[newLen];
                for (int i = 0; i < newLen; i++)
                {
                    double srcIdx = i / ratio;
                    int idx0 = (int)srcIdx;
                    int idx1 = Math.Min(idx0 + 1, rawSamples.Length - 1);
                    double frac = srcIdx - idx0;
                    resampled[i] = (float)(rawSamples[idx0] * (1 - frac) + rawSamples[idx1] * frac);
                }
                return resampled;
            }

            return rawSamples;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[VOICE TX] WAV load error: {ex.Message}");
            return null;
        }
    }

    private static void SaveDebugWav(float[] samples, string path)
    {
        try
        {
            using var fs = File.Create(path);
            using var bw = new BinaryWriter(fs);

            int dataSize = samples.Length * 2; // 16-bit samples
            bw.Write("RIFF"u8);
            bw.Write(36 + dataSize);
            bw.Write("WAVE"u8);
            bw.Write("fmt "u8);
            bw.Write(16);           // chunk size
            bw.Write((short)1);     // PCM
            bw.Write((short)1);     // mono
            bw.Write(DaxSampleRate);
            bw.Write(DaxSampleRate * 2); // byte rate
            bw.Write((short)2);     // block align
            bw.Write((short)16);    // bits per sample
            bw.Write("data"u8);
            bw.Write(dataSize);

            foreach (var s in samples)
            {
                short pcm = (short)(Math.Clamp(s * AudioScale, -1f, 1f) * 32767);
                bw.Write(pcm);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[VOICE TX] Debug WAV save error: {ex.Message}");
        }
    }
}
