using System.Text.Json;
using KyberWeave.Arbiter;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Configuration;
using Xunit;

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

    [Theory]
    [InlineData("csharp-dev")]
    [InlineData("test-dev")]
    [InlineData("github-devops")]
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
        IReadOnlyList<string> dirs = ReadGuard.ProtectedDirectories(new KyberWeaveConfig());

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
        string stdin = File.ReadAllText(Path.Combine(
            KyberWeaveTestPaths.ToolRoot, "tests", "KyberWeave.Tests",
            "Fixtures", "arbiter-hooks", "claude", "pre-read-denied.json"))
            .Replace("\"agent_type\": \"csharp-dev\",", string.Empty, StringComparison.Ordinal);
        int loads = 0;
        KyberWeaveConfig config = new() { Arbiter = new ArbiterConfig { Enabled = true } };
        HookCommand command = new(
            HarnessAdapterRegistry.CreateDefault(new AllowEngine()),
            root => { loads++; return config; },
            () => "decision-test-1");
        using StringWriter stdout = new();
        using StringWriter log = new();

        int exit = command.Run("claude", "research-agent", stdin, stdout, log);

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Equal(0, loads);
    }
}
