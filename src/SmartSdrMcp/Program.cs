using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartSdrMcp.Ai;
using SmartSdrMcp.Audio;
using SmartSdrMcp.Cw;
using SmartSdrMcp.Mcp.Resources;
using SmartSdrMcp.Mcp.Tools;
using SmartSdrMcp.Contest;
using SmartSdrMcp.Qso;
using SmartSdrMcp.Radio;
using SmartSdrMcp.Ssb;
using SmartSdrMcp.Tx;

const string MyCallsign = "K1AF";
const string MyName = "PATRICK";
const string MyQth = "Kansas";

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.SetMinimumLevel(LogLevel.Warning);

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

// MCP Server
builder.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<RadioTools>()
    .WithTools<CwListenerTools>()
    .WithTools<CwTransmitTools>()
    .WithTools<SsbListenerTools>()
    .WithTools<ContestTools>()
    .WithResources<RadioStateResource>()
    .WithResources<CwLiveResource>()
    .WithResources<CwRecentResource>()
    .WithResources<CwProposalsResource>();

var app = builder.Build();
await app.RunAsync();
