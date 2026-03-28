using AgenticUnattended.Platform;

namespace AgenticUnattended.Tests.Fakes;

public sealed class FakePlatformMonitor : IPlatformMonitor
{
    public nint? FocusedWindowHandle { get; set; }
    public string? FocusedWindowProcessName { get; set; }
    public event Action<nint, string>? WindowFocusChanged;
    public TimeSpan UserIdleDuration { get; set; }
    public uint LastInputTick { get; set; }
    private readonly HashSet<nint> _aliveWindows = [];

    public bool IsWindowAlive(nint hwnd) => _aliveWindows.Contains(hwnd);

    public void MarkWindowAlive(nint hwnd) => _aliveWindows.Add(hwnd);

    public void MarkWindowDead(nint hwnd) => _aliveWindows.Remove(hwnd);

    public void SimulateFocusChange(nint hwnd, string processName)
    {
        FocusedWindowHandle = hwnd;
        FocusedWindowProcessName = processName;
        WindowFocusChanged?.Invoke(hwnd, processName);
    }

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    public void Dispose() { }
}
