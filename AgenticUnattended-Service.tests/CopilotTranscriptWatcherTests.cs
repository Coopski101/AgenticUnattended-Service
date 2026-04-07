using AgenticUnattended.Config;
using AgenticUnattended.Events;
using AgenticUnattended.Hooks;
using AgenticUnattended.Sessions;
using AgenticUnattended.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgenticUnattended.Tests;

public sealed class CopilotTranscriptWatcherTests : IDisposable
{
    private readonly FakePlatformMonitor _monitor = new();
    private readonly EventBus _bus = new();
    private readonly SessionRegistry _registry = new();
    private readonly FakeTranscriptFileReader _fileReader = new();
    private readonly BeaconConfig _config = new() { AutoApprovedToolDetectionDelayMs = 100 };
    private readonly EventCollector _events;
    private readonly SessionStateMachine _sm;
    private readonly CopilotTranscriptWatcher _watcher;

    public CopilotTranscriptWatcherTests()
    {
        _events = new EventCollector(_bus);
        _sm = new SessionStateMachine(
            _registry,
            _monitor,
            _bus,
            _config,
            TimeProvider.System,
            NullLogger<SessionStateMachine>.Instance
        );
        _watcher = new CopilotTranscriptWatcher(
            _sm,
            _fileReader,
            _config,
            TimeProvider.System,
            NullLogger<CopilotTranscriptWatcher>.Instance
        );
    }

    public void Dispose()
    {
        _watcher.Dispose();
        _events.Dispose();
    }

    [Fact]
    public void SetTranscriptPath_SeeksToEndOfExistingFile()
    {
        _fileReader.SetFileContent(@"C:\t.jsonl", "existing content\n");

        _watcher.SetTranscriptPath("s1", @"C:\t.jsonl");

        _fileReader.AppendContent(@"C:\t.jsonl",
            """
            {"type":"assistant.message","data":{"toolRequests":[{"toolCallId":"toolu_tc1"}]}}
            """);

        TriggerPoll();

        // No crash, and the tool request should be picked up from the new content
    }

    [Fact]
    public async Task AssistantMessage_WithToolRequests_AfterDelay_PublishesWaiting()
    {
        _sm.HandleStateChange("s1", AgentSource.Copilot, HookAction.Clear, "Setup", "setup");

        _fileReader.SetFileContent(@"C:\t.jsonl", "");
        _watcher.SetTranscriptPath("s1", @"C:\t.jsonl");

        _fileReader.AppendContent(@"C:\t.jsonl",
            """{"type":"assistant.message","data":{"toolRequests":[{"toolCallId":"toolu_tc1"}]}}""" + "\n");

        TriggerPoll();

        await Task.Delay(200);

        Assert.Contains(_events.Events, e =>
            e.EventType == BeaconEventType.Waiting
            && e.SessionId == "s1"
            && e.HookEvent == "TranscriptApprovalPending");
    }

    [Fact]
    public async Task ToolExecutionStart_BeforeDelay_CancelsWaiting()
    {
        _sm.HandleStateChange("s1", AgentSource.Copilot, HookAction.Clear, "Setup", "setup");

        _fileReader.SetFileContent(@"C:\t.jsonl", "");
        _watcher.SetTranscriptPath("s1", @"C:\t.jsonl");

        _fileReader.AppendContent(@"C:\t.jsonl",
            """{"type":"assistant.message","data":{"toolRequests":[{"toolCallId":"toolu_tc1"}]}}""" + "\n");
        TriggerPoll();

        _fileReader.AppendContent(@"C:\t.jsonl",
            """{"type":"tool.execution_start","data":{"toolCallId":"toolu_tc1"}}""" + "\n");
        TriggerPoll();

        await Task.Delay(200);

        Assert.DoesNotContain(_events.Events, e =>
            e.EventType == BeaconEventType.Waiting
            && e.HookEvent == "TranscriptApprovalPending");
    }

