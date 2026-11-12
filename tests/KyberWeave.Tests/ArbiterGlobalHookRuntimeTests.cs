using System.Diagnostics;
using System.Text.Json.Nodes;
using KyberWeave.Arbiter;
using KyberWeave.Core.Squad.Deployment;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Global-scope hook runtime contract: the user-level hook evaluates under the project configuration
/// when no project Claude hook covers the dispatch, defers only on an explicit project opt-out or to a
/// claim, and home config decides when the project is unset. Malformed or disabled user config fails
/// closed with the home path, and project scope keeps the old behavior.
/// </summary>
public sealed class ArbiterGlobalHookRuntimeTests
{
    private const string Harness = "claude";
    private const string ToolUseId = "toolu-global-runtime-1";
    private const string UnmarkedPrompt = "Plan the parser.";
    private const string EvaluatingLog = "evaluating under the project configuration";
    private const string DeferredLog = "deferred-to-project";
    private const string NotTwiceLog = "not evaluated twice";
    private const string DispatchMatcher = "^(Agent|Task)$";

    private static readonly string ConductorSkill = Path.Combine(".claude", "skills", "conductor", "SKILL.md");

    [Fact]
    public async Task GlobalScope_WhenProjectEnablesArbiterWithoutClaudeHook_EvaluatesUnderProjectConfig()
    {
        // Before this change the user-level hook deferred to any project that enabled the Arbiter,
        // so a project with no project Claude hook was never gated. It now evaluates under the
        // project configuration and writes to the project's artifacts, as the project hook does.
        using ArbiterGlobalHookScenario scenario = ArbiterGlobalHookScenario.Create();
        string payload = ArbiterGlobalHookScenario.Dispatch(scenario.ProjectRoot, ToolUseId, null, "architect", UnmarkedPrompt);
        string twinPayload = ArbiterGlobalHookScenario.Dispatch(scenario.TwinRoot, ToolUseId, null, "architect", UnmarkedPrompt);

        HookRun twin = await ArbiterGlobalHookScenario.RunProject(twinPayload);
        HookRun global = await scenario.RunGlobal(payload);

        int twinLines = ArbiterGlobalHookScenario.DecisionLines(scenario.TwinRoot);
        Assert.True(twinLines > 0, "The project-scope twin must write at least one decision for an unmarked architect dispatch.");
        Assert.Equal(0, global.Exit);
        Assert.Contains(EvaluatingLog, global.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(DeferredLog, global.Stderr, StringComparison.Ordinal);
        Assert.Equal(
            ArbiterGlobalHookScenario.WithoutDecisionId(twin.Stdout),
            ArbiterGlobalHookScenario.WithoutDecisionId(global.Stdout));
        Assert.Equal(twinLines, ArbiterGlobalHookScenario.DecisionLines(scenario.ProjectRoot));
        Assert.False(Directory.Exists(Path.Combine(scenario.Home, ".kyber-weave", "arbiter", "projects")),
            "An evaluation under the project configuration must not write the user ledger under the home directory.");
    }

    [Fact]
    public async Task GlobalScope_WhenProjectConfigMissing_UsesUserHomeConfigAndWritesLedgerUnderHome()
    {
        string projectRoot = CreateTempDirectory();
        string home = CreateTempDirectory();
        WriteUserConfig(home, "arbiter:\n  enabled: true\n");

        using StringReader stdin = new(BuildPayload(projectRoot));
        using StringWriter stdout = new();
        using StringWriter stderr = new();

        int exit = await Composition.DispatchAsync(
            ["hook", "--harness", Harness, "--scope", "global", "--home", home],
            stdin,
            stdout,
            stderr);

        string rootKey = SquadPhysicalRootIdentity.KeyOf(projectRoot);
        string ledgerRoot = Path.Combine(home, ".kyber-weave", "arbiter", "projects", rootKey);

        Assert.Equal(0, exit);
        Assert.True(Directory.Exists(ledgerRoot),
            $"A global-scope evaluation without project config should write a project ledger under the home: {ledgerRoot}");
        Assert.Contains("global", stderr.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(projectRoot, ".kyber-weave", "arbiter")),
            "Global-scope evaluation must not write its ledger into the working tree.");
    }

