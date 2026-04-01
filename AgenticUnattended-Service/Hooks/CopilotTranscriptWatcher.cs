using System.Text.Json;
using AgenticUnattended.Config;
using AgenticUnattended.Events;
using AgenticUnattended.Sessions;

namespace AgenticUnattended.Hooks;

public sealed class CopilotTranscriptWatcher : BackgroundService
{
    private readonly SessionStateMachine _stateMachine;
    private readonly ITranscriptFileReader _fileReader;
    private readonly BeaconConfig _config;
    private readonly TimeProvider _time;
    private readonly ILogger<CopilotTranscriptWatcher> _logger;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, TranscriptSession> _sessions = new();

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(300);

    public CopilotTranscriptWatcher(
        SessionStateMachine stateMachine,
        ITranscriptFileReader fileReader,
        BeaconConfig config,
        TimeProvider time,
        ILogger<CopilotTranscriptWatcher> logger
    )
    {
        _stateMachine = stateMachine;
        _fileReader = fileReader;
        _config = config;
        _time = time;
        _logger = logger;
    }

    public void SetTranscriptPath(string sessionId, string path)
    {
        lock (_lock)
        {
            if (
                _sessions.TryGetValue(sessionId, out var existing)
                && string.Equals(existing.Path, path, StringComparison.OrdinalIgnoreCase)
            )
                return;

            var ts = new TranscriptSession { SessionId = sessionId, Path = path };

            _logger.LogInformation(
                "Transcript watcher targeting session {Session}: {Path}",
                sessionId,
                path
            );

            SeekToEnd(ts);
            _sessions[sessionId] = ts;
        }
    }

    public void ConfirmToolStarted(string sessionId, string toolUseId)
    {
        var baseId = StripVscodeSuffix(toolUseId);
        if (!IsParentToolCallId(baseId))
            return;

        lock (_lock)
        {
            if (!_sessions.TryGetValue(sessionId, out var ts))
                return;

            if (!ts.PendingToolCallIds.Remove(baseId))
                return;

            _logger.LogDebug(
                "Transcript [{Session}]: tool {Id} confirmed started via hook (remaining: {Remaining})",
                sessionId,
                baseId.Length <= 12 ? baseId : baseId[^12..],
                ts.PendingToolCallIds.Count
            );

            ts.WaitingTimerCts?.Cancel();
            ts.WaitingTimerCts?.Dispose();
            ts.WaitingTimerCts = null;
        }
    }

    private static string StripVscodeSuffix(string id)
    {
        var idx = id.IndexOf("__vscode-", StringComparison.Ordinal);
        return idx >= 0 ? id[..idx] : id;
    }