    [Fact]
    public async Task ToolApproved_AfterWaiting_PublishesClear()
    {
        _sm.HandleStateChange("s1", AgentSource.Copilot, HookAction.Clear, "Setup", "setup");

        _fileReader.SetFileContent(@"C:\t.jsonl", "");
        _watcher.SetTranscriptPath("s1", @"C:\t.jsonl");

        _fileReader.AppendContent(@"C:\t.jsonl",
            """{"type":"assistant.message","data":{"toolRequests":[{"toolCallId":"toolu_tc1"}]}}""" + "\n");
        TriggerPoll();

        await Task.Delay(200);

        Assert.Contains(_events.Events, e => e.EventType == BeaconEventType.Waiting);

        _fileReader.AppendContent(@"C:\t.jsonl",
            """{"type":"tool.execution_start","data":{"toolCallId":"toolu_tc1"}}""" + "\n");
        TriggerPoll();

        Assert.Contains(_events.Events, e =>
            e.EventType == BeaconEventType.Clear
            && e.HookEvent == "TranscriptApproval");
    }

    [Fact]
    public void AssistantMessage_WithoutToolRequests_IsIgnored()
    {
        _fileReader.SetFileContent(@"C:\t.jsonl", "");
        _watcher.SetTranscriptPath("s1", @"C:\t.jsonl");

        _fileReader.AppendContent(@"C:\t.jsonl",
            """{"type":"assistant.message","data":{"content":"hello"}}""" + "\n");
        TriggerPoll();

        Assert.DoesNotContain(_events.Events, e => e.EventType == BeaconEventType.Waiting);
    }

    [Fact]
    public void NonExistentFile_DoesNotThrow()
    {
        _watcher.SetTranscriptPath("s1", @"C:\does\not\exist.jsonl");

        TriggerPoll();
    }

    [Fact]
    public void InvalidJson_IsSkipped()
    {
        _fileReader.SetFileContent(@"C:\t.jsonl", "");
        _watcher.SetTranscriptPath("s1", @"C:\t.jsonl");

        _fileReader.AppendContent(@"C:\t.jsonl", "this is not json\n");
        TriggerPoll();
    }

    [Fact]
    public void SetTranscriptPath_SamePath_DoesNotReSeek()
    {
        _fileReader.SetFileContent(@"C:\t.jsonl",
            """{"type":"assistant.message","data":{"toolRequests":[{"toolCallId":"toolu_tc1"}]}}""" + "\n");
        _watcher.SetTranscriptPath("s1", @"C:\t.jsonl");

        _fileReader.AppendContent(@"C:\t.jsonl",
            """{"type":"assistant.message","data":{"toolRequests":[{"toolCallId":"toolu_tc2"}]}}""" + "\n");
        _watcher.SetTranscriptPath("s1", @"C:\t.jsonl");

        TriggerPoll();
    }

    [Fact]
    public async Task MultipleToolRequests_FirstStartCancelsTimer_NoFalseWaiting()
    {
        _sm.HandleStateChange("s1", AgentSource.Copilot, HookAction.Clear, "Setup", "setup");

        _fileReader.SetFileContent(@"C:\t.jsonl", "");
        _watcher.SetTranscriptPath("s1", @"C:\t.jsonl");

        _fileReader.AppendContent(@"C:\t.jsonl",
            """{"type":"assistant.message","data":{"toolRequests":[{"toolCallId":"toolu_tc1"},{"toolCallId":"toolu_tc2"}]}}""" + "\n");
        TriggerPoll();

        _fileReader.AppendContent(@"C:\t.jsonl",
            """{"type":"tool.execution_start","data":{"toolCallId":"toolu_tc1"}}""" + "\n");
        TriggerPoll();

        await Task.Delay(200);

        Assert.DoesNotContain(_events.Events, e =>
            e.EventType == BeaconEventType.Waiting
            && e.HookEvent == "TranscriptApprovalPending");
    }

    [Fact]
    public async Task MultipleToolRequests_CompleteThenSiblingNeedsApproval_PublishesWaiting()
    {
        _sm.HandleStateChange("s1", AgentSource.Copilot, HookAction.Clear, "Setup", "setup");

        _fileReader.SetFileContent(@"C:\t.jsonl", "");
        _watcher.SetTranscriptPath("s1", @"C:\t.jsonl");

        _fileReader.AppendContent(@"C:\t.jsonl",
            """{"type":"assistant.message","data":{"toolRequests":[{"toolCallId":"toolu_tc1"},{"toolCallId":"toolu_tc2"}]}}""" + "\n");
        TriggerPoll();

        _fileReader.AppendContent(@"C:\t.jsonl",
            """{"type":"tool.execution_start","data":{"toolCallId":"toolu_tc1"}}""" + "\n");
        TriggerPoll();

        _fileReader.AppendContent(@"C:\t.jsonl",
            """{"type":"tool.execution_complete","data":{"toolCallId":"toolu_tc1"}}""" + "\n");
        TriggerPoll();

        await Task.Delay(200);

        Assert.Contains(_events.Events, e =>
            e.EventType == BeaconEventType.Waiting
            && e.HookEvent == "TranscriptApprovalPending");
    }

