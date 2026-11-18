using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Which hook sources count as covering a dispatch when the user-level Claude hook decides whether
/// to evaluate: the project Claude hook, the agent file for the payload's agent type, and every
/// effective skill. A source that does not carry the dispatch hook under the event's own container
/// must not make the user-level hook wait or defer.
/// </summary>
public sealed class ArbiterGlobalHookCoverageTests
{
    private const string ToolUseId = "toolu-global-coverage-1";
    private const string DispatchMatcher = "^(Agent|Task)$";
    private const string ReadMatcher = "^(Read|Grep|Glob|Bash|PowerShell)$";

    private static readonly string AgentFile = Path.Combine(".claude", "agents", "csharp-dev.md");
    private static readonly string ConductorAgentFile = Path.Combine(".claude", "agents", "conductor.md");
    private static readonly string ConductorSkill = Path.Combine(".claude", "skills", "conductor", "SKILL.md");

    [Fact]
    public void GlobalScope_WhenAgentFileDispatchHookClaimedTheDispatch_DefersNamingTheAgentFile()
    {
        // The agent file's own PreToolUse dispatch hook is a candidate for csharp-dev dispatches.
        // A project claim already exists, so the user-level hook must defer and name that file
        // as the candidate that claimed it. Without the candidate it would not know to defer.
        using ArbiterGlobalHookScenario scenario = ArbiterGlobalHookScenario.Create();
        scenario.WriteProjectFile(
            AgentFile,
            ArbiterGlobalHookScenario.HookDocument(
                "csharp-dev",
                ArbiterGlobalHookScenario.CsharpDevCommand,
                ("PreToolUse", DispatchMatcher)));
        scenario.WriteClaim(ToolUseId, "pre", "project");
        string payload = ArbiterGlobalHookScenario.Dispatch(
            scenario.ProjectRoot, ToolUseId, "csharp-dev", "csharp-dev", "Implement the plan parser.");

        HookRun run = scenario.RunGlobal(payload);

        Assert.Equal(0, run.Exit);
        Assert.Contains(ArbiterGlobalHookScenario.DeferredLog, run.Stderr, StringComparison.Ordinal);
        Assert.Contains(AgentFile, run.Stderr, StringComparison.Ordinal);
        Assert.True(
            ArbiterGlobalHookScenario.DecisionLines(scenario.Project) == 0,
            "A deferred dispatch must not be evaluated by the user-level hook, so no decision may be written.");
    }

    [Fact]
    public void GlobalScope_WhenAgentFileHookMatchesOnlyReadTools_EvaluatesWithoutWaiting()
    {
        // The same agent file, but its only PreToolUse entry matches Read tools. It does not cover
        // Agent or Task, so it is not a candidate and the user-level hook evaluates at once.
        using ArbiterGlobalHookScenario scenario = ArbiterGlobalHookScenario.Create();
        scenario.WriteProjectFile(
            AgentFile,
            ArbiterGlobalHookScenario.HookDocument(
                "csharp-dev",
                ArbiterGlobalHookScenario.CsharpDevCommand,
                ("PreToolUse", ReadMatcher)));
        string payload = ArbiterGlobalHookScenario.Dispatch(
            scenario.ProjectRoot, ToolUseId, "csharp-dev", "csharp-dev", "Implement the plan parser.");

        HookRun run = scenario.RunGlobal(payload);

        // The log, not the clock: a hook that waited for a claim says the candidates did not fire, and
        // elapsed time under a loaded test run says nothing about whether it waited.
        Assert.Contains(ArbiterGlobalHookScenario.EvaluatingLog, run.Stderr, StringComparison.Ordinal);
        Assert.False(run.Stderr.Contains("candidate hooks did not fire", StringComparison.Ordinal),
            $"A non-candidate agent file must not make the user-level hook wait. {run.Stderr}");
    }

    [Fact]
    public void GlobalScope_WhenMainThreadDispatchHasOnlyAnAgentFileHook_EvaluatesWithoutWaiting()
    {
        // A main-thread dispatch has no agent type, so no agent file is effective even when
        // .claude/agents/conductor.md carries the dispatch hook. Nothing can claim it, so no wait.
        using ArbiterGlobalHookScenario scenario = ArbiterGlobalHookScenario.Create();
        scenario.WriteProjectFile(
            ConductorAgentFile,
            ArbiterGlobalHookScenario.HookDocument(
                "conductor",
                ArbiterGlobalHookScenario.ConductorCommand,
                ("PreToolUse", DispatchMatcher)));
        string payload = ArbiterGlobalHookScenario.Dispatch(
            scenario.ProjectRoot, ToolUseId, null, "architect", ArbiterGlobalHookScenario.UnmarkedPrompt);

        HookRun run = scenario.RunGlobal(payload);

        // The log, not the clock: a hook that waited for a claim says the candidates did not fire, and
        // elapsed time under a loaded test run says nothing about whether it waited.
        Assert.Contains(ArbiterGlobalHookScenario.EvaluatingLog, run.Stderr, StringComparison.Ordinal);
        Assert.False(run.Stderr.Contains("candidate hooks did not fire", StringComparison.Ordinal),
            $"An agent file that is not effective for a main-thread dispatch must not make the hook wait. {run.Stderr}");
    }

