using System.Security.Cryptography;
using System.Text;
using KyberWeave.Application.Arbiter;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Arbiter.Mcp;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.RunStart;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Arbiter;

/// <summary>
/// The Arbiter's path decisions over the injected seams: a relative path is resolved against the current directory the
/// test names, never the one the test process happens to run in, and each guard keeps its inside-versus-outside answer.
/// </summary>
public sealed class ArbiterHookPathResolutionTests
{
    private const string Current = "/kw/work";

    private readonly InMemoryFileSystem _files = new();
    private readonly FakeProcessEnvironment _environment = new(currentDirectory: Current);

    [Fact]
    public void ProjectRootKey_ARelativeRootResolvesAgainstTheGivenBase_AndADifferentBaseGivesADifferentKey()
    {
        string expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("/kw/work/repo")));

        Assert.Equal(expected, ArbiterHookDecisionEngine.ProjectRootKey("repo", Current));
        Assert.NotEqual(expected, ArbiterHookDecisionEngine.ProjectRootKey("repo", "/kw/elsewhere"));
    }

    [Fact]
    public void ProjectRootKey_ATrailingSeparatorDoesNotChangeTheKey()
    {
        Assert.Equal(
            ArbiterHookDecisionEngine.ProjectRootKey("/kw/work/repo", Current),
            ArbiterHookDecisionEngine.ProjectRootKey("/kw/work/repo/", Current));
    }

    [Fact]
    public void DirectoryPrefix_ARelativeDirectoryResolvesAgainstTheGivenBase_WithOneTrailingSlash()
    {
        Assert.Equal("/kw/work/repo/", ConductorGuardRules.DirectoryPrefix("repo", Current));
        Assert.Equal("/kw/work/repo/", ConductorGuardRules.DirectoryPrefix("/kw/work/repo///", Current));
    }

    [Fact]
    public void Provenance_NormalisesAFullyQualifiedRoot_AndRefusesARelativeOne()
    {
        string line = ArbiterProvenance.Line("/kw/work/../repo", "abc", 3);

        Assert.Equal("provenance: root=/kw/repo rules=abc rule-count=3", line);
        Assert.Throws<ArgumentException>(() => ArbiterProvenance.Line("repo", "abc", 3));
    }

    [Fact]
    public void PlanService_ResolvesTheRelativePlanAgainstTheRoot_AndReportsAMissingOne()
    {
        _files.CreateDirectory("/kw/repo/docs/plans");
        _files.WriteAllText("/kw/repo/docs/plans/plan.md", "# Plan\n");
        ArbiterPlanService service = new(_files, new FakePathResolver(_files));

        DiagnosticReport found = new();
        ArbiterPlanRead? read = service.TryRead("/kw/repo", "docs/plans/plan.md", "plan.md", found);
        DiagnosticReport missing = new();
        ArbiterPlanRead? none = service.TryRead("/kw/repo", "docs/plans/other.md", "other.md", missing);

        Assert.NotNull(read);
        Assert.Equal("/kw/repo/docs/plans/plan.md", read.ResolvedPath);
        Assert.Null(none);
        Assert.Contains(missing.Items, d => d.Code == IArbiterPlanService.PlanNotReadable);
    }

    [Fact]
    public void PlanService_RefusesARelativeRoot_InsteadOfReadingTheWorkingDirectory()
    {
        ArbiterPlanService service = new(_files, new FakePathResolver(_files));

        Assert.Throws<ArgumentException>(() => service.TryRead("repo", "plan.md", "plan.md", new DiagnosticReport()));
    }

    [Fact]
    public void ConductorGuard_AllowsAContractFileInsideTheWorkingDirectory_AndDeniesOneOutsideIt()
    {
        string inside = Run("/kw/repo", "/kw/repo/AGENTS.md");
        string outside = Run("/kw/repo", "/kw/other/AGENTS.md");

        Assert.Equal(string.Empty, inside);
        Assert.Contains("deny", outside, StringComparison.OrdinalIgnoreCase);
    }

    private string Run(string cwd, string viewed)
    {
        ConductorGuard guard = new(
            new FakeGitRepositoryFacts(),
            new NoRunStates(),
            new NoHostConfig(),
            new NoLinks(),
            _environment);
        string stdin = System.Text.Json.JsonSerializer.Serialize(new
        {
            toolName = "view",
            toolArgs = new { path = viewed },
            cwd,
        });
        using StringWriter stdout = new();

        Assert.Equal(0, guard.Run(stdin, stdout, TextWriter.Null, copilotHome: "/kw/copilot", homeDirectory: "/kw/home"));
        return stdout.ToString();
    }

    private sealed class NoLinks : IFileLinkProbe
    {
        public bool HasLinkBelow(string root, string fullPath) => false;
    }

    private sealed class NoHostConfig : IHostConfigLoader
    {
        public HostConfigLoadResult TryLoad(string repoRoot) => new(false, null, null, "no configuration in a unit test");
    }

    private sealed class NoRunStates : IRunStateStore
    {
        public void Write(string homeDirectory, string branch, string baseRef, RunStartPlace? place = null)
        {
        }

        public bool TryReadBaseRef(string homeDirectory, string branch, out string? baseRef)
        {
            baseRef = null;
            return false;
        }

        public bool? HasLiveRun(string homeDirectory, string repository) => null;

        public IReadOnlyList<RunRecord>? ReadRecords(string homeDirectory) => null;
    }
}