    [Fact]
    public async Task MultipleToolRequests_AllAutoApproved_NoWaiting()
    {
        _sm.HandleStateChange("s1", AgentSource.Copilot, HookAction.Clear, "Setup", "setup");

        _fileReader.SetFileContent(@"C:\t.jsonl", "");
        _watcher.SetTranscriptPath("s1", @"C:\t.jsonl");

        _fileReader.AppendContent(@"C:\t.jsonl",
            """{"type":"assistant.message","data":{"toolRequests":[{"toolCallId":"toolu_tc1"},{"toolCallId":"toolu_tc2"}]}}""" + "\n");
        TriggerPoll();

        _fileReader.AppendContent(@"C:\t.jsonl",
            """{"type":"tool.execution_start","data":{"toolCallId":"toolu_tc1"}}""" + "\n");
        TriggerPoll();

        _fileReader.AppendContent(@"C:\t.jsonl",
            """{"type":"tool.execution_complete","data":{"toolCallId":"toolu_tc1"}}""" + "\n");
        TriggerPoll();

        _fileReader.AppendContent(@"C:\t.jsonl",
            """{"type":"tool.execution_start","data":{"toolCallId":"toolu_tc2"}}""" + "\n");
        TriggerPoll();

        await Task.Delay(200);

        Assert.DoesNotContain(_events.Events, e =>
            e.EventType == BeaconEventType.Waiting
            && e.HookEvent == "TranscriptApprovalPending");
    }

    [Fact]
    public async Task MultipleToolRequests_SecondNeedsApproval_ThenApproved_PublishesClear()
    {
        _sm.HandleStateChange("s1", AgentSource.Copilot, HookAction.Clear, "Setup", "setup");

        _fileReader.SetFileContent(@"C:\t.jsonl", "");
        _watcher.SetTranscriptPath("s1", @"C:\t.jsonl");

        _fileReader.AppendContent(@"C:\t.jsonl",
            """{"type":"assistant.message","data":{"toolRequests":[{"toolCallId":"toolu_tc1"},{"toolCallId":"toolu_tc2"}]}}""" + "\n");
        TriggerPoll();

        _fileReader.AppendContent(@"C:\t.jsonl",
            """{"type":"tool.execution_start","data":{"toolCallId":"toolu_tc1"}}""" + "\n");
        TriggerPoll();

        _fileReader.AppendContent(@"C:\t.jsonl",
            """{"type":"tool.execution_complete","data":{"toolCallId":"toolu_tc1"}}""" + "\n");
        TriggerPoll();

        await Task.Delay(200);
        Assert.Contains(_events.Events, e => e.EventType == BeaconEventType.Waiting);

        _fileReader.AppendContent(@"C:\t.jsonl",
            """{"type":"tool.execution_start","data":{"toolCallId":"toolu_tc2"}}""" + "\n");
        TriggerPoll();

        Assert.Contains(_events.Events, e =>
            e.EventType == BeaconEventType.Clear
            && e.HookEvent == "TranscriptApproval");
    }