    [Fact]
    public async Task GlobalScope_WhenUserConfigIsMissingOrMalformed_FailsClosedWithHomeConfigPath()
    {
        string projectRoot = CreateTempDirectory();
        string home = CreateTempDirectory();

        using StringReader stdin = new(BuildPayload(projectRoot));
        using StringWriter stdout = new();
        using StringWriter stderr = new();

        int exit = await Composition.DispatchAsync(
            ["hook", "--harness", Harness, "--scope", "global", "--home", home],
            stdin,
            stdout,
            stderr);

        string combined = stdout.ToString() + stderr.ToString();
        string userConfigPath = Path.Combine(home, ".kyber-weave", "kyber-weave.yml");

        Assert.Equal(0, exit);
        Assert.Contains("KW-ARB-HOOK-001", combined, StringComparison.Ordinal);
        Assert.Contains(userConfigPath, combined, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GlobalScope_WhenMainThreadArchitectDispatchIsUnmarked_DeniesAsTheProjectHookDoes()
    {
        // The unmarked architect dispatch is denied by KW-ARB-PLANNER-001 in project scope. The user-level
        // hook must give the same decision document and write the same number of decisions.
        using ArbiterGlobalHookScenario scenario = ArbiterGlobalHookScenario.Create();
        string payload = ArbiterGlobalHookScenario.Dispatch(scenario.ProjectRoot, ToolUseId, null, "architect", UnmarkedPrompt);
        string twinPayload = ArbiterGlobalHookScenario.Dispatch(scenario.TwinRoot, ToolUseId, null, "architect", UnmarkedPrompt);

        HookRun twin = await ArbiterGlobalHookScenario.RunProject(twinPayload);
        HookRun global = await scenario.RunGlobal(payload);

        int twinLines = ArbiterGlobalHookScenario.DecisionLines(scenario.TwinRoot);
        Assert.True(twinLines > 0, "The project-scope twin must write at least one decision for an unmarked architect dispatch.");
        Assert.Contains("KW-ARB-PLANNER-001", global.Stdout, StringComparison.Ordinal);
        Assert.Equal(
            ArbiterGlobalHookScenario.WithoutDecisionId(twin.Stdout),
            ArbiterGlobalHookScenario.WithoutDecisionId(global.Stdout));
        Assert.Equal(twinLines, ArbiterGlobalHookScenario.DecisionLines(scenario.ProjectRoot));
    }

    [Fact]
    public async Task GlobalScope_WhenMainThreadExploreDispatchIsUnmarked_PassesThroughAsTheProjectHookDoes()
    {
        using ArbiterGlobalHookScenario scenario = ArbiterGlobalHookScenario.Create();
        string payload = ArbiterGlobalHookScenario.Dispatch(scenario.ProjectRoot, ToolUseId, null, "Explore", "Map the repository.");
        string twinPayload = ArbiterGlobalHookScenario.Dispatch(scenario.TwinRoot, ToolUseId, null, "Explore", "Map the repository.");

        HookRun twin = await ArbiterGlobalHookScenario.RunProject(twinPayload);
        HookRun global = await scenario.RunGlobal(payload);

        Assert.Equal(0, global.Exit);
        Assert.True(string.IsNullOrWhiteSpace(global.Stdout),
            $"An unmarked Explore dispatch passes through silently. Output: {global.Stdout}");
        Assert.Equal(
            ArbiterGlobalHookScenario.WithoutDecisionId(twin.Stdout),
            ArbiterGlobalHookScenario.WithoutDecisionId(global.Stdout));
        Assert.Equal(
            ArbiterGlobalHookScenario.DecisionLines(scenario.TwinRoot),
            ArbiterGlobalHookScenario.DecisionLines(scenario.ProjectRoot));
    }

    [Fact]
    public async Task GlobalScope_WhenProjectOptsOutWithArbiterEnabledFalse_DefersNamingTheOptOut()
    {
        using ArbiterGlobalHookScenario scenario = ArbiterGlobalHookScenario.Create(projectEnabled: false);
        string payload = ArbiterGlobalHookScenario.Dispatch(scenario.ProjectRoot, ToolUseId, null, "architect", UnmarkedPrompt);

        HookRun global = await scenario.RunGlobal(payload);

        Assert.Equal(0, global.Exit);
        Assert.True(string.IsNullOrWhiteSpace(global.Stdout),
            $"An opted-out project must emit no decision document from the user-level hook. Output: {global.Stdout}");
        Assert.Contains(DeferredLog, global.Stderr, StringComparison.Ordinal);
        Assert.Contains("arbiter.enabled: false", global.Stderr, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(scenario.ProjectRoot, "artifacts", "arbiter")),
            "A project that opted out must not get an artifacts/arbiter folder from the user-level hook.");
    }

    [Fact]
    public async Task GlobalScope_WhenProjectSkillHookNeverFires_EvaluatesAfterTheClaimWindow()
    {
        // The project skill carries the dispatch hook, but no claim ever appears. The user-level hook
        // polls for the claim for the full window (1500 ms) and then evaluates, naming the candidate.
        using ArbiterGlobalHookScenario scenario = ArbiterGlobalHookScenario.Create();
        scenario.WriteProjectFile(ConductorSkill, ConductorSkillWithDispatchHook());
        string payload = ArbiterGlobalHookScenario.Dispatch(scenario.ProjectRoot, ToolUseId, null, "architect", UnmarkedPrompt);

        Stopwatch clock = Stopwatch.StartNew();
        HookRun global = await scenario.RunGlobal(payload);
        clock.Stop();

        Assert.Contains(EvaluatingLog, global.Stderr, StringComparison.Ordinal);
        Assert.Contains(ConductorSkill, global.Stderr, StringComparison.Ordinal);
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(1000),
            $"The user-level hook must wait for a claim from a candidate. Elapsed {clock.ElapsedMilliseconds} ms.");
        Assert.True(clock.Elapsed <= TimeSpan.FromMilliseconds(5000),
            $"The claim wait must stay bounded. Elapsed {clock.ElapsedMilliseconds} ms.");
    }

