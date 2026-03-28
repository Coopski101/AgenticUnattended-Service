using AgenticUnattended.Events;
using AgenticUnattended.Sessions;

namespace AgenticUnattended.Tests;

public sealed class SessionRegistryTests
{
    [Fact]
    public void RegisterOrUpdate_NewSession_ReturnsIsNewTrue()
    {
        var registry = new SessionRegistry();

        var (session, isNew, displaced) = registry.RegisterOrUpdate("s1", 100, AgentSource.Copilot);

        Assert.True(isNew);
        Assert.Null(displaced);
        Assert.Equal("s1", session.SessionId);
        Assert.Equal((nint)100, session.WindowHandle);
        Assert.Equal(AgentSource.Copilot, session.Source);
    }

    [Fact]
    public void RegisterOrUpdate_ExistingSession_ReturnsIsNewFalse()
    {
        var registry = new SessionRegistry();
        registry.RegisterOrUpdate("s1", 100, AgentSource.Copilot);

        var (session, isNew, displaced) = registry.RegisterOrUpdate("s1", 100, AgentSource.ClaudeCode);

        Assert.False(isNew);
        Assert.Null(displaced);
        Assert.Equal(AgentSource.ClaudeCode, session.Source);
    }

    [Fact]
    public void RegisterOrUpdate_SameHwnd_DisplacesOldSession()
    {
        var registry = new SessionRegistry();
        registry.RegisterOrUpdate("old", 100, AgentSource.Copilot);

        var (session, isNew, displaced) = registry.RegisterOrUpdate("new", 100, AgentSource.Copilot);

        Assert.True(isNew);
        Assert.Equal("old", displaced);
        Assert.Equal("new", session.SessionId);
    }

    [Fact]
    public void TryGetSession_ReturnsNullForUnknown()
    {
        var registry = new SessionRegistry();

        Assert.Null(registry.TryGetSession("nope"));
    }

    [Fact]
    public void TryGetSessionByHwnd_FindsSession()
    {
        var registry = new SessionRegistry();
        registry.RegisterOrUpdate("s1", 42, AgentSource.Copilot);

        var session = registry.TryGetSessionByHwnd(42);

        Assert.NotNull(session);
        Assert.Equal("s1", session.SessionId);
    }

    [Fact]
    public void TryGetSessionByHwnd_ReturnsNullForUnknownHwnd()
    {
        var registry = new SessionRegistry();

        Assert.Null(registry.TryGetSessionByHwnd(999));
    }

    [Fact]
    public void GetAllSessions_ReturnsAll()
    {
        var registry = new SessionRegistry();
        registry.RegisterOrUpdate("s1", 1, AgentSource.Copilot);
        registry.RegisterOrUpdate("s2", 2, AgentSource.ClaudeCode);

        var all = registry.GetAllSessions();

        Assert.Equal(2, all.Count);
    }

    [Fact]
    public void EndSession_RemovesSession()
    {
        var registry = new SessionRegistry();
        registry.RegisterOrUpdate("s1", 100, AgentSource.Copilot);

        registry.EndSession("s1");

        Assert.Null(registry.TryGetSession("s1"));
        Assert.Null(registry.TryGetSessionByHwnd(100));
    }

    [Fact]
    public void ReassociateWindow_UpdatesHwndMapping()
    {
        var registry = new SessionRegistry();
        var (session, _, _) = registry.RegisterOrUpdate("s1", 0, AgentSource.Copilot);

        registry.ReassociateWindow(session, 200);

        Assert.Equal((nint)200, session.WindowHandle);
        Assert.Equal(session, registry.TryGetSessionByHwnd(200));
    }

    [Fact]
    public void ReassociateWindow_CleansUpOldHwnd()
    {
        var registry = new SessionRegistry();
        var (session, _, _) = registry.RegisterOrUpdate("s1", 100, AgentSource.Copilot);

        registry.ReassociateWindow(session, 200);

        Assert.Null(registry.TryGetSessionByHwnd(100));
        Assert.Equal(session, registry.TryGetSessionByHwnd(200));
    }

    [Fact]
    public void FindOrphanedSession_ReturnsSessionWithZeroHwnd()
    {
        var registry = new SessionRegistry();
        registry.RegisterOrUpdate("s1", 0, AgentSource.Copilot);

        var orphan = registry.FindOrphanedSession();

        Assert.NotNull(orphan);
        Assert.Equal("s1", orphan.SessionId);
    }

    [Fact]
    public void FindOrphanedSession_ReturnsNullIfNoneOrphaned()
    {
        var registry = new SessionRegistry();
        registry.RegisterOrUpdate("s1", 100, AgentSource.Copilot);

        Assert.Null(registry.FindOrphanedSession());
    }

    [Fact]
    public void RemoveDeadSessions_RemovesDeadWindows()
    {
        var registry = new SessionRegistry();
        registry.RegisterOrUpdate("alive", 1, AgentSource.Copilot);
        registry.RegisterOrUpdate("dead", 2, AgentSource.Copilot);

        var dead = registry.RemoveDeadSessions(hwnd => hwnd == (nint)1);

        Assert.Single(dead);
        Assert.Equal("dead", dead[0].SessionId);
        Assert.Null(registry.TryGetSession("dead"));
        Assert.NotNull(registry.TryGetSession("alive"));
    }

    [Fact]
    public void RemoveDeadSessions_SkipsZeroHwnd()
    {
        var registry = new SessionRegistry();
        registry.RegisterOrUpdate("orphan", 0, AgentSource.Copilot);

        var dead = registry.RemoveDeadSessions(_ => false);

        Assert.Empty(dead);
        Assert.NotNull(registry.TryGetSession("orphan"));
    }
}
