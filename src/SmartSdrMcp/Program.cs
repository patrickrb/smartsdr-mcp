using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartSdrMcp.Ai;
using SmartSdrMcp.Audio;
using SmartSdrMcp.Cw;
using SmartSdrMcp.Mcp.Resources;
using SmartSdrMcp.Mcp.Tools;
using SmartSdrMcp.Qso;
using SmartSdrMcp.Radio;
using SmartSdrMcp.Tx;

const string MyCallsign = "K1AF";
const string MyName = "PATRICK";

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

// MCP Server
builder.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<RadioTools>()
    .WithTools<CwListenerTools>()
    .WithTools<CwTransmitTools>()
    .WithResources<RadioStateResource>()
    .WithResources<CwLiveResource>()
    .WithResources<CwRecentResource>()
    .WithResources<CwProposalsResource>();

var app = builder.Build();
await app.RunAsync();