    [Fact]
    public async Task GlobalScope_WhenProjectAndUserHooksRunTogetherOnPreDispatch_EvaluatesOnce()
    {
        // Both arms see the same dispatch. The project arm runs with --caller conductor, as the skill's
        // own hook does, so the twin does too; the decision count is comparable only on that footing.
        using ArbiterGlobalHookScenario scenario = ArbiterGlobalHookScenario.Create();
        scenario.WriteProjectFile(ConductorSkill, ConductorSkillWithDispatchHook());
        string payload = ArbiterGlobalHookScenario.Dispatch(scenario.ProjectRoot, ToolUseId, null, "architect", UnmarkedPrompt);
        string twinPayload = ArbiterGlobalHookScenario.Dispatch(scenario.TwinRoot, ToolUseId, null, "architect", UnmarkedPrompt);

        HookRun twin = await ArbiterGlobalHookScenario.RunProject(twinPayload, "conductor");
        HookRun[] arms = await Task.WhenAll(
            ArbiterGlobalHookScenario.RunProject(payload, "conductor"),
            scenario.RunGlobal(payload));

        int twinLines = ArbiterGlobalHookScenario.DecisionLines(scenario.TwinRoot);
        Assert.True(twinLines > 0, "The project-scope twin must write at least one decision for an unmarked architect dispatch.");
        Assert.Equal(twinLines, ArbiterGlobalHookScenario.DecisionLines(scenario.ProjectRoot));
        string stderr = arms[0].Stderr + arms[1].Stderr;
        Assert.True(
            stderr.Contains(NotTwiceLog, StringComparison.Ordinal) || stderr.Contains(DeferredLog, StringComparison.Ordinal),
            $"One of the two concurrent hooks must say it did not evaluate a second time. Stderr: {stderr}");
        AssertClaimedBy(scenario.ProjectRoot, "pre", "project");
    }

    [Fact]
    public async Task GlobalScope_WhenProjectAndUserHooksRunTogetherOnPreAndPostDispatch_EvaluatesEachEventOnce()
    {
        // The PreToolUse and PostToolUse events are claimed separately. Each event runs as a concurrent
        // pair, in order, and the decision count must match the twin's sequential run of the same events.
        using ArbiterGlobalHookScenario scenario = ArbiterGlobalHookScenario.Create();
        scenario.WriteProjectFile(ConductorSkill, ConductorSkillWithDispatchHook());
        string pre = ArbiterGlobalHookScenario.Dispatch(scenario.ProjectRoot, ToolUseId, null, "architect", UnmarkedPrompt);
        string post = ArbiterGlobalHookScenario.PostDispatch(scenario.ProjectRoot, ToolUseId, null, "architect", UnmarkedPrompt);
        string twinPre = ArbiterGlobalHookScenario.Dispatch(scenario.TwinRoot, ToolUseId, null, "architect", UnmarkedPrompt);
        string twinPost = ArbiterGlobalHookScenario.PostDispatch(scenario.TwinRoot, ToolUseId, null, "architect", UnmarkedPrompt);

        await ArbiterGlobalHookScenario.RunProject(twinPre, "conductor");
        await ArbiterGlobalHookScenario.RunProject(twinPost, "conductor");
        int twinLines = ArbiterGlobalHookScenario.DecisionLines(scenario.TwinRoot);
        Assert.True(twinLines > 0, "The project-scope twin must write at least one decision across the pre and post events.");

        HookRun[] preArms = await Task.WhenAll(
            ArbiterGlobalHookScenario.RunProject(pre, "conductor"),
            scenario.RunGlobal(pre));
        HookRun[] postArms = await Task.WhenAll(
            ArbiterGlobalHookScenario.RunProject(post, "conductor"),
            scenario.RunGlobal(post));

        Assert.Equal(twinLines, ArbiterGlobalHookScenario.DecisionLines(scenario.ProjectRoot));
        string stderr = preArms[0].Stderr + preArms[1].Stderr + postArms[0].Stderr + postArms[1].Stderr;
        Assert.True(
            stderr.Contains(NotTwiceLog, StringComparison.Ordinal) || stderr.Contains(DeferredLog, StringComparison.Ordinal),
            $"The concurrent hooks must not both evaluate either event. Stderr: {stderr}");
        AssertClaimedBy(scenario.ProjectRoot, "pre", "project");
        AssertClaimedBy(scenario.ProjectRoot, "post", "project");
    }

