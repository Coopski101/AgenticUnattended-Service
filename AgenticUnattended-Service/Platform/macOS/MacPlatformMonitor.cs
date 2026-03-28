using System.Diagnostics;
using System.Runtime.Versioning;

namespace AgenticUnattended.Platform.macOS;

[SupportedOSPlatform("macos")]
public sealed class MacPlatformMonitor : IPlatformMonitor
{
    private readonly ILogger<MacPlatformMonitor> _logger;
    private nint? _lastFocusedHwnd;
    private string? _lastFocusedProcessName;
    private CancellationTokenSource? _cts;

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    public nint? FocusedWindowHandle => _lastFocusedHwnd;
    public string? FocusedWindowProcessName => _lastFocusedProcessName;
    public event Action<nint, string>? WindowFocusChanged;

    public MacPlatformMonitor(ILogger<MacPlatformMonitor> logger)
    {
        _logger = logger;
    }

    public TimeSpan UserIdleDuration
    {
        get
        {
            var seconds = MacNative.CGEventSourceSecondsSinceLastEventType(
                MacNative.kCGEventSourceStateCombinedSessionState,
                MacNative.kCGAnyInputEventType);
            return TimeSpan.FromSeconds(seconds);
        }
    }

    public uint LastInputTick
    {
        get
        {
            var idle = UserIdleDuration;
            return (uint)(Environment.TickCount - (int)idle.TotalMilliseconds);
        }
    }

    public bool IsWindowAlive(nint hwnd)
    {
        try
        {
            var pid = (int)hwnd;
            if (pid <= 0) return false;
            var proc = Process.GetProcessById(pid);
            return !proc.HasExited;
        }
        catch
        {
            return false;
        }
    }

    public Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        ReadCurrentForeground();
        _logger.LogInformation("macOS platform monitor started (polling)");

        _ = Task.Run(() => PollLoop(_cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }

    private async Task PollLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollInterval, ct);
                CheckForeground();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogTrace(ex, "Error polling foreground app");
            }
        }
    }

    private void ReadCurrentForeground()
    {
        try
        {
            var workspace = MacNative.GetSharedWorkspace();
            if (workspace == nint.Zero) return;

            var app = MacNative.GetFrontmostApplication(workspace);
            if (app == nint.Zero) return;

            var pid = MacNative.GetProcessIdentifier(app);
            var name = MacNative.GetLocalizedName(app) ?? "Unknown";

            _lastFocusedHwnd = (nint)pid;
            _lastFocusedProcessName = name;
            _logger.LogInformation("Initial foreground: {Process} (pid={Pid})", name, pid);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read initial foreground app");
        }
    }

    private void CheckForeground()
    {
        var workspace = MacNative.GetSharedWorkspace();
        if (workspace == nint.Zero) return;

        var app = MacNative.GetFrontmostApplication(workspace);
        if (app == nint.Zero) return;

        var pid = MacNative.GetProcessIdentifier(app);
        var handle = (nint)pid;

        if (handle == _lastFocusedHwnd) return;

        var name = MacNative.GetLocalizedName(app) ?? "Unknown";
        _logger.LogDebug("Foreground changed to {Process} (pid={Pid})", name, pid);
        _lastFocusedHwnd = handle;
        _lastFocusedProcessName = name;

        WindowFocusChanged?.Invoke(handle, name);
    }
}
