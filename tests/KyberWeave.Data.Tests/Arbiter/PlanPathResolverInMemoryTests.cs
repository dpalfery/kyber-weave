using KyberWeave.Core.Arbiter.Plans;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>Plan-path containment over an in-memory tree, links included, with no disk.</summary>
public sealed class PlanPathResolverInMemoryTests
{
    private static readonly string Base = Path.GetFullPath(Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "planpaths"));
    private static readonly string Repo = Path.Combine(Base, "repo");
    private static readonly string Outside = Path.Combine(Base, "outside");

    private readonly InMemoryFileSystem _files = new();
    private readonly PlanPathResolver _resolver;

    public PlanPathResolverInMemoryTests()
    {
        _resolver = new PlanPathResolver(new FakePathResolver(_files), new FakeProcessEnvironment(currentDirectory: Base));
        Add(Path.Combine(Repo, "docs", "plans", "plan.md"));
        Add(Path.Combine(Outside, "secret.md"));
    }

    [Fact]
    public void ARelativePlanInsideTheRepositoryResolves() =>
        Assert.Equal(Path.Combine(Repo, "docs", "plans", "plan.md"), _resolver.Resolve("docs/plans/plan.md", Repo));

    [Fact]
    public void ARootedPlanInsideTheRepositoryResolves()
    {
        string plan = Path.Combine(Repo, "docs", "plans", "plan.md");

        Assert.Equal(plan, _resolver.Resolve(plan, Repo));
    }

    [Fact]
    public void ADetourThatStaysInsideResolves() =>
        Assert.Equal(Path.Combine(Repo, "docs", "plans", "plan.md"), _resolver.Resolve("docs/../docs/plans/plan.md", Repo));

    [Fact]
    public void ARootSpelledWithATrailingSeparatorRejectsEveryRelativePlan() =>
        Assert.Null(_resolver.Resolve("docs/plans/plan.md", Repo + Path.DirectorySeparatorChar));

    [Fact]
    public void ARootSpelledWithATrailingSeparatorAcceptsItselfSpelledTheSameWay() =>
        Assert.Equal(Repo + Path.DirectorySeparatorChar, _resolver.Resolve("./", Repo + Path.DirectorySeparatorChar));

    [Theory]
    [InlineData("../outside/secret.md")]
    [InlineData("docs/../../outside/secret.md")]
    public void APathThatClimbsOutOfTheRepositoryIsRejected(string hostile) =>
        Assert.Null(_resolver.Resolve(hostile, Repo));

    [Fact]
    public void ARootedPathOutsideTheRepositoryIsRejected() =>
        Assert.Null(_resolver.Resolve(Path.Combine(Outside, "secret.md"), Repo));

    [Fact]
    public void ASiblingThatSharesTheRepositoryNameAsAPrefixIsRejected()
    {
        string sibling = Path.Combine(Base, "repo-evil", "secret.md");
        Add(sibling);

        Assert.Null(_resolver.Resolve(sibling, Repo));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankPlanResolvesToNothing(string? blank) =>
        Assert.Null(_resolver.Resolve(blank, Repo));

    [Fact]
    public void ALinkedFileInsideTheRepositoryPointingOutsideIsRejected()
    {
        _files.CreateFileLink(Path.Combine(Repo, "docs", "plans", "linked.md"), Path.Combine(Outside, "secret.md"));

        Assert.Null(_resolver.Resolve("docs/plans/linked.md", Repo));
    }

    [Fact]
    public void ALinkedDirectoryInsideTheRepositoryPointingOutsideIsRejected()
    {
        _files.CreateDirectoryLink(Path.Combine(Repo, "docs", "linked"), Outside);

        Assert.Null(_resolver.Resolve("docs/linked/secret.md", Repo));
    }

    [Fact]
    public void ALinkThatStaysInsideTheRepositoryResolves()
    {
        _files.CreateFileLink(Path.Combine(Repo, "docs", "alias.md"), Path.Combine(Repo, "docs", "plans", "plan.md"));

        Assert.Equal(Path.Combine(Repo, "docs", "alias.md"), _resolver.Resolve("docs/alias.md", Repo));
    }

    private void Add(string path)
    {
        _files.CreateDirectory(Path.GetDirectoryName(path)!);
        _files.WriteAllText(path, "# Plan\n");
    }
}