    [Fact]
    public void GlobalScope_WhenPersonalSkillShadowsProjectHookSkill_EvaluatesWithoutWaiting()
    {
        // The personal skill of the same name shadows the project skill, and it carries no hook.
        // The project skill's dispatch hook is therefore not effective, so nothing can claim.
        using ArbiterGlobalHookScenario scenario = ArbiterGlobalHookScenario.Create();
        scenario.WriteProjectFile(
            ConductorSkill,
            ArbiterGlobalHookScenario.HookDocument(
                "conductor",
                ArbiterGlobalHookScenario.ConductorCommand,
                ("PreToolUse", DispatchMatcher),
                ("PostToolUse", DispatchMatcher)));
        scenario.WriteHomeFile(ConductorSkill, ArbiterGlobalHookScenario.PlainSkillDocument("conductor"));
        string payload = ArbiterGlobalHookScenario.Dispatch(
            scenario.ProjectRoot, ToolUseId, null, "architect", ArbiterGlobalHookScenario.UnmarkedPrompt);

        HookRun run = scenario.RunGlobal(payload);

        // The log, not the clock: a hook that waited for a claim says the candidates did not fire, and
        // elapsed time under a loaded test run says nothing about whether it waited.
        Assert.Contains(ArbiterGlobalHookScenario.EvaluatingLog, run.Stderr, StringComparison.Ordinal);
        Assert.False(run.Stderr.Contains("candidate hooks did not fire", StringComparison.Ordinal),
            $"A project skill shadowed by a personal skill must not make the hook wait. {run.Stderr}");
    }

    [Theory]
    [InlineData("../../etc/x")]
    [InlineData("plugin:agent")]
    public void GlobalScope_WhenAgentTypeIsNotAValidAgentFileName_EvaluatesWithoutReadingAgentFile(string agentType)
    {
        // A traversal name would resolve to etc/x.md beside .claude. The decoy there carries the
        // dispatch hook, so reading it would defer. An invalid name must use no agent file at all.
        using ArbiterGlobalHookScenario scenario = ArbiterGlobalHookScenario.Create();
        string decoy = Path.Combine("etc", "x.md");
        scenario.WriteProjectFile(
            decoy,
            ArbiterGlobalHookScenario.HookDocument(
                "x",
                ArbiterGlobalHookScenario.CsharpDevCommand,
                ("PreToolUse", DispatchMatcher)));
        scenario.WriteHomeFile(
            decoy,
            ArbiterGlobalHookScenario.HookDocument(
                "x",
                ArbiterGlobalHookScenario.CsharpDevCommand,
                ("PreToolUse", DispatchMatcher)));
        string payload = ArbiterGlobalHookScenario.Dispatch(
            scenario.ProjectRoot, ToolUseId, agentType, "csharp-dev", "Implement the plan parser.");

        HookRun run = scenario.RunGlobal(payload);

        Assert.Equal(0, run.Exit);
        Assert.Contains(ArbiterGlobalHookScenario.EvaluatingLog, run.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(ArbiterGlobalHookScenario.DeferredLog, run.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(ArbiterGlobalHookScenario.FailClosedCode, run.Stdout + run.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void GlobalScope_WhenHarnessHasNoPerDispatchProbe_DefersNamingTheHarness()
    {
        // Cursor has no per-dispatch probe for the user-level hook. The project's own Cursor hooks
        // decide, so the user-level hook defers and names the harness, and allows nothing itself.
        using ArbiterGlobalHookScenario scenario = ArbiterGlobalHookScenario.Create();
        string payload = ArbiterGlobalHookScenario.CursorTaskDispatch(scenario.ProjectRoot);

        HookRun run = scenario.RunGlobal(payload, "cursor");

        Assert.Equal(0, run.Exit);
        Assert.True(string.IsNullOrWhiteSpace(run.Stdout),
            $"A deferred Cursor dispatch must emit no decision document. Output: {run.Stdout}");
        Assert.Contains(ArbiterGlobalHookScenario.DeferredLog, run.Stderr, StringComparison.Ordinal);
        Assert.Contains("cursor", run.Stderr, StringComparison.Ordinal);
    }
}
