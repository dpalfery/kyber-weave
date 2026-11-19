using System.Text.Json;
using KyberWeave.Arbiter;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Arbiter.Hooks.Adapters;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Configuration;
using Xunit;
using KyberWeave.Tests.Corpus;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract for task 4.1: the Read guard (Req 25.2, Req 25.4). It applies only when the
/// caller names an implementation specialist, denies reads resolving inside the folders
/// of the three index properties, matches shell commands by substring (best effort),
/// and names Req 25 in the reason. The protected directories are derived from the
/// host configuration, mirroring the <c>config.planning-dirs</c> fact.
/// RED: the Arbiter project and the Read guard do not exist yet.
/// </summary>
public sealed class ArbiterReadGuardTests
{

    private static readonly IReadOnlyList<string> ProtectedDirs = ["docs/plans", "docs/specs", "docs/todo"];

    private sealed class AllowEngine : IHookDecisionEngine
    {
        public HookOutcome DecidePreDispatch(string caller, string? target, string prompt, KyberWeaveConfig config) =>
            new(HookOutcomeKind.Allow);

        public HookOutcome DecidePostDispatch(string caller, string? target, string toolOutput, KyberWeaveConfig config) =>
            new(HookOutcomeKind.Allow);
    }

    private static JsonElement ToolInput(string json)
    {
        JsonDocument doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// Run-9 shape: the payload cwd is the original checkout while the run works in its own worktree. A worker that
    /// reads the plan by its absolute path in the run worktree must be denied, because that is a planning path of the
    /// work the run is doing.
    /// </summary>
    [Fact]
    public void Guard_JudgesAWorkersAbsoluteReadInTheRunWorktree_AgainstTheRunContext()
    {
        const string payload =
            """
            {
              "hook_event_name": "PreToolUse",
              "tool_name": "Read",
              "tool_use_id": "toolu-read-1",
              "session_id": "session-1",
              "cwd": "/repo",
              "agent_type": "dev-medium",
              "tool_input": { "file_path": "/repo.worktrees/run/docs/plans/2026-11-18-run.md" }
            }
            """;
        HookContext context = new(
            "/repo",
            () => "decision-test-1",
            TextWriter.Null,
            RunContext: new HookRunContext(
                "/repo",
                "/repo/.git",
                ActiveRun: null,
                Worktree: "/repo.worktrees/run",
                Worktrees: ["/repo", "/repo.worktrees/run"],
                PlanFile: null,
                PlanPath: null,
                TaskFile: null,
                TaskFileExists: false,
                HookRunContextSource.EventWorktree,
                Notes: []));
        IHarnessHookAdapter adapter = new ClaudeHookAdapter(new AllowEngine());

        string output = adapter.Handle(ToolInput(payload), payload, "conductor", ReadGuardConfig(), context);

        Assert.Contains("\"permissionDecision\":\"deny\"", output, StringComparison.Ordinal);
        Assert.Contains("Req 25", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The runtime's Local schema judges every non-dispatch tool by its content, and resolves the same run worktree.
    /// </summary>
    [Fact]
    public void LocalGuard_JudgesAnAbsoluteReadInTheRunWorktree_AgainstTheRunContext()
    {
        const string payload =
            """
            {
              "hook_event_name": "PreToolUse",
              "tool_name": "Read",
              "cwd": "/repo",
              "tool_input": { "file_path": "/repo.worktrees/run/docs/specs/2026-11-18-spec/tasks.md" }
            }
            """;
        HookContext context = new(
            "/repo",
            () => "decision-test-1",
            TextWriter.Null,
            RunContext: new HookRunContext(
                "/repo",
                "/repo/.git",
                ActiveRun: null,
                Worktree: "/repo.worktrees/run",
                Worktrees: ["/repo", "/repo.worktrees/run"],
                PlanFile: null,
                PlanPath: null,
                TaskFile: null,
                TaskFileExists: false,
                HookRunContextSource.EventWorktree,
                Notes: []));
        IHarnessHookAdapter adapter = new CopilotHookAdapter(new AllowEngine(), CopilotHookAdapter.VsCodeToken);

        string output = adapter.Handle(ToolInput(payload), payload, null, ReadGuardConfig(), context);

        Assert.Contains("\"permissionDecision\":\"deny\"", output, StringComparison.Ordinal);
    }

    /// <summary>A path outside the repository and its worktrees is not a planning path of this run, and stays allowed.</summary>
    [Fact]
    public void Guard_LeavesAnAbsolutePathOutsideTheRepositoryAndItsWorktreesAlone()
    {
        ReadGuardResult result = ReadGuard.Check(
            "Read",
            ToolInput("""{"file_path": "/elsewhere/docs/plans/plan.md"}"""),
            "/repo.worktrees/run",
            ProtectedDirs,
            ["/repo", "/repo.worktrees/run"]);

        Assert.True(result.Allowed);
    }

    private static KyberWeaveConfig ReadGuardConfig() => new()
    {
        Ontology = OntologyConfig.ProductDefaults.WithDocsRoots(["docs"]),
        Arbiter = new ArbiterConfig { Enabled = true },
    };

    [Theory]
    [InlineData("dev-easy")]
    [InlineData("dev-medium")]
    [InlineData("devops")]
    public void IsImplementationSpecialist_NamesTheGuardedSpecialists(string caller)
    {
        Assert.True(ReadGuard.IsImplementationSpecialist(caller));
    }

    [Theory]
    [InlineData("conductor")]
    [InlineData("architect")]
    [InlineData("docs-dev")]
    [InlineData("research-agent")]
    [InlineData("code-reviewer")]
    [InlineData("unknown-agent")]
    [InlineData("")]
    public void IsImplementationSpecialist_ExcludesEveryoneElse(string caller)
    {
        // docs-dev keeps unguarded document access (Req 25.3): the guard keys on the
        // worker/publishing-worker capability profiles, never on a name carve-out.
        Assert.False(ReadGuard.IsImplementationSpecialist(caller));
    }

    [Fact]
    public void IsImplementationSpecialist_RejectsNull()
    {
        Assert.False(ReadGuard.IsImplementationSpecialist(null));
    }

    [Fact]
    public void ProtectedDirectories_FromDefaultConfig_CoversTheThreeIndexFolders()
    {
        IReadOnlyList<string> dirs = ReadGuard.ProtectedDirectories(new KyberWeaveConfig { Arbiter = ArbiterConfigDefaults.Product });

        Assert.Equal(["6-Docs/plans", "6-Docs/specs", "6-Docs/todo"], dirs);
    }

    [Fact]
    public void Check_ReadInsidePlans_DeniesAndNamesReq25()
    {
        ReadGuardResult result = ReadGuard.Check(
            "Read",
            ToolInput("""{"file_path": "/repo/docs/plans/plan.md"}"""),
            "/repo",
            ProtectedDirs);

        Assert.False(result.Allowed);
        Assert.Contains("Req 25", result.Reason ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("docs/plans", result.Reason ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_ReadOutsideProtectedDirs_Allows()
    {
        ReadGuardResult result = ReadGuard.Check(
            "Read",
            ToolInput("""{"file_path": "/repo/src/Foo.cs"}"""),
            "/repo",
            ProtectedDirs);

        Assert.True(result.Allowed);
    }

    [Fact]
    public void Check_RelativePath_ResolvesAgainstTheRepositoryRoot()
    {
        ReadGuardResult result = ReadGuard.Check(
            "Read",
            ToolInput("""{"file_path": "docs/todo/item.md"}"""),
            "/repo",
            ProtectedDirs);

        Assert.False(result.Allowed);
    }

    [Fact]
    public void Check_DotDotSegments_NormalizeBeforeMatching()
    {
        ReadGuardResult result = ReadGuard.Check(
            "Read",
            ToolInput("""{"file_path": "/repo/docs/plans/../specs/spec.md"}"""),
            "/repo",
            ProtectedDirs);

        Assert.False(result.Allowed);
    }

    [Fact]
    public void Check_AbsolutePathOutsideTheRoot_Allows()
    {
        ReadGuardResult result = ReadGuard.Check(
            "Read",
            ToolInput("""{"file_path": "/other/docs/plans/plan.md"}"""),
            "/repo",
            ProtectedDirs);

        Assert.True(result.Allowed);
    }

    [Fact]
    public void Check_GrepPathInsideSpecs_Denies()
    {
        ReadGuardResult result = ReadGuard.Check(
            "Grep",
            ToolInput("""{"pattern": "TASK", "path": "/repo/docs/specs/spec.md"}"""),
            "/repo",
            ProtectedDirs);

        Assert.False(result.Allowed);
        Assert.Contains("Req 25", result.Reason ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_GlobPathInsideTodo_Denies()
    {
        ReadGuardResult result = ReadGuard.Check(
            "Glob",
            ToolInput("""{"pattern": "*.md", "path": "/repo/docs/todo"}"""),
            "/repo",
            ProtectedDirs);

        Assert.False(result.Allowed);
    }

    [Fact]
    public void Check_GlobPatternCoveringPlans_Denies()
    {
        ReadGuardResult result = ReadGuard.Check(
            "Glob",
            ToolInput("""{"pattern": "docs/plans/*.md"}"""),
            "/repo",
            ProtectedDirs);

        Assert.False(result.Allowed);
        Assert.Contains("Req 25", result.Reason ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_BareGlobPattern_Allows()
    {
        ReadGuardResult result = ReadGuard.Check(
            "Glob",
            ToolInput("""{"pattern": "*.md"}"""),
            "/repo",
            ProtectedDirs);

        Assert.True(result.Allowed);
    }

    [Fact]
    public void Check_BashNamingAPlanningPath_DeniesBySubstring()
    {
        ReadGuardResult result = ReadGuard.Check(
            "Bash",
            ToolInput("""{"command": "cat docs/specs/spec.md"}"""),
            "/repo",
            ProtectedDirs);

        Assert.False(result.Allowed);
        Assert.Contains("Req 25", result.Reason ?? string.Empty, StringComparison.Ordinal);
    }

    [Theory]
    // The substring match ran against the raw text, so an equivalent path spelled with a
    // repeated separator or a "." segment never contained "docs/plans" and passed.
    [InlineData("cat docs//plans/plan.md")]
    [InlineData("cat docs/./plans/plan.md")]
    [InlineData("cat ./docs/plans/plan.md")]
    [InlineData("cat docs/specs/../plans/plan.md")]
    [InlineData("cat docs/plans\\plan.md")]
    public void Check_BashNamingAPlanningPathThroughAnEquivalentSpelling_Denies(string command)
    {
        ReadGuardResult result = ReadGuard.Check(
            "Bash",
            ToolInput(JsonSerializer.Serialize(new { command })),
            "/repo",
            ProtectedDirs);

        Assert.False(result.Allowed);
        Assert.Contains("Req 25", result.Reason ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_BashWithoutPlanningPath_Allows()
    {
        ReadGuardResult result = ReadGuard.Check(
            "Bash",
            ToolInput("""{"command": "dotnet build KyberWeave.sln -c Release"}"""),
            "/repo",
            ProtectedDirs);

        Assert.True(result.Allowed);
    }

    [Theory]
    // A search rooted above a protected directory covers it, so searching "." or "docs"
    // reaches the planning paths just as surely as naming one. Only the inward direction
    // was checked, so those searches were allowed.
    [InlineData("Grep", """{"path": ".", "pattern": "KW-ARB"}""")]
    [InlineData("Grep", """{"path": "docs", "pattern": "KW-ARB"}""")]
    [InlineData("Grep", """{"path": "./", "pattern": "KW-ARB"}""")]
    [InlineData("Glob", """{"path": "docs", "pattern": "plans/**"}""")]
    public void Check_SearchRootedAboveAProtectedDirectory_Denies(string tool, string input)
    {
        ReadGuardResult result = ReadGuard.Check(tool, ToolInput(input), "/repo", ProtectedDirs);

        Assert.False(result.Allowed);
        Assert.Contains("Req 25", result.Reason ?? string.Empty, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Grep", """{"path": "src", "pattern": "KW-ARB"}""")]
    [InlineData("Glob", """{"path": "src/KyberWeave.Core", "pattern": "*.cs"}""")]
    public void Check_SearchOutsideEveryProtectedDirectory_Allows(string tool, string input)
    {
        ReadGuardResult result = ReadGuard.Check(tool, ToolInput(input), "/repo", ProtectedDirs);

        Assert.True(result.Allowed);
    }

    [Fact]
    public void Check_WithoutProtectedDirs_AllowsEverything()
    {
        ReadGuardResult result = ReadGuard.Check(
            "Read",
            ToolInput("""{"file_path": "/repo/docs/plans/plan.md"}"""),
            "/repo",
            []);

        Assert.True(result.Allowed);
    }

    [Fact]
    public void Check_MissingValue_Allows()
    {
        ReadGuardResult result = ReadGuard.Check(
            "Read",
            ToolInput("""{"other": "x"}"""),
            "/repo",
            ProtectedDirs);

        Assert.True(result.Allowed);
    }

    [Fact]
    public void Guard_AppliesOnlyToSpecialistCallers_EndToEnd()
    {
        string stdin = EmbeddedFixtureText.Read(typeof(ArbiterReadGuardTests).Assembly, "arbiter-hooks/claude/pre-read-denied.json")
            .Replace("\"agent_type\": \"dev-medium\",", string.Empty, StringComparison.Ordinal);
        int loads = 0;
        KyberWeaveConfig config = new() { Arbiter = new ArbiterConfig { Enabled = true } };
        HookCommand command = ArbiterTestServices.Command(
            ArbiterTestServices.Registry(new AllowEngine()),
            root => { loads++; return config; },
            () => "decision-test-1");
        using StringWriter stdout = new();
        using StringWriter log = new();

        int exit = command.Run("claude", "research-agent", stdin, stdout, log);

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Equal(0, loads);
    }

    [Theory]
    [InlineData("kyber-weave docs packet T1")]
    [InlineData("kyber-weave docs packet t2 --plan docs/plans/2026-11-13-x.md")]
    [InlineData("kyber-weave docs packet 3 --plan docs/plans/2026-11-13-x.md --prior-results /tmp/results.txt")]
    public void Check_AllowsTheCommandThatPrintsAWorkersOwnPacket(string command)
    {
        ReadGuardResult result = ReadGuard.Check("Bash", ToolInput(JsonSerializer.Serialize(new { command })), "/repo", ProtectedDirs);

        Assert.True(result.Allowed, result.Reason);
    }

    [Theory]
    [InlineData("kyber-weave docs packet T1 --plan docs/plans/x.md; cat docs/plans/x.md")]
    [InlineData("kyber-weave docs packet T1 --plan docs/plans/x.md && cat docs/plans/x.md")]
    [InlineData("kyber-weave docs packet T1 --plan docs/plans/x.md | head")]
    [InlineData("kyber-weave docs packet T1 --plan $(cat docs/plans/x.md)")]
    [InlineData("kyber-weave docs packet T1\ncat docs/plans/secret.md")]
    [InlineData("kyber-weave docs packet T1\r\ncat docs/plans/secret.md")]
    [InlineData("kyber-weave docs packet T1 `cat docs/plans/x.md`")]
    [InlineData("kyber-weave docs packet T1 || cat docs/plans/x.md")]
    [InlineData("kyber-weave docs packet T1 > docs/plans/x.md")]
    [InlineData("$(cat docs/plans/x.md)/kyber-weave docs packet T1")]
    [InlineData("x;cat docs/plans/x.md;/kyber-weave docs packet T1")]
    [InlineData("cat docs/plans/x.md")]
    public void Check_StillDeniesAnythingElseThatNamesAPlan(string command)
    {
        ReadGuardResult result = ReadGuard.Check("Bash", ToolInput(JsonSerializer.Serialize(new { command })), "/repo", ProtectedDirs);

        Assert.False(result.Allowed);
    }
}
