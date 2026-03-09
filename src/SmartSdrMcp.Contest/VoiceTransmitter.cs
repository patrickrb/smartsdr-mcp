using System.Diagnostics;
using NAudio.Wave;
using SmartSdrMcp.Radio;

namespace SmartSdrMcp.Contest;

public class VoiceTransmitter
{
    private readonly RadioManager _radioManager;
    private const int TtsSampleRate = 48000;
    private const string Voice = "Microsoft Zira Desktop";

    public VoiceTransmitter(RadioManager radioManager)
    {
        _radioManager = radioManager;
    }

    /// <summary>Send a 1kHz sine wave for 2 seconds to test DAX TX audio path.</summary>
    public async Task<(bool Success, string Message)> SendToneAsync()
    {
        // Generate 2s of 1kHz sine at 48kHz, save as WAV, play through DAX TX
        var wavPath = Path.Combine(Path.GetTempPath(), $"smartsdr_tone_{Guid.NewGuid():N}.wav");
        try
        {
            const int sampleRate = 48000;
            const int durationSec = 2;
            int numSamples = sampleRate * durationSec;

            using (var writer = new WaveFileWriter(wavPath, new WaveFormat(sampleRate, 16, 1)))
            {
                for (int i = 0; i < numSamples; i++)
                {
                    float sample = (float)Math.Sin(2.0 * Math.PI * 1000.0 * i / sampleRate) * 0.5f;
                    writer.WriteSample(sample);
                }
            }

            return await PlayThroughDaxTx(wavPath, "1kHz tone (2s)");
        }
        finally
        {
            try { File.Delete(wavPath); } catch { }
        }
    }

    public async Task<(bool Success, string Message)> SpeakAsync(string text)
    {
        var radio = _radioManager.Radio;
        if (radio == null || !radio.Connected)
            return (false, "Radio not connected");

        // Verify frequency is within amateur bands before transmitting
        var state = _radioManager.GetState();
        var safety = new SmartSdrMcp.Tx.TransmitSafety();
        var freqCheck = safety.CheckTransmitAllowed(state.FrequencyMHz);
        if (!freqCheck.Allowed)
            return (false, $"TX blocked: {freqCheck.Reason}");

        // Generate WAV via Windows SAPI
        var wavPath = Path.Combine(Path.GetTempPath(), $"smartsdr_tts_{Guid.NewGuid():N}.wav");
        try
        {
            var generated = await GenerateWavAsync(text, wavPath);
            if (!generated)
                return (false, "TTS generation failed");

            Console.Error.WriteLine($"[VOICE TX] TTS WAV generated: {wavPath}");

            return await PlayThroughDaxTx(wavPath, $"Transmitted: {text}");
        }
        finally
        {
            try { File.Delete(wavPath); } catch { }
        }
    }

    private async Task<(bool Success, string Message)> PlayThroughDaxTx(string wavPath, string successMessage)
    {
        // Find the DAX TX virtual audio device
        int daxDeviceId = FindDaxTxDevice();
        if (daxDeviceId < 0)
            return (false, "DAX TX audio device not found. Ensure SmartSDR DAX is running.");

        var deviceCaps = WaveOut.GetCapabilities(daxDeviceId);
        Console.Error.WriteLine($"[VOICE TX] Using DAX TX device: {deviceCaps.ProductName} (id={daxDeviceId})");

        var radio = _radioManager.Radio;
        if (radio == null || !radio.Connected)
            return (false, "Radio not connected");

        // Key MOX
        radio.Mox = true;
        await Task.Delay(300); // TX relay settling

        try
        {
            using var reader = new AudioFileReader(wavPath);
            using var waveOut = new WaveOutEvent
            {
                DeviceNumber = daxDeviceId,
                DesiredLatency = 100
            };

            var tcs = new TaskCompletionSource<bool>();
            waveOut.PlaybackStopped += (_, _) => tcs.TrySetResult(true);

            waveOut.Init(reader);
            waveOut.Play();

            Console.Error.WriteLine($"[VOICE TX] Playing {reader.TotalTime.TotalSeconds:F1}s audio through DAX TX");

            // Wait for playback to complete or timeout
            var timeout = Task.Delay(TimeSpan.FromSeconds(reader.TotalTime.TotalSeconds + 5));
            await Task.WhenAny(tcs.Task, timeout);

            waveOut.Stop();
        }
        finally
        {
            radio.Mox = false;
        }

        return (true, successMessage);
    }

    private static int FindDaxTxDevice()
    {
        for (int i = 0; i < WaveOut.DeviceCount; i++)
        {
            var caps = WaveOut.GetCapabilities(i);
            Console.Error.WriteLine($"[VOICE TX] Audio device {i}: {caps.ProductName}");

            if (caps.ProductName.Contains("DAX TX", StringComparison.OrdinalIgnoreCase) ||
                caps.ProductName.Contains("FlexRadio", StringComparison.OrdinalIgnoreCase) &&
                caps.ProductName.Contains("TX", StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    // Allowed characters for TTS text: letters, digits, spaces, basic punctuation.
    // Rejects anything that could be interpreted as code by PowerShell.
    private static readonly System.Text.RegularExpressions.Regex SafeTtsText =
        new(@"^[A-Za-z0-9 .,!?\-']+$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static async Task<bool> GenerateWavAsync(string text, string wavPath)
    {
        // Validate input: only allow safe characters to prevent injection
        if (string.IsNullOrWhiteSpace(text) || text.Length > 500)
            return false;

        if (!SafeTtsText.IsMatch(text))
        {
            Console.Error.WriteLine("[VOICE TX] Text contains disallowed characters, rejecting.");
            return false;
        }

        // Write text and path to temp files so they are never interpolated into script code
        var textFile = Path.Combine(Path.GetTempPath(), $"smartsdr_tts_text_{Guid.NewGuid():N}.txt");
        var pathFile = Path.Combine(Path.GetTempPath(), $"smartsdr_tts_path_{Guid.NewGuid():N}.txt");
        var psPath = Path.Combine(Path.GetTempPath(), $"smartsdr_tts_{Guid.NewGuid():N}.ps1");
        try
        {
            await File.WriteAllTextAsync(textFile, text);
            await File.WriteAllTextAsync(pathFile, wavPath);

            // Script reads text and output path from temp files — no user input is interpolated into code.
            var script = $@"
Add-Type -AssemblyName System.Speech
$wavPath = (Get-Content -LiteralPath '{pathFile.Replace("'", "''")}' -Raw).Trim()
$text = (Get-Content -LiteralPath '{textFile.Replace("'", "''")}' -Raw).Trim()
$s = New-Object System.Speech.Synthesis.SpeechSynthesizer
$s.SelectVoice('{Voice}')
$s.Rate = 1
$fmt = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo({TtsSampleRate}, [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen, [System.Speech.AudioFormat.AudioChannel]::Mono)
$s.SetOutputToWaveFile($wavPath, $fmt)
$s.Speak($text)
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
            try { File.Delete(textFile); } catch { }
            try { File.Delete(pathFile); } catch { }
        }
    }
}