    [Fact]
    public async Task InnerSubagentToolCalls_AreIgnored_DoNotCauseWaiting()
    {
        _sm.HandleStateChange("s1", AgentSource.Copilot, HookAction.Clear, "Setup", "setup");

        _fileReader.SetFileContent(@"C:\t.jsonl", "");
        _watcher.SetTranscriptPath("s1", @"C:\t.jsonl");

        _fileReader.AppendContent(@"C:\t.jsonl",
            """{"type":"assistant.message","data":{"toolRequests":[{"toolCallId":"toolu_parent1"}]}}""" + "\n");
        TriggerPoll();

        _fileReader.AppendContent(@"C:\t.jsonl",
            """{"type":"tool.execution_start","data":{"toolCallId":"toolu_parent1"}}""" + "\n");
        TriggerPoll();

        _fileReader.AppendContent(@"C:\t.jsonl",
            """{"type":"assistant.message","data":{"toolRequests":[{"toolCallId":"call_innerABC"}]}}""" + "\n");
        TriggerPoll();

        _fileReader.AppendContent(@"C:\t.jsonl",
            """{"type":"tool.execution_start","data":{"toolCallId":"call_innerABC"}}""" + "\n");
        TriggerPoll();

        _fileReader.AppendContent(@"C:\t.jsonl",
            """{"type":"tool.execution_complete","data":{"toolCallId":"call_innerABC"}}""" + "\n");
        TriggerPoll();

        await Task.Delay(200);

        Assert.DoesNotContain(_events.Events, e =>
            e.EventType == BeaconEventType.Waiting
            && e.HookEvent == "TranscriptApprovalPending");
    }

    [Fact]
    public async Task InnerSubagentComplete_DoesNotRestartTimerForParentIds()
    {
        _sm.HandleStateChange("s1", AgentSource.Copilot, HookAction.Clear, "Setup", "setup");

        _fileReader.SetFileContent(@"C:\t.jsonl", "");
        _watcher.SetTranscriptPath("s1", @"C:\t.jsonl");

        _fileReader.AppendContent(@"C:\t.jsonl",
            """{"type":"assistant.message","data":{"toolRequests":[{"toolCallId":"toolu_p1"},{"toolCallId":"toolu_p2"}]}}""" + "\n");
        TriggerPoll();

        _fileReader.AppendContent(@"C:\t.jsonl",
            """{"type":"tool.execution_start","data":{"toolCallId":"toolu_p1"}}""" + "\n");
        TriggerPoll();

        _fileReader.AppendContent(@"C:\t.jsonl",
            """{"type":"assistant.message","data":{"toolRequests":[{"toolCallId":"call_inner1"}]}}""" + "\n");
        TriggerPoll();

        _fileReader.AppendContent(@"C:\t.jsonl",
            """{"type":"tool.execution_start","data":{"toolCallId":"call_inner1"}}""" + "\n");
        TriggerPoll();

        _fileReader.AppendContent(@"C:\t.jsonl",
            """{"type":"tool.execution_complete","data":{"toolCallId":"call_inner1"}}""" + "\n");
        TriggerPoll();

        await Task.Delay(200);

        Assert.DoesNotContain(_events.Events, e =>
            e.EventType == BeaconEventType.Waiting
            && e.HookEvent == "TranscriptApprovalPending");
    }

    [Fact]
    public async Task ConfirmToolStarted_ViaHook_RestartsTimer_ExecutionStartClearsBeforeTimeout()
    {
        _sm.HandleStateChange("s1", AgentSource.Copilot, HookAction.Clear, "Setup", "setup");

        _fileReader.SetFileContent(@"C:\t.jsonl", "");
        _watcher.SetTranscriptPath("s1", @"C:\t.jsonl");

        _fileReader.AppendContent(@"C:\t.jsonl",
            """  {"type":"assistant.message","data":{"toolRequests":[{"toolCallId":"toolu_bdrk_01ABC123"}]}}""" + "\n");
        TriggerPoll();

        _watcher.ConfirmToolStarted("s1", "toolu_bdrk_01ABC123__vscode-12345");

        _fileReader.AppendContent(@"C:\t.jsonl",
            """  {"type":"tool.execution_start","data":{"toolCallId":"toolu_bdrk_01ABC123"}}""" + "\n");
        TriggerPoll();

        await Task.Delay(200);

        Assert.DoesNotContain(_events.Events, e =>
            e.EventType == BeaconEventType.Waiting
            && e.HookEvent == "TranscriptApprovalPending");
    }

