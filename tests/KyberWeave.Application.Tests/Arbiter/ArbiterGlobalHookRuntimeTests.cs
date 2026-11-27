using System.Text.Json;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Configuration;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Global-scope hook runtime contract: the user-level hook evaluates under the project configuration
/// when no project Claude hook covers the dispatch, defers only on an explicit project opt-out or to a
/// claim, and home config decides when the project is unset. Malformed or disabled user config fails
/// closed with the home path, and project scope keeps the old behavior. The two cases that run the
/// project and user hooks at the same moment need the real file system's atomic claim, so they stay
/// in <c>ArbiterGlobalHookConcurrentClaimTests</c> in the integration project.
/// </summary>
public sealed class ArbiterGlobalHookRuntimeTests
{
    private const string ToolUseId = "toolu-global-runtime-1";
    private const string UnmarkedPrompt = "Plan the parser.";
    private const string EvaluatingLog = ArbiterGlobalHookScenario.EvaluatingLog;
    private const string DeferredLog = ArbiterGlobalHookScenario.DeferredLog;
    private const string NotTwiceLog = ArbiterGlobalHookScenario.NotTwiceLog;
    private const string DispatchMatcher = "^(Agent|Task)$";
    private const string ShellRuleId = "KW-ARB-SHELL-001";

    private static readonly string ConductorSkill = Path.Combine(".claude", "skills", "conductor", "SKILL.md");

