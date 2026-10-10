using KyberWeave.Arbiter.Hooks;
using KyberWeave.Cli.Commands.Arbiter;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Plans;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Containment for the plan path an event header names (G1). <c>PLAN_FILE</c> is
/// caller-controlled and is read off disk, so a rooted path, a <c>..</c> segment or a
/// symlink must not resolve outside the repository. The last two cases are proved
/// through the resolver and then through both plan readers, because a resolver the
/// readers do not consult would leave the header just as caller-controlled.
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

    /// <summary>
    /// Creates a symlink, or skips where the host will not make one. Creating a
    /// Windows symlink needs a privilege or developer mode, and the guarantee under
    /// test is about link resolution rather than about privilege, so an unarrangeable
    /// case is skipped rather than failed.
    /// </summary>
    private static FileSystemInfo Link(string path, string target, bool directory)
    {
        try
        {
            return directory
                ? Directory.CreateSymbolicLink(path, target)
                : File.CreateSymbolicLink(path, target);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            Assert.Skip($"This host does not permit creating symbolic links ({exception.Message}).");
            throw;
        }
    }

    /// <summary>A directory beside the repository root, deleted when the test ends.</summary>
    private string Outside(params string[] segments)
    {
        string path = _root + "-outside";
        foreach (string segment in segments)
        {
            path = Path.Combine(path, segment);
        }

        if (File.Exists(path))
        {
            File.Delete(path);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "# Outside\n");
        return path;
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

    /// <summary>
    /// A symlink is the escape <c>Path.GetFullPath</c> does not close: it collapses
    /// <c>..</c> but never resolves a link, so a repository-internal link pointing
    /// outside satisfies the lexical check and its target is read.
    /// </summary>
    [Fact]
    public void ASymlinkInsideTheRepositoryPointingOutsideIsRejected()
    {
        string outside = Outside("stolen.md");
        string link = Path.Combine(_root, "docs", "plans", "linked.md");
        try
        {
            Link(link, outside, directory: false);

            Assert.Null(PlanPathResolver.Resolve("docs/plans/linked.md", _root));
        }
        finally
        {
            if (File.Exists(link))
            {
                File.Delete(link);
            }

            if (Directory.Exists(_root + "-outside"))
            {
                Directory.Delete(_root + "-outside", recursive: true);
            }
        }
    }

    /// <summary>
    /// A symlinked <em>directory</em> inside the repository escapes the same way, and
    /// the name that escapes can be several components below the root.
    /// </summary>
    [Fact]
    public void ASymlinkedDirectoryInsideTheRepositoryPointingOutsideIsRejected()
    {
        string outsideDirectory = _root + "-outside-dir";
        Directory.CreateDirectory(outsideDirectory);
        File.WriteAllText(Path.Combine(outsideDirectory, "stolen.md"), "# Outside\n");
        string link = Path.Combine(_root, "docs", "linked");
        try
        {
            Link(link, outsideDirectory, directory: true);

            Assert.Null(PlanPathResolver.Resolve("docs/linked/stolen.md", _root));
        }
        finally
        {
            if (Directory.Exists(_root + "-outside-dir"))
            {
                Directory.Delete(_root + "-outside-dir", recursive: true);
            }
        }
    }

    /// <summary>
    /// The legitimate case the symlink check must not break: a link whose target is
    /// inside the repository still resolves. Rejecting every link would trade an escape
    /// for an outage.
    /// </summary>
    [Fact]
    public void ASymlinkPointingInsideTheRepositoryStillResolves()
    {
        string link = Path.Combine(_root, "docs", "plans", "alias.md");
        try
        {
            Link(link, Path.Combine(_root, "docs", "plans", "plan.md"), directory: false);

            string? resolved = PlanPathResolver.Resolve("docs/plans/alias.md", _root);

            Assert.NotNull(resolved);
            Assert.True(File.Exists(resolved));
        }
        finally
        {
            if (File.Exists(link))
            {
                File.Delete(link);
            }
        }
    }

    /// <summary>
    /// macOS hands out <c>/var/folders/...</c> paths and <c>/var</c> is a symlink to
    /// <c>/private/var</c>, so every macOS checkout reaches a plan through links above
    /// the repository root. Resolving the root's own links is what keeps this working;
    /// comparing a resolved candidate against an unresolved root would reject every
    /// plan on the platform.
    /// </summary>
    [Fact]
    public void ARepositoryRootReachedThroughASymlinkStillResolves()
    {
        string alias = _root + "-alias";
        Link(alias, _root, directory: true);
        try
        {
            string? resolved = PlanPathResolver.Resolve(
                Path.Combine(alias, "docs", "plans", "plan.md"), alias);

            Assert.NotNull(resolved);
            Assert.True(File.Exists(resolved));
        }
        finally
        {
            if (Directory.Exists(alias))
            {
                Directory.Delete(alias);
            }
        }
    }

    /// <summary>
    /// Both readers resolve the header through <see cref="PlanPathResolver"/>, so a
    /// symlink escape has to surface as <c>plan.exists: false</c> at each of them
    /// rather than as a parse of a file the caller has no business reading.
    /// </summary>
    public static TheoryData<string> Readers => new() { "hook", "cli" };

    private static TriggerClassification ClassificationNaming(string planFile) =>
        new(
            "delegate",
            "conductor",
            ArbiterCallerSources.Header,
            "csharp-dev",
            "agent",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["PLAN_FILE"] = planFile,
            },
            Marker: null,
            PassedWithoutConfig: false);

    private static ArbiterFactSet Enrich(string reader, IArbiterPlanReader planReader, string planFile) =>
        planReader.Enrich(new ArbiterFactSet(), ClassificationNaming(planFile));

    private static IArbiterPlanReader Reader(string reader, string repositoryRoot) =>
        reader == "hook"
            ? new HookPlanReader(repositoryRoot)
            : new ArbiterCommandComposition.ArbiterCliPlanReader(repositoryRoot);

    [Theory]
    [MemberData(nameof(Readers))]
    public void ASymlinkEscapeReadsAsAnAbsentPlanThroughBothReaders(string reader)
    {
        string outside = Outside("stolen.md");
        string link = Path.Combine(_root, "docs", "plans", "linked.md");
        try
        {
            Link(link, outside, directory: false);

            ArbiterFactSet facts = Enrich(reader, Reader(reader, _root), "docs/plans/linked.md");

            // plan.exists is asserted as false, which is a present fact holding false --
            // the shape an `exists: false` rule matches on. No digest means the file was
            // never read.
            Assert.True(facts.TryGet("plan.exists", out ArbiterFact? exists));
            Assert.False((bool)exists!.Value!);
            Assert.False(facts.TryGet("plan.digest", out _));
        }
        finally
        {
            if (File.Exists(link))
            {
                File.Delete(link);
            }

            if (Directory.Exists(_root + "-outside"))
            {
                Directory.Delete(_root + "-outside", recursive: true);
            }
        }
    }

    /// <summary>
    /// The same path read honestly: a real plan inside the repository is still
    /// <c>plan.exists: true</c>, so the escape case is a containment decision and not a
    /// reader that has stopped reading.
    /// </summary>
    [Theory]
    [MemberData(nameof(Readers))]
    public void ARealPlanInsideTheRepositoryStillReadsAsPresentThroughBothReaders(string reader)
    {
        ArbiterFactSet facts = Enrich(reader, Reader(reader, _root), "docs/plans/plan.md");

        Assert.True(facts.TryGet("plan.exists", out ArbiterFact? exists));
        Assert.True((bool)exists!.Value!);
        Assert.True(facts.IsPresent("plan.digest"));
    }
}
