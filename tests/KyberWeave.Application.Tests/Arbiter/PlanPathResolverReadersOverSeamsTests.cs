using KyberWeave.Arbiter.Hooks;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Cli.Commands.Arbiter;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Plans;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Containment for the plan path an event header names, over an in-memory tree with links. Both plan readers resolve
/// the header through the resolver, so a link escape has to surface as <c>plan.exists: false</c> at each of them
/// rather than as a parse of a file the caller has no business reading.
/// </summary>
public sealed class PlanPathResolverReadersOverSeamsTests
{
    private static readonly string Base = ArbiterTestRoots.Under("planpaths");
    private static readonly string Repo = Path.Combine(Base, "repo");
    private static readonly string Outside = Path.Combine(Base, "outside");

    private readonly InMemoryFileSystem _files = new();
    private readonly InMemoryArbiterState _state;

    public PlanPathResolverReadersOverSeamsTests()
    {
        _state = new InMemoryArbiterState(_files);
        Add(Path.Combine(Repo, "docs", "plans", "plan.md"), "# Plan\n");
        Add(Path.Combine(Outside, "stolen.md"), "# Outside\n");
    }

    public static TheoryData<string> Readers => new() { "hook", "cli" };

    /// <summary>
    /// A checkout reached through a link above the repository root, as every macOS temporary directory is: resolving
    /// the root's own links is what keeps a plan resolvable, because comparing a resolved candidate against an
    /// unresolved root would reject every plan on the platform.
    /// </summary>
    [Fact]
    public void ARepositoryRootReachedThroughALinkStillResolves()
    {
        string alias = Path.Combine(Base, "alias");
        _files.CreateDirectoryLink(alias, Repo);
        PlanPathResolver resolver = new(_state.Paths, new FakeProcessEnvironment(currentDirectory: Base));

        string? resolved = resolver.Resolve(Path.Combine(alias, "docs", "plans", "plan.md"), alias);

        Assert.NotNull(resolved);
        Assert.True(_files.FileExists(resolved));
    }

    [Theory]
    [MemberData(nameof(Readers))]
    public void ALinkEscapeReadsAsAnAbsentPlanThroughBothReaders(string reader)
    {
        _files.CreateFileLink(Path.Combine(Repo, "docs", "plans", "linked.md"), Path.Combine(Outside, "stolen.md"));

        ArbiterFactSet facts = Enrich(Reader(reader), "docs/plans/linked.md");

        // plan.exists is asserted as false, which is a present fact holding false -- the shape an `exists: false`
        // rule matches on. No digest means the file was never read.
        Assert.True(facts.TryGet("plan.exists", out ArbiterFact? exists));
        Assert.False((bool)exists!.Value!);
        Assert.False(facts.TryGet("plan.digest", out _));
    }

    /// <summary>
    /// The same path read honestly: a real plan inside the repository is still <c>plan.exists: true</c>, so the escape
    /// case is a containment decision and not a reader that has stopped reading.
    /// </summary>
    [Theory]
    [MemberData(nameof(Readers))]
    public void ARealPlanInsideTheRepositoryStillReadsAsPresentThroughBothReaders(string reader)
    {
        ArbiterFactSet facts = Enrich(Reader(reader), "docs/plans/plan.md");

        Assert.True(facts.TryGet("plan.exists", out ArbiterFact? exists));
        Assert.True((bool)exists!.Value!);
        Assert.True(facts.IsPresent("plan.digest"));
    }

    private static ArbiterFactSet Enrich(IArbiterPlanReader planReader, string planFile) =>
        planReader.Enrich(
            new ArbiterFactSet(),
            new TriggerClassification(
                "delegate",
                "conductor",
                ArbiterCallerSources.Header,
                "dev-medium",
                "agent",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["PLAN_FILE"] = planFile,
                },
                Marker: null,
                PassedWithoutConfig: false));

    private IArbiterPlanReader Reader(string reader) =>
        reader == "hook"
            ? new HookPlanReader(_state.PlanFacts(), Repo)
            : new ArbiterCliPlanReader(_state.PlanFacts(), Repo);

    private void Add(string path, string content)
    {
        _files.CreateDirectory(Path.GetDirectoryName(path)!);
        _files.WriteAllText(path, content);
    }
}
