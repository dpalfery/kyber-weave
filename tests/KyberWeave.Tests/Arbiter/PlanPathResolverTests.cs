using KyberWeave.Core.Arbiter.Plans;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Containment for the plan path an event header names (G1). <c>PLAN_FILE</c> is
/// caller-controlled and is read off disk, so a rooted path or a <c>..</c> segment must
/// not resolve outside the repository.
/// </summary>
public sealed class PlanPathResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kw-planpath-" + Guid.NewGuid().ToString("N"));

    public PlanPathResolverTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "docs", "plans"));
        File.WriteAllText(Path.Combine(_root, "docs", "plans", "plan.md"), "# Plan\n");
        File.WriteAllText(Path.Combine(_root, "secret.md"), "# Secret\n");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void ARelativePlanPathResolvesInsideTheRepository()
    {
        string? resolved = PlanPathResolver.Resolve("docs/plans/plan.md", _root);

        Assert.NotNull(resolved);
        Assert.True(File.Exists(resolved));
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("/private/etc/passwd")]
    [InlineData("../x")]
    [InlineData("docs/../../../x")]
    [InlineData("../../../../../../../../etc/passwd")]
    public void APathOutsideTheRepositoryIsRejected(string hostile)
    {
        Assert.Null(PlanPathResolver.Resolve(hostile, _root));
    }

    [Theory]
    // ".." that stays inside the repository is not an escape; only the resolved full
    // path decides, so a plan reached through a detour is still the same file.
    [InlineData("docs/plans/../../secret.md")]
    [InlineData("./docs/../secret.md")]
    public void ADetourThatStaysInsideTheRepositoryResolves(string relative)
    {
        string? resolved = PlanPathResolver.Resolve(relative, _root);

        Assert.NotNull(resolved);
        Assert.Equal(Path.GetFullPath(Path.Combine(_root, "secret.md")), resolved);
    }

    [Fact]
    public void ARootedPathInsideTheRepositoryIsAccepted()
    {
        string? resolved = PlanPathResolver.Resolve(Path.Combine(_root, "docs", "plans", "plan.md"), _root);

        Assert.NotNull(resolved);
    }

    [Fact]
    public void ASiblingDirectoryWithTheRepositoryAsAPrefixIsRejected()
    {
        // "/tmp/repo-evil" starts with "/tmp/repo"; only the separator tells them apart.
        string sibling = _root + "-evil";
        Directory.CreateDirectory(sibling);
        try
        {
            Assert.Null(PlanPathResolver.Resolve(Path.Combine(sibling, "secret.md"), _root));
        }
        finally
        {
            Directory.Delete(sibling, recursive: true);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyPlanFileResolvesToNothing(string? empty)
    {
        Assert.Null(PlanPathResolver.Resolve(empty, _root));
    }
}