    [Fact]
    public async Task GlobalScope_WhenProjectHookRunsFirstOnPreDispatch_UserHookDefersToItsClaim()
    {
        // Sequential: the project hook claims and evaluates first. The user-level hook then finds the
        // claim at once and defers, so the decision count matches the twin's single project run.
        using ArbiterGlobalHookScenario scenario = ArbiterGlobalHookScenario.Create();
        scenario.WriteProjectFile(ConductorSkill, ConductorSkillWithDispatchHook());
        string payload = ArbiterGlobalHookScenario.Dispatch(scenario.ProjectRoot, ToolUseId, null, "architect", UnmarkedPrompt);
        string twinPayload = ArbiterGlobalHookScenario.Dispatch(scenario.TwinRoot, ToolUseId, null, "architect", UnmarkedPrompt);

        await ArbiterGlobalHookScenario.RunProject(twinPayload, "conductor");
        int twinLines = ArbiterGlobalHookScenario.DecisionLines(scenario.TwinRoot);
        Assert.True(twinLines > 0, "The project-scope twin must write at least one decision for an unmarked architect dispatch.");

        await ArbiterGlobalHookScenario.RunProject(payload, "conductor");
        HookRun global = await scenario.RunGlobal(payload);

        Assert.Equal(twinLines, ArbiterGlobalHookScenario.DecisionLines(scenario.ProjectRoot));
        Assert.Contains(DeferredLog, global.Stderr, StringComparison.Ordinal);
        AssertClaimedBy(scenario.ProjectRoot, "pre", "project");
    }

    [Fact]
    public async Task ProjectScope_WhenRunTwiceOnOnePayload_EvaluatesEachTimeAndClaimsProject()
    {
        // An existing project claim does not stop the project hook, so two project runs each evaluate,
        // as they did before claims existed. The first run must still leave its project claim behind.
        using ArbiterGlobalHookScenario scenario = ArbiterGlobalHookScenario.Create();
        string payload = ArbiterGlobalHookScenario.Dispatch(scenario.ProjectRoot, ToolUseId, null, "architect", UnmarkedPrompt);
        string twinPayload = ArbiterGlobalHookScenario.Dispatch(scenario.TwinRoot, ToolUseId, null, "architect", UnmarkedPrompt);

        await ArbiterGlobalHookScenario.RunProject(twinPayload);
        int twinLines = ArbiterGlobalHookScenario.DecisionLines(scenario.TwinRoot);
        Assert.True(twinLines > 0, "The project-scope twin must write at least one decision for an unmarked architect dispatch.");

        await ArbiterGlobalHookScenario.RunProject(payload);
        await ArbiterGlobalHookScenario.RunProject(payload);

        Assert.Equal(twinLines * 2, ArbiterGlobalHookScenario.DecisionLines(scenario.ProjectRoot));
        AssertClaimedBy(scenario.ProjectRoot, "pre", "project");
    }

