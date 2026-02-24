using AgenticUnattended.Config;
using AgenticUnattended.Events;
using AgenticUnattended.Hooks;
using AgenticUnattended.Platform;
using AgenticUnattended.Platform.Windows;
using AgenticUnattended.Server;
using AgenticUnattended.Services;
using AgenticUnattended.Sessions;

var builder = WebApplication.CreateBuilder(args);

var config = new BeaconConfig();
builder.Configuration.GetSection("Beacon").Bind(config);

var bus = new EventBus();

builder.Services.AddSingleton(bus);
builder.Services.AddSingleton(config);
builder.Services.AddSingleton<HookNormalizer>();
builder.Services.AddSingleton<SessionRegistry>();
builder.Services.AddSingleton<CopilotTranscriptWatcher>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CopilotTranscriptWatcher>());

if (config.FakeMode)
{
    builder.Services.AddSingleton<IPlatformMonitor, NullPlatformMonitor>();
    builder.Services.AddSingleton<SessionOrchestrator>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<SessionOrchestrator>());
    builder.Services.AddHostedService<FakeEventEmitter>();
}
else
{
    if (OperatingSystem.IsWindows())
    {
        builder.Services.AddSingleton<IPlatformMonitor, WindowsPlatformMonitor>();
    }
    else
    {
        builder.Services.AddSingleton<IPlatformMonitor, NullPlatformMonitor>();
    }

    builder.Services.AddSingleton<SessionOrchestrator>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<SessionOrchestrator>());
}

var app = builder.Build();

app.MapBeaconEndpoints(bus);

app.Urls.Add($"http://127.0.0.1:{config.Port}");

app.Run();