    private void SeekToEnd(TranscriptSession ts)
    {
        try
        {
            if (_fileReader.Exists(ts.Path))
                ts.LastFilePosition = _fileReader.GetLength(ts.Path);
        }
        catch { }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Copilot transcript watcher started");

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(PollInterval, stoppingToken);

            List<TranscriptSession> snapshot;
            lock (_lock)
            {
                snapshot = [.. _sessions.Values];
            }

            foreach (var ts in snapshot)
            {
                try
                {
                    ReadNewLines(ts);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(
                        ex,
                        "Error reading transcript for session {Session}",
                        ts.SessionId
                    );
                }
            }
        }
    }

    private void ReadNewLines(TranscriptSession ts)
    {
        if (!_fileReader.Exists(ts.Path))
            return;

        using var fs = _fileReader.OpenRead(ts.Path);

        if (fs.Length <= ts.LastFilePosition)
            return;

        fs.Seek(ts.LastFilePosition, SeekOrigin.Begin);

        using var reader = new StreamReader(fs);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            ProcessLine(ts, line);
        }

        ts.LastFilePosition = fs.Position;
    }

    private void ProcessLine(TranscriptSession ts, string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeProp))
                return;

            var type = typeProp.GetString();

            switch (type)
            {
                case "assistant.message":
                    HandleAssistantMessage(ts, root);
                    break;
                case "tool.execution_start":
                    HandleToolExecutionStart(ts, root);
                    break;
                case "tool.execution_complete":
                    HandleToolExecutionComplete(ts, root);
                    break;
            }
        }
        catch (JsonException) { }
    }

    private static bool IsParentToolCallId(string id) =>
        id.StartsWith("toolu_", StringComparison.Ordinal);

    private void HandleAssistantMessage(TranscriptSession ts, JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data))
            return;

        if (!data.TryGetProperty("toolRequests", out var toolRequests))
            return;

        if (toolRequests.ValueKind != JsonValueKind.Array || toolRequests.GetArrayLength() == 0)
            return;

        var toolCallIds = new List<string>();
        foreach (var req in toolRequests.EnumerateArray())
        {
            if (req.TryGetProperty("toolCallId", out var idProp))
            {
                var id = idProp.GetString();
                if (id is not null && IsParentToolCallId(id))
                    toolCallIds.Add(id);
            }
        }

        if (toolCallIds.Count == 0)
            return;

        lock (_lock)
        {
            foreach (var id in toolCallIds)
            {
                ts.PendingToolCallIds.Add(id);
            }
        }

        _logger.LogDebug(
            "Transcript [{Session}]: {Count} parent tool request(s) pending approval: [{Ids}]",
            ts.SessionId,
            toolCallIds.Count,
            string.Join(", ", toolCallIds.Select(id => id.Length <= 12 ? id : id[^12..]))
        );

        StartApprovalTimer(ts);
    }

    private void HandleToolExecutionStart(TranscriptSession ts, JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data))
            return;

        if (!data.TryGetProperty("toolCallId", out var idProp))
            return;

        var toolCallId = idProp.GetString();
        if (toolCallId is null)
            return;

        if (!IsParentToolCallId(toolCallId))
        {
            _logger.LogTrace(
                "Transcript [{Session}]: ignoring inner tool {Id} execution_start",
                ts.SessionId,
                toolCallId.Length <= 12 ? toolCallId : toolCallId[^12..]
            );
            return;
        }

        bool wasWaiting;

        lock (_lock)
        {
            wasWaiting = ts.WaitingPublished;
            ts.PendingToolCallIds.Remove(toolCallId);

            ts.WaitingTimerCts?.Cancel();
            ts.WaitingTimerCts?.Dispose();
            ts.WaitingTimerCts = null;
        }

        if (wasWaiting)
        {
            _logger.LogInformation(
                "Transcript [{Session}]: tool {Id} approved, sending Clear (remaining: {Remaining})",
                ts.SessionId,
                toolCallId.Length <= 12 ? toolCallId : toolCallId[^12..],
                ts.PendingToolCallIds.Count
            );

            lock (_lock)
            {
                ts.WaitingPublished = false;
            }

            _stateMachine.HandleStateChange(
                ts.SessionId,
                AgentSource.Copilot,
                HookAction.Clear,
                "TranscriptApproval",
                "User approved tool execution"
            );
        }
        else
        {
            _logger.LogDebug(
                "Transcript [{Session}]: tool {Id} started (auto-approved, remaining: {Remaining})",
                ts.SessionId,
                toolCallId.Length <= 12 ? toolCallId : toolCallId[^12..],
                ts.PendingToolCallIds.Count
            );
        }
    }

    private void HandleToolExecutionComplete(TranscriptSession ts, JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data))
            return;

        if (!data.TryGetProperty("toolCallId", out var idProp))
            return;

        var toolCallId = idProp.GetString();
        if (toolCallId is null)
            return;

        if (!IsParentToolCallId(toolCallId))
            return;

        bool hasPending;
        lock (_lock)
        {
            hasPending = ts.PendingToolCallIds.Count > 0;
        }

        if (hasPending)
        {
            _logger.LogDebug(
                "Transcript [{Session}]: tool {Id} completed, {Remaining} sibling(s) still pending [{Ids}] — restarting approval timer",
                ts.SessionId,
                toolCallId.Length <= 12 ? toolCallId : toolCallId[^12..],
                ts.PendingToolCallIds.Count,
                string.Join(", ", ts.PendingToolCallIds.Select(id => id.Length <= 12 ? id : id[^12..]))
            );
            StartApprovalTimer(ts);
        }
    }

    private void StartApprovalTimer(TranscriptSession ts)
    {
        lock (_lock)
        {
            ts.WaitingTimerCts?.Cancel();
            ts.WaitingTimerCts?.Dispose();
            ts.WaitingTimerCts = new CancellationTokenSource();
        }

        var cts = ts.WaitingTimerCts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(_config.AutoApprovedToolDetectionDelayMs), _time, cts!.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            bool hasPending;
            lock (_lock)
            {
                hasPending = ts.PendingToolCallIds.Count > 0;
            }

            if (!hasPending)
                return;

            lock (_lock)
            {
                ts.WaitingPublished = true;
            }

            string pendingIds;
            lock (_lock)
            {
                pendingIds = string.Join(", ", ts.PendingToolCallIds.Select(id => id.Length <= 12 ? id : id[^12..]));
            }

            _logger.LogInformation(
                "Transcript [{Session}]: no tool.execution_start after {Delay}ms for [{Ids}] — sending Waiting",
                ts.SessionId,
                _config.AutoApprovedToolDetectionDelayMs,
                pendingIds
            );

            _stateMachine.HandleStateChange(
                ts.SessionId,
                AgentSource.Copilot,
                HookAction.Waiting,
                "TranscriptApprovalPending",
                "Copilot waiting for user to approve tool execution"
            );
        });
    }

    public override void Dispose()
    {
        lock (_lock)
        {
            foreach (var ts in _sessions.Values)
            {
                ts.WaitingTimerCts?.Cancel();
                ts.WaitingTimerCts?.Dispose();
            }
        }
        base.Dispose();
    }

    private sealed class TranscriptSession
    {
        public required string SessionId { get; init; }
        public required string Path { get; init; }
        public long LastFilePosition { get; set; }
        public HashSet<string> PendingToolCallIds { get; } = [];
        public CancellationTokenSource? WaitingTimerCts { get; set; }
        public bool WaitingPublished { get; set; }
    }
}