    [Fact]
    public async Task ConfirmToolStarted_ViaHook_WithoutVscodeSuffix_ExecutionStartClears()
    {
        _sm.HandleStateChange("s1", AgentSource.Copilot, HookAction.Clear, "Setup", "setup");

        _fileReader.SetFileContent(@"C:\t.jsonl", "");
        _watcher.SetTranscriptPath("s1", @"C:\t.jsonl");

        _fileReader.AppendContent(@"C:\t.jsonl",
            """  {"type":"assistant.message","data":{"toolRequests":[{"toolCallId":"toolu_bdrk_01XYZ789"}]}}""" + "\n");
        TriggerPoll();

        _watcher.ConfirmToolStarted("s1", "toolu_bdrk_01XYZ789");

        _fileReader.AppendContent(@"C:\t.jsonl",
            """  {"type":"tool.execution_start","data":{"toolCallId":"toolu_bdrk_01XYZ789"}}""" + "\n");
        TriggerPoll();

        await Task.Delay(200);

        Assert.DoesNotContain(_events.Events, e =>
            e.EventType == BeaconEventType.Waiting
            && e.HookEvent == "TranscriptApprovalPending");
    }

    [Fact]
    public async Task ConfirmToolStarted_ViaHook_BatchPartialConfirm_SiblingStillTimesOut()
    {
        _sm.HandleStateChange("s1", AgentSource.Copilot, HookAction.Clear, "Setup", "setup");

        _fileReader.SetFileContent(@"C:\t.jsonl", "");
        _watcher.SetTranscriptPath("s1", @"C:\t.jsonl");

        _fileReader.AppendContent(@"C:\t.jsonl",
            """{"type":"assistant.message","data":{"toolRequests":[{"toolCallId":"toolu_bdrk_01AAA"},{"toolCallId":"toolu_bdrk_01BBB"}]}}""" + "\n");
        TriggerPoll();

        _watcher.ConfirmToolStarted("s1", "toolu_bdrk_01AAA__vscode-999");

        _fileReader.AppendContent(@"C:\t.jsonl",
            """{"type":"tool.execution_complete","data":{"toolCallId":"toolu_bdrk_01AAA"}}""" + "\n");
        TriggerPoll();

        await Task.Delay(200);

        Assert.Contains(_events.Events, e =>
            e.EventType == BeaconEventType.Waiting
            && e.HookEvent == "TranscriptApprovalPending");
    }

    [Fact]
    public void ConfirmToolStarted_UnknownSession_DoesNotThrow()
    {
        _watcher.ConfirmToolStarted("nonexistent", "toolu_bdrk_01ABC__vscode-1");
    }

    [Fact]
    public async Task ConfirmToolStarted_UnknownToolId_DoesNotCancelTimer()
    {
        _sm.HandleStateChange("s1", AgentSource.Copilot, HookAction.Clear, "Setup", "setup");

        _fileReader.SetFileContent(@"C:\t.jsonl", "");
        _watcher.SetTranscriptPath("s1", @"C:\t.jsonl");

        _fileReader.AppendContent(@"C:\t.jsonl",
            """{"type":"assistant.message","data":{"toolRequests":[{"toolCallId":"toolu_bdrk_01REAL"}]}}""" + "\n");
        TriggerPoll();

        _watcher.ConfirmToolStarted("s1", "toolu_bdrk_01WRONG__vscode-1");

        await Task.Delay(200);

        Assert.Contains(_events.Events, e =>
            e.EventType == BeaconEventType.Waiting
            && e.HookEvent == "TranscriptApprovalPending");
    }

    [Fact]
    public async Task ConfirmToolStarted_HookBeforeTranscript_ExecutionStartClearsBeforeTimeout()
    {
        _sm.HandleStateChange("s1", AgentSource.Copilot, HookAction.Clear, "Setup", "setup");

        _fileReader.SetFileContent(@"C:\t.jsonl", "");
        _watcher.SetTranscriptPath("s1", @"C:\t.jsonl");

        _watcher.ConfirmToolStarted("s1", "toolu_bdrk_01FAST__vscode-999");

        _fileReader.AppendContent(@"C:\t.jsonl",
            """  {"type":"assistant.message","data":{"toolRequests":[{"toolCallId":"toolu_bdrk_01FAST"}]}}""" + "\n");
        TriggerPoll();

        _fileReader.AppendContent(@"C:\t.jsonl",
            """  {"type":"tool.execution_start","data":{"toolCallId":"toolu_bdrk_01FAST"}}""" + "\n");
        TriggerPoll();

        await Task.Delay(200);

        Assert.DoesNotContain(_events.Events, e =>
            e.EventType == BeaconEventType.Waiting
            && e.HookEvent == "TranscriptApprovalPending");
    }