    [Fact]
    public void GlobalScope_WhenProjectEnablesArbiterWithoutClaudeHook_EvaluatesUnderProjectConfig()
    {
        // Before this change the user-level hook deferred to any project that enabled the Arbiter,
        // so a project with no project Claude hook was never gated. It now evaluates under the
        // project configuration and writes to the project's artifacts, as the project hook does.
        using ArbiterGlobalHookScenario scenario = ArbiterGlobalHookScenario.Create();
        string payload = ArbiterGlobalHookScenario.Dispatch(scenario.ProjectRoot, ToolUseId, null, "architect", UnmarkedPrompt);
        string twinPayload = ArbiterGlobalHookScenario.Dispatch(scenario.TwinRoot, ToolUseId, null, "architect", UnmarkedPrompt);

        HookRun twin = scenario.RunTwin(twinPayload);
        HookRun global = scenario.RunGlobal(payload);

        int twinLines = ArbiterGlobalHookScenario.DecisionLines(scenario.Twin);
        Assert.True(twinLines > 0, "The project-scope twin must write at least one decision for an unmarked architect dispatch.");
        Assert.Equal(0, global.Exit);
        Assert.Contains(EvaluatingLog, global.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(DeferredLog, global.Stderr, StringComparison.Ordinal);
        Assert.Equal(
            ArbiterGlobalHookScenario.WithoutDecisionId(twin.Stdout),
            ArbiterGlobalHookScenario.WithoutDecisionId(global.Stdout));
        Assert.Equal(twinLines, ArbiterGlobalHookScenario.DecisionLines(scenario.Project));
        Assert.False(
            ArbiterGlobalHookScenario.RecordedUnder(scenario.Project, Path.Combine(scenario.Home, ".kyber-weave", "arbiter", "projects")),
            "An evaluation under the project configuration must not write the user ledger under the home directory.");
    }

    [Fact]
    public void GlobalScope_WhenProjectConfigMissing_UsesUserHomeConfigAndWritesLedgerUnderHome()
    {
        using InMemoryArbiterHost host = InMemoryArbiterHost.Create();
        string projectRoot = host.RepoRoot;
        _ = host.Write(UserHostConfigPath.Of(host.HomeDirectory), "arbiter:\n  enabled: true\n");

        HookRun run = ArbiterGlobalHookScenario.Run(
            host,
            ArbiterGlobalHookScenario.Harness,
            null,
            ArbiterGlobalHookScenario.Fixture(projectRoot, "claude/pre-agent-dispatch.json", _ => { }),
            HookScope.Global,
            host.HomeDirectory);

        string rootKey = ArbiterStateDirectory.ProjectRootKey(projectRoot, host.WorkingDirectory);
        string ledgerRoot = Path.Combine(host.HomeDirectory, ".kyber-weave", "arbiter", "projects", rootKey);

        Assert.Equal(0, run.Exit);
        Assert.True(ArbiterGlobalHookScenario.RecordedUnder(host, ledgerRoot),
            $"A global-scope evaluation without project config should write a project ledger under the home: {ledgerRoot}");
        Assert.Contains("global", run.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.False(
            ArbiterGlobalHookScenario.RecordedUnder(host, Path.Combine(projectRoot, ".kyber-weave", "arbiter")),
            "Global-scope evaluation must not write its ledger into the working tree.");
    }

    [Fact]
    public void GlobalScope_WhenUserConfigIsMissingOrMalformed_FailsClosedWithHomeConfigPath()
    {
        using InMemoryArbiterHost host = InMemoryArbiterHost.Create();

        HookRun run = ArbiterGlobalHookScenario.Run(
            host,
            ArbiterGlobalHookScenario.Harness,
            null,
            ArbiterGlobalHookScenario.Fixture(host.RepoRoot, "claude/pre-agent-dispatch.json", _ => { }),
            HookScope.Global,
            host.HomeDirectory);

        string combined = run.Stdout + run.Stderr;
        string userConfigPath = Path.Combine(host.HomeDirectory, ".kyber-weave", "kyber-weave.yml");

        Assert.Equal(0, run.Exit);
        Assert.Contains("KW-ARB-HOOK-001", combined, StringComparison.Ordinal);
        Assert.Contains(userConfigPath, combined, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GlobalScope_WhenMainThreadArchitectDispatchIsUnmarked_DecidesAsTheProjectHookDoes()
    {
        // The unmarked architect dispatch is judged by KW-ARB-PLANNER-001 in project scope: with no model it is
        // undecidable and proceeds (ADR 0072). The user-level hook must give the same decision document and
        // write the same number of decisions.
        using ArbiterGlobalHookScenario scenario = ArbiterGlobalHookScenario.Create();
        string payload = ArbiterGlobalHookScenario.Dispatch(scenario.ProjectRoot, ToolUseId, null, "architect", UnmarkedPrompt);
        string twinPayload = ArbiterGlobalHookScenario.Dispatch(scenario.TwinRoot, ToolUseId, null, "architect", UnmarkedPrompt);

        HookRun twin = scenario.RunTwin(twinPayload);
        HookRun global = scenario.RunGlobal(payload);

        int twinLines = ArbiterGlobalHookScenario.DecisionLines(scenario.Twin);
        Assert.True(twinLines > 0, "The project-scope twin must write at least one decision for an unmarked architect dispatch.");
        Assert.DoesNotContain("\"deny\"", global.Stdout, StringComparison.Ordinal);
        Assert.Equal(
            ArbiterGlobalHookScenario.WithoutDecisionId(twin.Stdout),
            ArbiterGlobalHookScenario.WithoutDecisionId(global.Stdout));
        Assert.Equal(twinLines, ArbiterGlobalHookScenario.DecisionLines(scenario.Project));
    }

    [Fact]
    public void GlobalScope_WhenMainThreadExploreDispatchIsUnmarked_PassesThroughAsTheProjectHookDoes()
    {
        using ArbiterGlobalHookScenario scenario = ArbiterGlobalHookScenario.Create();
        string payload = ArbiterGlobalHookScenario.Dispatch(scenario.ProjectRoot, ToolUseId, null, "Explore", "Map the repository.");
        string twinPayload = ArbiterGlobalHookScenario.Dispatch(scenario.TwinRoot, ToolUseId, null, "Explore", "Map the repository.");

        HookRun twin = scenario.RunTwin(twinPayload);
        HookRun global = scenario.RunGlobal(payload);

        Assert.Equal(0, global.Exit);
        Assert.True(string.IsNullOrWhiteSpace(global.Stdout),
            $"An unmarked Explore dispatch passes through silently. Output: {global.Stdout}");
        Assert.Equal(
            ArbiterGlobalHookScenario.WithoutDecisionId(twin.Stdout),
            ArbiterGlobalHookScenario.WithoutDecisionId(global.Stdout));
        Assert.Equal(
            ArbiterGlobalHookScenario.DecisionLines(scenario.Twin),
            ArbiterGlobalHookScenario.DecisionLines(scenario.Project));
    }

    [Fact]
    public void GlobalScope_WhenProjectOptsOutWithArbiterEnabledFalse_DefersNamingTheOptOut()
    {
        using ArbiterGlobalHookScenario scenario = ArbiterGlobalHookScenario.Create(projectEnabled: false);
        string payload = ArbiterGlobalHookScenario.Dispatch(scenario.ProjectRoot, ToolUseId, null, "architect", UnmarkedPrompt);

        HookRun global = scenario.RunGlobal(payload);

        Assert.Equal(0, global.Exit);
        Assert.True(string.IsNullOrWhiteSpace(global.Stdout),
            $"An opted-out project must emit no decision document from the user-level hook. Output: {global.Stdout}");
        Assert.Contains(DeferredLog, global.Stderr, StringComparison.Ordinal);
        Assert.Contains("arbiter.enabled: false", global.Stderr, StringComparison.Ordinal);
        Assert.False(
            ArbiterGlobalHookScenario.RecordedUnder(scenario.Project, Path.Combine(scenario.ProjectRoot, "artifacts", "arbiter")),
            "A project that opted out must not get an artifacts/arbiter folder from the user-level hook.");
    }

    [Fact]
    public void GlobalScope_WhenProjectSkillHookNeverFires_EvaluatesAfterTheClaimWindow()
    {
        // The project skill carries the dispatch hook, but no claim ever appears. The user-level hook
        // polls for the claim for the full window (1500 ms) and then evaluates, naming the candidate.
        // The wait runs on a fake clock that moves only while the hook is parked in a delay.
        ParkObservingTimeProvider clock = new();
        using ArbiterGlobalHookScenario scenario = ArbiterGlobalHookScenario.Create(clock: clock);
        scenario.WriteProjectFile(ConductorSkill, ConductorSkillWithDispatchHook());
        string payload = ArbiterGlobalHookScenario.Dispatch(scenario.ProjectRoot, ToolUseId, null, "architect", UnmarkedPrompt);

        DateTimeOffset started = clock.GetUtcNow();
        HookRun global = clock.RunAdvancingOnParks(
            () => scenario.RunGlobal(payload),
            TimeSpan.FromMilliseconds(50),
            int.MaxValue,
            atPark: null);
        TimeSpan waited = clock.GetUtcNow() - started;

        Assert.Contains(EvaluatingLog, global.Stderr, StringComparison.Ordinal);
        Assert.Contains(ConductorSkill, global.Stderr, StringComparison.Ordinal);
        Assert.True(waited >= TimeSpan.FromMilliseconds(1000),
            $"The user-level hook must wait for a claim from a candidate. Waited {waited.TotalMilliseconds} ms.");
        Assert.True(waited <= TimeSpan.FromMilliseconds(5000),
            $"The claim wait must stay bounded. Waited {waited.TotalMilliseconds} ms.");
    }

    [Fact]
    public void GlobalScope_WhenProjectHookRunsFirstOnPreDispatch_UserHookDefersToItsClaim()
    {
        // Sequential: the project hook claims and evaluates first. The user-level hook then finds the
        // claim at once and defers, so the decision count matches the twin's single project run.
        using ArbiterGlobalHookScenario scenario = ArbiterGlobalHookScenario.Create();
        scenario.WriteProjectFile(ConductorSkill, ConductorSkillWithDispatchHook());
        string payload = ArbiterGlobalHookScenario.Dispatch(scenario.ProjectRoot, ToolUseId, null, "architect", UnmarkedPrompt);
        string twinPayload = ArbiterGlobalHookScenario.Dispatch(scenario.TwinRoot, ToolUseId, null, "architect", UnmarkedPrompt);

        _ = scenario.RunTwin(twinPayload, "conductor");
        int twinLines = ArbiterGlobalHookScenario.DecisionLines(scenario.Twin);
        Assert.True(twinLines > 0, "The project-scope twin must write at least one decision for an unmarked architect dispatch.");

        _ = scenario.RunProject(payload, "conductor");
        HookRun global = scenario.RunGlobal(payload);

        Assert.Equal(twinLines, ArbiterGlobalHookScenario.DecisionLines(scenario.Project));
        Assert.Contains(DeferredLog, global.Stderr, StringComparison.Ordinal);
        AssertClaimedBy(scenario, "pre", "project");
    }

    [Fact]
    public void ProjectScope_WhenRunTwiceOnOnePayload_EvaluatesEachTimeAndClaimsProject()
    {
        // An existing project claim does not stop the project hook, so two project runs each evaluate,
        // as they did before claims existed. The first run must still leave its project claim behind.
        using ArbiterGlobalHookScenario scenario = ArbiterGlobalHookScenario.Create();
        string payload = ArbiterGlobalHookScenario.Dispatch(scenario.ProjectRoot, ToolUseId, null, "architect", UnmarkedPrompt);
        string twinPayload = ArbiterGlobalHookScenario.Dispatch(scenario.TwinRoot, ToolUseId, null, "architect", UnmarkedPrompt);

        _ = scenario.RunTwin(twinPayload);
        int twinLines = ArbiterGlobalHookScenario.DecisionLines(scenario.Twin);
        Assert.True(twinLines > 0, "The project-scope twin must write at least one decision for an unmarked architect dispatch.");

        _ = scenario.RunProject(payload);
        _ = scenario.RunProject(payload);

        Assert.Equal(twinLines * 2, ArbiterGlobalHookScenario.DecisionLines(scenario.Project));
        AssertClaimedBy(scenario, "pre", "project");
    }

    [Fact]
    public void ProjectScope_WhenGlobalClaimAlreadyExists_AllowsSilentlyWithoutEvaluating()
    {
        // The user-level hook already evaluated this dispatch. The project hook must allow with no
        // decision document and must not write a second decision for it.
        using ArbiterGlobalHookScenario scenario = ArbiterGlobalHookScenario.Create();
        scenario.WriteClaim(ToolUseId, "pre", "global");
        string payload = ArbiterGlobalHookScenario.Dispatch(scenario.ProjectRoot, ToolUseId, null, "architect", UnmarkedPrompt);

        HookRun project = scenario.RunProject(payload);

        Assert.Equal(0, project.Exit);
        Assert.True(string.IsNullOrWhiteSpace(project.Stdout),
            $"A dispatch already claimed by the global hook must be allowed silently. Output: {project.Stdout}");
        Assert.Contains(NotTwiceLog, project.Stderr, StringComparison.Ordinal);
        Assert.Equal(0, ArbiterGlobalHookScenario.DecisionLines(scenario.Project));
    }

    [Fact]
    public void ProjectScope_WhenReadDispatchIsNotAClaimEvent_LeavesNoClaimsFolder()
    {
        // Only Agent and Task dispatch events claim. A Read passes through without creating claims.
        using ArbiterGlobalHookScenario scenario = ArbiterGlobalHookScenario.Create();
        string payload = ArbiterGlobalHookScenario.ReadPayload(scenario.ProjectRoot, "toolu-read-global-1");

        HookRun project = scenario.RunProject(payload);

        Assert.Equal(0, project.Exit);
        Assert.False(scenario.ClaimsFolderExists(),
            "A non-dispatch Read event must not create a claims folder.");
    }

    // Shell guard (KW-ARB-SHELL-001) at global scope: the user-level hook evaluates an architect Bash call
    // against the same allowlist as the project hook, under the same configuration rules.

    [Fact]
    public void GlobalScope_ArchitectBashOutsideTheList_DeniesUnderTheProjectConfig_WithoutAClaim()
    {
        using ArbiterGlobalHookScenario scenario = ArbiterGlobalHookScenario.Create();

        HookRun global = scenario.RunGlobal(ArbiterGlobalHookScenario.ShellPayload(scenario.ProjectRoot, "grep x | sed y"));

        Assert.Equal(0, global.Exit);
        Assert.Contains(ShellRuleId, global.Stdout, StringComparison.Ordinal);
        Assert.Equal("deny", DenyDecision(global.Stdout));
        Assert.Empty(scenario.ClaimFiles());
    }

    [Fact]
    public void GlobalScope_ArchitectBashOutsideTheList_DeniesUnderTheUserConfigWhenTheProjectHasNone()
    {
        using InMemoryArbiterHost host = InMemoryArbiterHost.Create();
        _ = host.Write(UserHostConfigPath.Of(host.HomeDirectory), "arbiter:\n  enabled: true\n");

        HookRun run = ArbiterGlobalHookScenario.Run(
            host,
            ArbiterGlobalHookScenario.Harness,
            null,
            ArbiterGlobalHookScenario.ShellPayload(host.RepoRoot, "grep x | sed y"),
            HookScope.Global,
            host.HomeDirectory);

        Assert.Equal(0, run.Exit);
        Assert.Contains(ShellRuleId, run.Stdout, StringComparison.Ordinal);
        Assert.Equal("deny", DenyDecision(run.Stdout));
    }

    [Fact]
    public void GlobalScope_ArchitectBashOutsideTheList_AllowsWithoutEvaluationWhenTheProjectOptsOut()
    {
        using ArbiterGlobalHookScenario scenario = ArbiterGlobalHookScenario.Create(projectEnabled: false);

        HookRun global = scenario.RunGlobal(ArbiterGlobalHookScenario.ShellPayload(scenario.ProjectRoot, "grep x | sed y"));

        Assert.Equal(0, global.Exit);
        Assert.True(string.IsNullOrWhiteSpace(global.Stdout),
            $"An opted-out project must get no decision from the user-level hook. Output: {global.Stdout}");
        Assert.DoesNotContain(EvaluatingLog, global.Stderr, StringComparison.Ordinal);
        Assert.Equal(0, ArbiterGlobalHookScenario.DecisionLines(scenario.Project));
    }

    private static string DenyDecision(string stdout)
    {
        using JsonDocument doc = JsonDocument.Parse(stdout);
        return doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("permissionDecision").GetString()
            ?? string.Empty;
    }

    private static string ConductorSkillWithDispatchHook() =>
        ArbiterGlobalHookScenario.HookDocument(
            "conductor",
            ArbiterGlobalHookScenario.ConductorCommand,
            ("PreToolUse", DispatchMatcher),
            ("PostToolUse", DispatchMatcher));

    private static void AssertClaimedBy(ArbiterGlobalHookScenario scenario, string phase, string source)
    {
        Assert.True(scenario.ClaimExists(ToolUseId, phase),
            $"The project-scope hook must claim the dispatch before evaluating: missing {phase} claim.");
        Assert.Equal(source, scenario.ClaimOwner(ToolUseId, phase));
    }
}
