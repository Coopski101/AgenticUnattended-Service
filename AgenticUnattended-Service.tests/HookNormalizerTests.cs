using AgenticUnattended.Config;
using AgenticUnattended.Events;
using AgenticUnattended.Hooks;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgenticUnattended.Tests;

public sealed class HookNormalizerTests
{
    private static HookNormalizer CreateNormalizer(BeaconConfig? config = null) =>
        new(config ?? new BeaconConfig(), NullLogger<HookNormalizer>.Instance);

    [Fact]
    public void Normalize_CopilotStop_MapsToDone()
    {
        var normalizer = CreateNormalizer();
        var payload = new HookPayload { CopilotHookEventName = "Stop" };

        var result = normalizer.Normalize(payload);

        Assert.NotNull(result);
        Assert.Equal(HookAction.Done, result.Action);
        Assert.Equal(AgentSource.Copilot, result.Source);
    }

    [Fact]
    public void Normalize_CopilotUserPromptSubmit_MapsToClear()
    {
        var normalizer = CreateNormalizer();
        var payload = new HookPayload { CopilotHookEventName = "UserPromptSubmit" };

        var result = normalizer.Normalize(payload);

        Assert.NotNull(result);
        Assert.Equal(HookAction.Clear, result.Action);
    }

    [Fact]
    public void Normalize_CopilotSessionStart_MapsToClear()
    {
        var normalizer = CreateNormalizer();
        var payload = new HookPayload { CopilotHookEventName = "SessionStart" };

        var result = normalizer.Normalize(payload);

        Assert.NotNull(result);
        Assert.Equal(HookAction.Clear, result.Action);
    }

    [Fact]
    public void Normalize_CopilotPreToolUse_MapsToWatchTranscript()
    {
        var normalizer = CreateNormalizer();
        var payload = new HookPayload
        {
            CopilotHookEventName = "PreToolUse",
            TranscriptPath = @"C:\fake\transcript.jsonl",
        };

        var result = normalizer.Normalize(payload);

        Assert.NotNull(result);
        Assert.Equal(HookAction.WatchTranscript, result.Action);
        Assert.Equal(@"C:\fake\transcript.jsonl", result.TranscriptPath);
    }

    [Fact]
    public void Normalize_PreToolUseWithoutTranscriptPath_ReturnsNull()
    {
        var normalizer = CreateNormalizer();
        var payload = new HookPayload { CopilotHookEventName = "PreToolUse" };

        var result = normalizer.Normalize(payload);

        Assert.Null(result);
    }

    [Fact]
    public void Normalize_ClaudeStop_MapsToDone()
    {
        var normalizer = CreateNormalizer();
        var payload = new HookPayload { ClaudeHookEventName = "Stop" };

        var result = normalizer.Normalize(payload);

        Assert.NotNull(result);
        Assert.Equal(HookAction.Done, result.Action);
        Assert.Equal(AgentSource.ClaudeCode, result.Source);
    }

    [Fact]
    public void Normalize_ClaudeSubagentStop_MapsToDone()
    {
        var normalizer = CreateNormalizer();
        var payload = new HookPayload { ClaudeHookEventName = "SubagentStop" };

        var result = normalizer.Normalize(payload);

        Assert.NotNull(result);
        Assert.Equal(HookAction.Done, result.Action);
    }

    [Fact]
    public void Normalize_ClaudePermissionRequest_MapsToWaiting()
    {
        var normalizer = CreateNormalizer();
        var payload = new HookPayload { ClaudeHookEventName = "PermissionRequest" };

        var result = normalizer.Normalize(payload);

        Assert.NotNull(result);
        Assert.Equal(HookAction.Waiting, result.Action);
    }

    [Fact]
    public void Normalize_ClaudeNotificationPermissionPrompt_MapsToWaiting()
    {
        var normalizer = CreateNormalizer();
        var payload = new HookPayload
        {
            ClaudeHookEventName = "Notification",
            NotificationType = "permission_prompt",
        };

        var result = normalizer.Normalize(payload);

        Assert.NotNull(result);
        Assert.Equal(HookAction.Waiting, result.Action);
    }

    [Fact]
    public void Normalize_ClaudeNotificationIdlePrompt_MapsToWaiting()
    {
        var normalizer = CreateNormalizer();
        var payload = new HookPayload
        {
            ClaudeHookEventName = "Notification",
            NotificationType = "idle_prompt",
        };

        var result = normalizer.Normalize(payload);

        Assert.NotNull(result);
        Assert.Equal(HookAction.Waiting, result.Action);
    }

    [Fact]
    public void Normalize_UnknownEvent_ReturnsNull()
    {
        var normalizer = CreateNormalizer();
        var payload = new HookPayload { CopilotHookEventName = "SomeUnknownEvent" };

        var result = normalizer.Normalize(payload);

        Assert.Null(result);
    }

    [Fact]
    public void Normalize_StopHookActive_ReturnsNull()
    {
        var normalizer = CreateNormalizer();
        var payload = new HookPayload
        {
            CopilotHookEventName = "Stop",
            StopHookActive = true,
        };

        var result = normalizer.Normalize(payload);

        Assert.Null(result);
    }

    [Fact]
    public void Normalize_SourceOverride_Copilot_DetectedCorrectly()
    {
        var normalizer = CreateNormalizer();
        var payload = new HookPayload
        {
            CopilotHookEventName = "Stop",
            Source = "copilot",
        };

        var result = normalizer.Normalize(payload);

        Assert.NotNull(result);
        Assert.Equal(AgentSource.Copilot, result.Source);
    }

    [Fact]
    public void Normalize_SourceOverride_Claude_DetectedCorrectly()
    {
        var normalizer = CreateNormalizer();
        var payload = new HookPayload
        {
            ClaudeHookEventName = "Stop",
            Source = "claude",
        };

        var result = normalizer.Normalize(payload);

        Assert.NotNull(result);
        Assert.Equal(AgentSource.ClaudeCode, result.Source);
    }

    [Fact]
    public void Normalize_CustomMappings_AreRespected()
    {
        var config = new BeaconConfig();
        config.CopilotEventMappings["Stop"] = "Waiting";
        var normalizer = CreateNormalizer(config);
        var payload = new HookPayload { CopilotHookEventName = "Stop" };

        var result = normalizer.Normalize(payload);

        Assert.NotNull(result);
        Assert.Equal(HookAction.Waiting, result.Action);
    }

    [Fact]
    public void HookPayload_ResolvedSessionId_PrefersClaude()
    {
        var payload = new HookPayload
        {
            SessionId = "claude-id",
            CopilotSessionId = "copilot-id",
        };

        Assert.Equal("claude-id", payload.ResolvedSessionId);
    }

    [Fact]
    public void HookPayload_ResolvedSessionId_FallsToCopilot()
    {
        var payload = new HookPayload { CopilotSessionId = "copilot-id" };

        Assert.Equal("copilot-id", payload.ResolvedSessionId);
    }

    [Fact]
    public void HookPayload_ResolvedSessionId_DefaultsToUnknown()
    {
        var payload = new HookPayload();

        Assert.Equal("unknown", payload.ResolvedSessionId);
    }

    [Fact]
    public void HookPayload_ResolvedHookEventName_PrefersClaude()
    {
        var payload = new HookPayload
        {
            ClaudeHookEventName = "Stop",
            CopilotHookEventName = "SessionStart",
        };

        Assert.Equal("Stop", payload.ResolvedHookEventName);
    }
}
