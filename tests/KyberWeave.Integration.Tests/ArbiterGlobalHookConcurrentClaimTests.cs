using System.Text.Json.Nodes;
using KyberWeave.Arbiter;
using KyberWeave.Tests.Corpus;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The project hook and the user-level hook started at the same moment on the real file system: exactly one
/// of them wins the atomic claim, so the dispatch is evaluated once. The in-memory file system has no race, so this
/// is the only place the atomic move is proved; every sequential case is a unit test in
/// <c>ArbiterGlobalHookRuntimeTests</c>.
/// </summary>
public sealed class ArbiterGlobalHookConcurrentClaimTests
{
    private const string ToolUseId = "toolu-global-runtime-1";

    private static readonly string ConductorSkill = Path.Combine(".claude", "skills", "conductor", "SKILL.md");

    [Fact]
    public async Task GlobalScope_WhenProjectAndUserHooksRunTogetherOnPreDispatch_EvaluatesOnce()
    {
        // Both arms see the same dispatch. The project arm runs with --caller conductor, as the skill's
        // own hook does, so the twin does too; the decision count is comparable only on that footing.
        using ArbiterGlobalHookScenario scenario = ArbiterGlobalHookScenario.Create();
        scenario.WriteProjectFile(ConductorSkill, ConductorSkillWithDispatchHook());
        string payload = ArbiterGlobalHookScenario.Dispatch(scenario.ProjectRoot, ToolUseId, null, "architect", ArbiterGlobalHookScenario.UnmarkedPrompt);
        string twinPayload = ArbiterGlobalHookScenario.Dispatch(scenario.TwinRoot, ToolUseId, null, "architect", ArbiterGlobalHookScenario.UnmarkedPrompt);

        HookRun twin = await ArbiterGlobalHookScenario.RunProject(twinPayload, "conductor");
        HookRun[] arms = await Task.WhenAll(
            ArbiterGlobalHookScenario.RunProject(payload, "conductor"),
            scenario.RunGlobal(payload));

        int twinLines = ArbiterGlobalHookScenario.DecisionLines(scenario.TwinRoot);
        Assert.Equal(0, twin.Exit);
        Assert.True(twinLines > 0, "The project-scope twin must write at least one decision for an unmarked architect dispatch.");
        Assert.Equal(twinLines, ArbiterGlobalHookScenario.DecisionLines(scenario.ProjectRoot));
        string stderr = arms[0].Stderr + arms[1].Stderr;
        Assert.True(
            stderr.Contains(ArbiterGlobalHookScenario.NotTwiceLog, StringComparison.Ordinal)
            || stderr.Contains(ArbiterGlobalHookScenario.DeferredLog, StringComparison.Ordinal),
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
        string pre = ArbiterGlobalHookScenario.Dispatch(scenario.ProjectRoot, ToolUseId, null, "architect", ArbiterGlobalHookScenario.UnmarkedPrompt);
        string post = ArbiterGlobalHookScenario.PostDispatch(scenario.ProjectRoot, ToolUseId, null, "architect", ArbiterGlobalHookScenario.UnmarkedPrompt);
        string twinPre = ArbiterGlobalHookScenario.Dispatch(scenario.TwinRoot, ToolUseId, null, "architect", ArbiterGlobalHookScenario.UnmarkedPrompt);
        string twinPost = ArbiterGlobalHookScenario.PostDispatch(scenario.TwinRoot, ToolUseId, null, "architect", ArbiterGlobalHookScenario.UnmarkedPrompt);

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
            stderr.Contains(ArbiterGlobalHookScenario.NotTwiceLog, StringComparison.Ordinal)
            || stderr.Contains(ArbiterGlobalHookScenario.DeferredLog, StringComparison.Ordinal),
            $"The concurrent hooks must not both evaluate either event. Stderr: {stderr}");
        AssertClaimedBy(scenario.ProjectRoot, "pre", "project");
        AssertClaimedBy(scenario.ProjectRoot, "post", "project");
    }

    private static string ConductorSkillWithDispatchHook() =>
        ArbiterGlobalHookScenario.HookDocument(
            "conductor",
            ArbiterGlobalHookScenario.ConductorCommand,
            ("PreToolUse", ArbiterGlobalHookScenario.DispatchMatcher),
            ("PostToolUse", ArbiterGlobalHookScenario.DispatchMatcher));

    private static void AssertClaimedBy(string root, string phase, string source)
    {
        string path = ArbiterGlobalHookScenario.ClaimPath(root, ToolUseId, phase);
        Assert.True(File.Exists(path),
            $"The project-scope hook must claim the dispatch before evaluating: missing {path}.");
        Assert.Equal(source, File.ReadAllText(path).Trim());
    }
}

/// <summary>Smoke test of the real hook host: no project configuration at project scope fails closed.</summary>
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
        const string PayloadFixture = "tests/KyberWeave.Application.Tests/Fixtures/arbiter-hooks/claude/pre-agent-dispatch.json";

        JsonNode root = JsonNode.Parse(CheckoutCorpusSource.Checkout.ReadText(PayloadFixture))!.AsObject();
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
