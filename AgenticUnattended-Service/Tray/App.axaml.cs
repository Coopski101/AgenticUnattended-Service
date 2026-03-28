using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using AgenticUnattended.Config;
using AgenticUnattended.Events;
using AgenticUnattended.Hooks;
using AgenticUnattended.Platform;
using AgenticUnattended.Platform.macOS;
using AgenticUnattended.Platform.Windows;
using AgenticUnattended.Server;
using AgenticUnattended.Services;
using AgenticUnattended.Sessions;

namespace AgenticUnattended.Tray;

public partial class App : Application
{
    private CancellationTokenSource? _cts;
    private LogWindow? _logWindow;
    private TrayIcon? _trayIcon;

    public static LogSink LogSink { get; } = new();

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _cts = new CancellationTokenSource();
        SetupTrayIcon();
        _ = Task.Run(() => StartWebHostAsync(_cts.Token));

        base.OnFrameworkInitializationCompleted();
    }

    private void SetupTrayIcon()
    {
        var showLog = new NativeMenuItem("Show Log");
        showLog.Click += (_, _) => ToggleLogWindow();

        var autoStart = new NativeMenuItem("Auto-start")
        {
            ToggleType = NativeMenuItemToggleType.CheckBox,
            IsChecked = AutoStartManager.IsEnabled(),
        };
        autoStart.Click += (_, _) =>
        {
            var enabled = !AutoStartManager.IsEnabled();
            AutoStartManager.SetEnabled(enabled);
            autoStart.IsChecked = enabled;
        };

        var exit = new NativeMenuItem("Exit");
        exit.Click += (_, _) => DoShutdown();

        var menu = new NativeMenu();
        menu.Items.Add(showLog);
        menu.Items.Add(autoStart);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exit);

        _trayIcon = new TrayIcon
        {
            ToolTipText = "Agentic Unattended Service",
            Menu = menu,
            Icon = CreateIcon(),
            IsVisible = true,
        };
        _trayIcon.Clicked += (_, _) => ToggleLogWindow();
    }

    private void ToggleLogWindow()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_logWindow is null || !_logWindow.IsVisible)
            {
                _logWindow ??= new LogWindow();
                _logWindow.Show();
                _logWindow.Activate();
            }
            else
            {
                _logWindow.Hide();
            }
        });
    }

    private void DoShutdown()
    {
        _cts?.Cancel();
        _trayIcon?.Dispose();
        _trayIcon = null;

        Dispatcher.UIThread.Post(() =>
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
        });
    }

    private async Task StartWebHostAsync(CancellationToken ct)
    {
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ContentRootPath = AppContext.BaseDirectory,
            });

            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(new LogSinkProvider(LogSink));
            var fileLogSink = new FileLogSink();
            builder.Logging.AddProvider(new FileLogSinkProvider(fileLogSink));
            builder.Logging.AddFilter<FileLogSinkProvider>(null, LogLevel.Trace);

            var config = new BeaconConfig();
            builder.Configuration.GetSection("Beacon").Bind(config);

            var bus = new EventBus();

            builder.Services.AddSingleton(bus);
            builder.Services.AddSingleton(config);
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddSingleton<ITranscriptFileReader, TranscriptFileReader>();
            builder.Services.AddSingleton<HookNormalizer>();
            builder.Services.AddSingleton<SessionRegistry>();
            builder.Services.AddSingleton<SessionStateMachine>();
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
                    builder.Services.AddSingleton<IPlatformMonitor, WindowsPlatformMonitor>();
                else if (OperatingSystem.IsMacOS())
                    builder.Services.AddSingleton<IPlatformMonitor, MacPlatformMonitor>();
                else
                    builder.Services.AddSingleton<IPlatformMonitor, NullPlatformMonitor>();

                builder.Services.AddSingleton<SessionOrchestrator>();
                builder.Services.AddHostedService(sp => sp.GetRequiredService<SessionOrchestrator>());
            }

            var app = builder.Build();

            app.MapBeaconEndpoints(bus);
            app.Urls.Add($"http://127.0.0.1:{config.Port}");

            LogSink.Write($"[{DateTime.Now:HH:mm:ss}] INFO Service: Listening on http://127.0.0.1:{config.Port}");
            await app.RunAsync(ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogSink.Write($"[{DateTime.Now:HH:mm:ss}] CRIT Service: {ex}");
        }
    }

    public static WindowIcon? CreateIcon()
    {
        try
        {
            int size = 32;
            using var bitmap = new WriteableBitmap(
                new PixelSize(size, size),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Premul);

            using (var fb = bitmap.Lock())
            {
                unsafe
                {
                    var ptr = (byte*)fb.Address.ToPointer();
                    int stride = fb.RowBytes;

                    void SetPixel(int px, int py, byte r, byte g, byte b, byte a = 255)
                    {
                        if (px < 0 || px >= size || py < 0 || py >= size) return;
                        int idx = py * stride + px * 4;
                        ptr[idx] = b;
                        ptr[idx + 1] = g;
                        ptr[idx + 2] = r;
                        ptr[idx + 3] = a;
                    }

                    void FillRect(int x0, int y0, int w, int h, byte r, byte g, byte b)
                    {
                        for (int py = y0; py < y0 + h; py++)
                        for (int px = x0; px < x0 + w; px++)
                            SetPixel(px, py, r, g, b);
                    }

                    // "A" in red — left half (columns 1-14)
                    // Legs
                    FillRect(1, 6, 3, 22, 220, 50, 50);   // left leg
                    FillRect(12, 6, 3, 22, 220, 50, 50);   // right leg
                    // Top peak
                    FillRect(4, 4, 8, 3, 220, 50, 50);
                    FillRect(5, 2, 6, 2, 220, 50, 50);
                    FillRect(6, 1, 4, 1, 220, 50, 50);
                    // Crossbar
                    FillRect(4, 17, 8, 3, 220, 50, 50);

                    // "I" in green — right half (columns 18-30)
                    // Top bar
                    FillRect(19, 1, 10, 3, 50, 200, 50);
                    // Vertical stem
                    FillRect(22, 4, 4, 20, 50, 200, 50);
                    // Bottom bar
                    FillRect(19, 24, 10, 3, 50, 200, 50);
                }
            }

            using var ms = new MemoryStream();
            bitmap.Save(ms);
            ms.Position = 0;
            return new WindowIcon(ms);
        }
        catch
        {
            return null;
        }
    }
}
