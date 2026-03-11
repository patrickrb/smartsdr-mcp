using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartSdrMcp.Ai;
using SmartSdrMcp.Audio;
using SmartSdrMcp.BandScout;
using SmartSdrMcp.Cw;
using SmartSdrMcp.CqCaller;
using SmartSdrMcp.CwNeural;
using SmartSdrMcp.DxHunter;
using SmartSdrMcp.Mcp.Resources;
using SmartSdrMcp.Mcp.Tools;
using SmartSdrMcp.Contest;
using SmartSdrMcp.Qso;
using SmartSdrMcp.Radio;
using SmartSdrMcp.Ssb;
using SmartSdrMcp.Tx;
using SmartSdrMcp.Web;

const string MyCallsign = "K1AF";
const string MyName = "PATRICK";

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.ConfigureKestrel(k => k.ListenLocalhost(5100));
builder.WebHost.SuppressStatusMessages(true);

// Core services
builder.Services.AddSingleton<RadioManager>();
builder.Services.AddSingleton<AudioPipeline>();
builder.Services.AddSingleton<CwPipeline>(sp =>
{
    var audio = sp.GetRequiredService<AudioPipeline>();
    return new CwPipeline(audio, sampleRate: audio.SampleRate);
});
builder.Services.AddSingleton<MessageSegmenter>(sp =>
    new MessageSegmenter(sp.GetRequiredService<CwPipeline>(), MyCallsign));
builder.Services.AddSingleton<QsoTracker>(sp =>
{
    var tracker = new QsoTracker(MyCallsign);
    var segmenter = sp.GetRequiredService<MessageSegmenter>();
    segmenter.MessageCompleted += tracker.ProcessMessage;
    return tracker;
});
builder.Services.AddSingleton<ReplyGenerator>(_ =>
    new ReplyGenerator(MyCallsign, MyName));
var anthropicApiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
if (!string.IsNullOrWhiteSpace(anthropicApiKey))
{
    builder.Services.AddSingleton(sp =>
    {
        var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        CwAiRescorer.ConfigureHttpClient(httpClient, anthropicApiKey);
        return new CwAiRescorer(httpClient);
    });
    builder.Services.AddSingleton(sp =>
    {
        var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        CwAiRescorer.ConfigureHttpClient(httpClient, anthropicApiKey);
        return new SsbAiCallsignExtractor(httpClient);
    });
    builder.Services.AddSingleton(sp =>
    {
        var aiRescorer = sp.GetRequiredService<CwAiRescorer>();
        var cwPipeline = sp.GetRequiredService<CwPipeline>();
        var qsoTracker = sp.GetRequiredService<QsoTracker>();
        var radioManager = sp.GetRequiredService<RadioManager>();
        var dxCluster = sp.GetRequiredService<DxClusterService>();

        Func<double, List<(string Callsign, double FreqKhz)>> spotLookup = freqMHz =>
            dxCluster.GetSpotsNearFrequency(freqMHz)
                .Select(s => (s.DxCall, s.FrequencyKhz))
                .ToList();

        return new CwStreamingRescorer(aiRescorer, cwPipeline, qsoTracker, radioManager, spotLookup);
    });
}
builder.Services.AddSingleton<NeuralCwDecoder>(sp =>
{
    var audio = sp.GetRequiredService<AudioPipeline>();
    var modelPath = Path.Combine(AppContext.BaseDirectory, "models", "model_en.onnx");
    if (!File.Exists(modelPath))
        modelPath = Path.Combine(Directory.GetCurrentDirectory(), "models", "model_en.onnx");
    var decoder = new NeuralCwDecoder(audio, modelPath);
    decoder.SetMyCallsign(MyCallsign);

    // Wire neural parse results into the QSO tracker
    var qsoTracker = sp.GetRequiredService<QsoTracker>();
    var radioMgr = sp.GetRequiredService<RadioManager>();
    decoder.MessageParsed += parseResult =>
    {
        if (parseResult.StationCallsign != null)
        {
            var state = radioMgr.GetState();
            var msg = new CwMessage(
                Timestamp: DateTime.UtcNow,
                FrequencyMHz: state.FrequencyMHz,
                DecodedText: parseResult.CleanedText,
                Confidence: 0.8,
                DetectedCallsign: parseResult.StationCallsign,
                IsCq: parseResult.IsCq);
            qsoTracker.ProcessMessage(msg);
        }
    };

    return decoder;
});
builder.Services.AddSingleton<TransmitController>();
builder.Services.AddSingleton<SsbPipeline>(sp =>
{
    var audio = sp.GetRequiredService<AudioPipeline>();
    var modelsDir = Path.Combine(AppContext.BaseDirectory, "models");
    var modelPath = Path.Combine(modelsDir, "ggml-small.en.bin");
    if (!File.Exists(modelPath))
        modelPath = Path.Combine(modelsDir, "ggml-base.en.bin");
    return new SsbPipeline(audio, modelPath);
});
builder.Services.AddSingleton<ContestAgent>(sp =>
    new ContestAgent(
        sp.GetRequiredService<SsbPipeline>(),
        sp.GetRequiredService<RadioManager>(),
        sp.GetRequiredService<AudioPipeline>()));
builder.Services.AddSingleton<BandScoutMonitor>(sp =>
    new BandScoutMonitor(sp.GetRequiredService<RadioManager>()));
builder.Services.AddSingleton<DxHunterAgent>(sp =>
    new DxHunterAgent(
        sp.GetRequiredService<RadioManager>(),
        sp.GetRequiredService<CwPipeline>(),
        sp.GetService<CwAiRescorer>(),
        sp.GetRequiredService<SsbPipeline>(),
        sp.GetRequiredService<TransmitController>()));
builder.Services.AddSingleton<DxClusterService>(sp =>
    new DxClusterService(
        sp.GetRequiredService<RadioManager>(),
        sp.GetRequiredService<DxHunterAgent>()));
builder.Services.AddSingleton<CqCallerAgent>(sp =>
    new CqCallerAgent(
        sp.GetRequiredService<RadioManager>(),
        sp.GetRequiredService<CwPipeline>(),
        sp.GetRequiredService<AudioPipeline>(),
        sp.GetRequiredService<TransmitController>(),
        sp.GetRequiredService<SsbPipeline>(),
        sp.GetService<CwAiRescorer>(),
        sp.GetService<SsbAiCallsignExtractor>()));

// Web dashboard
builder.Services.AddSignalR();
builder.Services.AddHostedService<DashboardBroadcaster>();

// MCP Server
builder.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<RadioTools>()
    .WithTools<CwListenerTools>()
    .WithTools<CwTransmitTools>()
    .WithTools<SsbListenerTools>()
    .WithTools<ContestTools>()
    .WithTools<BandScoutTools>()
    .WithTools<DxHunterTools>()
    .WithTools<CqCallerTools>()
    .WithResources<RadioStateResource>()
    .WithResources<CwLiveResource>()
    .WithResources<CwRecentResource>()
    .WithResources<CwProposalsResource>();

var app = builder.Build();
app.UseStaticFiles();
app.MapHub<DashboardHub>("/dashboard-hub");
app.MapFallbackToFile("index.html");
await app.RunAsync();
