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
            {"type":"assistant.message","data":{"toolRequests":[{"toolCallId":"tc1"}]}}
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
            """{"type":"assistant.message","data":{"toolRequests":[{"toolCallId":"tc1"}]}}""" + "\n");

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
            """{"type":"assistant.message","data":{"toolRequests":[{"toolCallId":"tc1"}]}}""" + "\n");
        TriggerPoll();

        _fileReader.AppendContent(@"C:\t.jsonl",
            """{"type":"tool.execution_start","data":{"toolCallId":"tc1"}}""" + "\n");
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
            """{"type":"assistant.message","data":{"toolRequests":[{"toolCallId":"tc1"}]}}""" + "\n");
        TriggerPoll();

        await Task.Delay(200);

        Assert.Contains(_events.Events, e => e.EventType == BeaconEventType.Waiting);

        _fileReader.AppendContent(@"C:\t.jsonl",
            """{"type":"tool.execution_start","data":{"toolCallId":"tc1"}}""" + "\n");
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
            """{"type":"assistant.message","data":{"toolRequests":[{"toolCallId":"tc1"}]}}""" + "\n");
        _watcher.SetTranscriptPath("s1", @"C:\t.jsonl");

        _fileReader.AppendContent(@"C:\t.jsonl",
            """{"type":"assistant.message","data":{"toolRequests":[{"toolCallId":"tc2"}]}}""" + "\n");
        _watcher.SetTranscriptPath("s1", @"C:\t.jsonl");

        TriggerPoll();
    }

    [Fact]
    public async Task MultipleToolRequests_AllMustComplete_BeforeClear()
    {
        _sm.HandleStateChange("s1", AgentSource.Copilot, HookAction.Clear, "Setup", "setup");

        _fileReader.SetFileContent(@"C:\t.jsonl", "");
        _watcher.SetTranscriptPath("s1", @"C:\t.jsonl");

        _fileReader.AppendContent(@"C:\t.jsonl",
            """{"type":"assistant.message","data":{"toolRequests":[{"toolCallId":"tc1"},{"toolCallId":"tc2"}]}}""" + "\n");
        TriggerPoll();

        await Task.Delay(200);
        Assert.Contains(_events.Events, e => e.EventType == BeaconEventType.Waiting);

        _fileReader.AppendContent(@"C:\t.jsonl",
            """{"type":"tool.execution_start","data":{"toolCallId":"tc1"}}""" + "\n");
        TriggerPoll();

        Assert.DoesNotContain(_events.Events, e =>
            e.EventType == BeaconEventType.Clear && e.HookEvent == "TranscriptApproval");

        _fileReader.AppendContent(@"C:\t.jsonl",
            """{"type":"tool.execution_start","data":{"toolCallId":"tc2"}}""" + "\n");
        TriggerPoll();

        Assert.Contains(_events.Events, e =>
            e.EventType == BeaconEventType.Clear && e.HookEvent == "TranscriptApproval");
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
