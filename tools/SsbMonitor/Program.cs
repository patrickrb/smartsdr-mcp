using SsbMonitor;
using SmartSdrMcp.Audio;
using SmartSdrMcp.Radio;
using SmartSdrMcp.Ssb;

// Suppress stderr debug output from SsbPipeline, RadioManager, etc.
Console.SetError(TextWriter.Null);

int daxChannel = 1;
string? radioSerial = null;

// Parse args
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--dax" && i + 1 < args.Length)
        daxChannel = int.Parse(args[++i]);
    else if (args[i] == "--serial" && i + 1 < args.Length)
        radioSerial = args[++i];
}

Console.WriteLine("╔══════════════════════════════════════════╗");
Console.WriteLine("║     SmartSDR SSB Live Monitor            ║");
Console.WriteLine("╚══════════════════════════════════════════╝");
Console.WriteLine($"  DAX Channel: {daxChannel}");
Console.WriteLine("  Press Ctrl+C to exit\n");

// Connect to radio
var radioManager = new RadioManager();
Console.Write("Discovering radios...");
radioManager.Initialize();

// Wait for radio discovery
for (int i = 0; i < 30; i++)
{
    Thread.Sleep(500);
    var radios = radioManager.DiscoverRadios();
    if (radios.Count > 0)
    {
        Console.WriteLine($" found {radios.Count}.");
        foreach (var r in radios)
            Console.WriteLine($"  - {r.Name} ({r.Model}) S/N: {r.Serial} @ {r.IpAddress}");
        break;
    }
    Console.Write(".");
}

if (!radioManager.IsConnected)
{
    Console.Write("Connecting...");
    bool connected = radioManager.Connect(serial: radioSerial);
    if (!connected)
    {
        Console.WriteLine(" FAILED. No radio found.");
        return 1;
    }
    Console.WriteLine(" connected!");

    // Wait for slices to populate
    Thread.Sleep(2000);
}

var state = radioManager.GetState();
Console.WriteLine($"  Radio: {state.RadioName} on {state.FrequencyMHz:F3} MHz {state.Mode}\n");

// Start audio pipeline
var audioPipeline = new AudioPipeline(radioManager);
var (audioOk, audioErr) = audioPipeline.Start(daxChannel);
if (!audioOk)
{
    Console.WriteLine($"Audio pipeline failed: {audioErr}");
    return 1;
}
Console.WriteLine($"Audio pipeline started on DAX channel {daxChannel}.");

// Start SSB pipeline
var modelsDir = Path.Combine(AppContext.BaseDirectory, "models");
var modelPath = Path.Combine(modelsDir, "ggml-small.en.bin");
if (!File.Exists(modelPath))
    modelPath = Path.Combine(modelsDir, "ggml-base.en.bin");
if (!File.Exists(modelPath))
{
    // Try relative to project root
    modelsDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "SmartSdrMcp", "bin", "Debug", "net9.0-windows", "models");
    modelPath = Path.Combine(modelsDir, "ggml-small.en.bin");
    if (!File.Exists(modelPath))
        modelPath = Path.Combine(modelsDir, "ggml-base.en.bin");
}

if (!File.Exists(modelPath))
{
    Console.WriteLine($"Whisper model not found. Looked in:");
    Console.WriteLine($"  {Path.Combine(AppContext.BaseDirectory, "models")}");
    Console.WriteLine($"  {modelsDir}");
    Console.WriteLine("Place ggml-small.en.bin or ggml-base.en.bin in a 'models' directory.");
    return 1;
}

var ssbPipeline = new SsbPipeline(audioPipeline, modelPath);
var ssbResult = ssbPipeline.Start();
if (ssbResult != "ok")
{
    Console.WriteLine($"SSB pipeline failed: {ssbResult}");
    return 1;
}
Console.WriteLine($"Whisper model loaded: {Path.GetFileName(modelPath)}");
Console.WriteLine("Listening...\n");
Console.WriteLine("─────────────────────────────────────────────");

// Speaker tracking
var speakerTracker = new SpeakerTracker(
    speakerChangeGap: TimeSpan.FromSeconds(2),
    myCallsign: null);
string? lastSpeaker = null;

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    while (!cts.Token.IsCancellationRequested)
    {
        Thread.Sleep(2000);

        // Get segments with voice fingerprints for speaker detection
        var segments = ssbPipeline.GetRecentSegments(500);
        var newLines = speakerTracker.ProcessNewSegments(segments);

        foreach (var line in newLines)
        {
            var timestamp = line.Timestamp.ToLocalTime().ToString("HH:mm:ss");

            // Show speaker change notification
            if (lastSpeaker != null && line.Speaker != lastSpeaker)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"  ── {line.Speaker} ──");
                Console.ResetColor();
            }
            lastSpeaker = line.Speaker;

            // Timestamp in gray, speaker label in color, text in white
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"[{timestamp}] ");
            Console.ForegroundColor = line.Color;
            Console.Write($"[{line.Speaker}] ");
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine(line.Text);
            Console.ResetColor();
        }

        // Show frequency changes
        var currentState = radioManager.GetState();
        if (Math.Abs(currentState.FrequencyMHz - state.FrequencyMHz) > 0.0001 || currentState.Mode != state.Mode)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"  ▸ QSY: {currentState.FrequencyMHz:F3} MHz {currentState.Mode}");
            Console.ResetColor();
            state = currentState;
        }
    }
}
catch (OperationCanceledException) { }

// Print speaker summary
var summary = speakerTracker.GetSpeakerSummary();
var identified = summary.Where(kv => kv.Value != null).ToList();
if (identified.Count > 0)
{
    Console.WriteLine("\nIdentified speakers:");
    foreach (var (label, call) in identified)
        Console.WriteLine($"  {label} → {call}");
}

Console.WriteLine("\n─────────────────────────────────────────────");
Console.WriteLine("Shutting down...");
ssbPipeline.Stop();
audioPipeline.Stop();
radioManager.Dispose();
Console.WriteLine("Done.");
return 0;