    [Fact]
    public async Task ProjectScope_WhenGlobalClaimAlreadyExists_AllowsSilentlyWithoutEvaluating()
    {
        // The user-level hook already evaluated this dispatch. The project hook must allow with no
        // decision document and must not write a second decision for it.
        using ArbiterGlobalHookScenario scenario = ArbiterGlobalHookScenario.Create();
        ArbiterGlobalHookScenario.WriteClaim(scenario.ProjectRoot, ToolUseId, "pre", "global");
        string payload = ArbiterGlobalHookScenario.Dispatch(scenario.ProjectRoot, ToolUseId, null, "architect", UnmarkedPrompt);

        HookRun project = await ArbiterGlobalHookScenario.RunProject(payload);

        Assert.Equal(0, project.Exit);
        Assert.True(string.IsNullOrWhiteSpace(project.Stdout),
            $"A dispatch already claimed by the global hook must be allowed silently. Output: {project.Stdout}");
        Assert.Contains(NotTwiceLog, project.Stderr, StringComparison.Ordinal);
        Assert.Equal(0, ArbiterGlobalHookScenario.DecisionLines(scenario.ProjectRoot));
    }

    [Fact]
    public async Task ProjectScope_WhenReadDispatchIsNotAClaimEvent_LeavesNoClaimsFolder()
    {
        // Only Agent and Task dispatch events claim. A Read passes through without creating claims.
        using ArbiterGlobalHookScenario scenario = ArbiterGlobalHookScenario.Create();
        string payload = ArbiterGlobalHookScenario.ReadPayload(scenario.ProjectRoot, "toolu-read-global-1");

        HookRun project = await ArbiterGlobalHookScenario.RunProject(payload);

        Assert.Equal(0, project.Exit);
        Assert.False(ArbiterGlobalHookScenario.ClaimsFolderExists(scenario.ProjectRoot),
            "A non-dispatch Read event must not create a claims folder.");
    }

    private static string ConductorSkillWithDispatchHook() =>
        ArbiterGlobalHookScenario.HookDocument(
            "conductor",
            ArbiterGlobalHookScenario.ConductorCommand,
            ("PreToolUse", DispatchMatcher),
            ("PostToolUse", DispatchMatcher));

    private static void AssertClaimedBy(string root, string phase, string source)
    {
        string path = ArbiterGlobalHookScenario.ClaimPath(root, ToolUseId, phase);
        Assert.True(File.Exists(path),
            $"The project-scope hook must claim the dispatch before evaluating: missing {path}.");
        Assert.Equal(source, File.ReadAllText(path).Trim());
    }

    private static string BuildPayload(string projectRoot)
    {
        string fixturePath = Path.Combine(
            KyberWeaveTestPaths.ToolRoot,
            "tests",
            "KyberWeave.Tests",
            "Fixtures",
            "arbiter-hooks",
            "claude",
            "pre-agent-dispatch.json");

        JsonNode root = JsonNode.Parse(File.ReadAllText(fixturePath))!.AsObject();
        root["cwd"] = projectRoot;
        root["transcript_path"] = Path.Combine(projectRoot, "transcript.jsonl");
        return root.ToJsonString();
    }

    private static void WriteUserConfig(string home, string yaml)
    {
        string configDir = Path.Combine(home, ".kyber-weave");
        Directory.CreateDirectory(configDir);
        File.WriteAllText(Path.Combine(configDir, "kyber-weave.yml"), yaml);
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "kw-arbiter-global-hook-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}

public sealed class ArbiterHookCommandTests
{
    [Fact]
    public async Task HookCommand_ProjectScopeWithoutProjectConfig_FailsClosed()
    {
        string projectRoot = CreateTempDirectory();
        string payload = BuildPayload(projectRoot);

        using StringReader stdin = new(payload);
        using StringWriter stdout = new();
        using StringWriter stderr = new();

        int exit = await Composition.DispatchAsync(
            ["hook", "--harness", "claude"],
            stdin,
            stdout,
            stderr);

        string combined = stdout.ToString() + stderr.ToString();

        Assert.Equal(0, exit);
        Assert.Contains("KW-ARB-HOOK-001", combined, StringComparison.Ordinal);
    }

    private static string BuildPayload(string projectRoot)
    {
        string fixturePath = Path.Combine(
            KyberWeaveTestPaths.ToolRoot,
            "tests",
            "KyberWeave.Tests",
            "Fixtures",
            "arbiter-hooks",
            "claude",
            "pre-agent-dispatch.json");

        JsonNode root = JsonNode.Parse(File.ReadAllText(fixturePath))!.AsObject();
        root["cwd"] = projectRoot;
        root["transcript_path"] = Path.Combine(projectRoot, "transcript.jsonl");
        return root.ToJsonString();
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "kw-arbiter-project-hook-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