    [Fact]
    public async Task ConfirmToolStarted_ToolNeedsApproval_NoExecutionStart_WaitingFires()
    {
        _sm.HandleStateChange("s1", AgentSource.Copilot, HookAction.Clear, "Setup", "setup");

        _fileReader.SetFileContent(@"C:\t.jsonl", "");
        _watcher.SetTranscriptPath("s1", @"C:\t.jsonl");

        _fileReader.AppendContent(@"C:\t.jsonl",
            """  {"type":"assistant.message","data":{"toolRequests":[{"toolCallId":"toolu_bdrk_01PERM"}]}}""" + "\n");
        TriggerPoll();

        _watcher.ConfirmToolStarted("s1", "toolu_bdrk_01PERM__vscode-999");

        await Task.Delay(200);

        Assert.Contains(_events.Events, e =>
            e.EventType == BeaconEventType.Waiting
            && e.HookEvent == "TranscriptApprovalPending");
    }

    [Fact]
    public async Task ConfirmToolStarted_HookBeforeTranscript_BatchPartialPreConfirm_SiblingStillTimesOut()
    {
        _sm.HandleStateChange("s1", AgentSource.Copilot, HookAction.Clear, "Setup", "setup");

        _fileReader.SetFileContent(@"C:\t.jsonl", "");
        _watcher.SetTranscriptPath("s1", @"C:\t.jsonl");

        _watcher.ConfirmToolStarted("s1", "toolu_bdrk_01FAST__vscode-999");

        _fileReader.AppendContent(@"C:\t.jsonl",
            """{"type":"assistant.message","data":{"toolRequests":[{"toolCallId":"toolu_bdrk_01FAST"},{"toolCallId":"toolu_bdrk_01SLOW"}]}}""" + "\n");
        TriggerPoll();

        await Task.Delay(200);

        Assert.Contains(_events.Events, e =>
            e.EventType == BeaconEventType.Waiting
            && e.HookEvent == "TranscriptApprovalPending");
    }

    [Fact]
    public async Task NewAssistantMessage_ClearsStalePendingFromPreviousTurn()
    {
        _sm.HandleStateChange("s1", AgentSource.Copilot, HookAction.Clear, "Setup", "setup");

        _fileReader.SetFileContent(@"C:\t.jsonl", "");
        _watcher.SetTranscriptPath("s1", @"C:\t.jsonl");

        _fileReader.AppendContent(@"C:\t.jsonl",
            """{"type":"assistant.message","data":{"toolRequests":[{"toolCallId":"toolu_bdrk_01GHOST"},{"toolCallId":"toolu_bdrk_01GOOD"}]}}""" + "\n");
        TriggerPoll();

        _fileReader.AppendContent(@"C:\t.jsonl",
            """{"type":"tool.execution_start","data":{"toolCallId":"toolu_bdrk_01GOOD"}}""" + "\n");
        TriggerPoll();

        _fileReader.AppendContent(@"C:\t.jsonl",
            """{"type":"tool.execution_complete","data":{"toolCallId":"toolu_bdrk_01GOOD"}}""" + "\n"
            + """{"type":"assistant.message","data":{"toolRequests":[{"toolCallId":"toolu_bdrk_01NEXT"}]}}""" + "\n");
        TriggerPoll();

        _watcher.ConfirmToolStarted("s1", "toolu_bdrk_01NEXT__vscode-1");

        _fileReader.AppendContent(@"C:\t.jsonl",
            """{"type":"tool.execution_start","data":{"toolCallId":"toolu_bdrk_01NEXT"}}""" + "\n");
        TriggerPoll();

        await Task.Delay(200);

        Assert.DoesNotContain(_events.Events, e =>
            e.EventType == BeaconEventType.Waiting
            && e.HookEvent == "TranscriptApprovalPending");
    }

    private void TriggerPoll()
    {
        var method = typeof(CopilotTranscriptWatcher).GetMethod(
            "ReadNewLines",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        var sessionsField = typeof(CopilotTranscriptWatcher).GetField(
            "_sessions",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        if (sessionsField?.GetValue(_watcher) is not System.Collections.IDictionary sessions)
            return;

        foreach (var ts in sessions.Values.Cast<object>().ToList())
        {
            method?.Invoke(_watcher, [ts]);
        }
    }
}
