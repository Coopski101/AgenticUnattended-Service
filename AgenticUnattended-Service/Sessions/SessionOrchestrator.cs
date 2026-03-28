using AgenticUnattended.Config;
using AgenticUnattended.Events;
using AgenticUnattended.Hooks;
using AgenticUnattended.Platform;

namespace AgenticUnattended.Sessions;

public sealed class SessionOrchestrator : BackgroundService
{
    private readonly SessionStateMachine _stateMachine;
    private readonly IPlatformMonitor _monitor;
    private readonly BeaconConfig _config;
    private readonly ILogger<SessionOrchestrator> _logger;

    public SessionOrchestrator(
        SessionStateMachine stateMachine,
        IPlatformMonitor monitor,
        BeaconConfig config,
        ILogger<SessionOrchestrator> logger
    )
    {
        _stateMachine = stateMachine;
        _monitor = monitor;
        _config = config;
        _logger = logger;
    }

    public SessionStateMachine StateMachine => _stateMachine;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _monitor.StartAsync(stoppingToken);

        _monitor.WindowFocusChanged += _stateMachine.OnWindowFocusChanged;

        _logger.LogInformation(
            "Session orchestrator started (AFK threshold={Threshold}s, poll={Poll}ms)",
            _config.AfkThresholdSeconds,
            _config.PollIntervalMs
        );

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(_config.PollIntervalMs, stoppingToken);
            _stateMachine.PollAfk();
            _stateMachine.PollDeadWindows();
        }
    }

    public override void Dispose()
    {
        _monitor.WindowFocusChanged -= _stateMachine.OnWindowFocusChanged;
        _monitor.Dispose();
        base.Dispose();
    }
}
