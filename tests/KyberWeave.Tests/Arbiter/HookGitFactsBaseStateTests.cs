using System.Text;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.RunStart;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// The review's changed paths are measured from the base the run recorded, not guessed:
/// <c>run start</c> stores its base branch per branch, and the dispatch hook reads it back
/// so a stacked branch is judged against its parent slice rather than <c>origin/HEAD</c>.
/// </summary>
/// <remarks>
/// The repository is built through <see cref="RunStartProbe" />, the suite's one allow-listed
/// driver of a real git repository: the stacked case needs a parent branch and a feature
/// branch off it, which that fixture supplies with a real origin.
/// </remarks>
public sealed class HookGitFactsBaseStateTests : IDisposable
{
    private const string ParentBranch = "slice/parent";
    private const string FeatureBranch = "slice/feature";
    private const string ParentFile = "src/Parent.cs";
    private const string FeatureFile = "src/Feature.cs";

    private readonly RunStartProbe _probe = StackedBranches();
    private readonly TempDirectory _home = new();

    public void Dispose()
    {
        _probe.Dispose();
        _home.Dispose();
    }

    /// <summary>
    /// A parent slice branch and a feature branch off it, each with one commit: the stacked
    /// shape the default base misjudges.
    /// </summary>
    private static RunStartProbe StackedBranches()
    {
        RunStartProbe probe = RunStartProbe.Create();
        probe.Git("switch", "-c", ParentBranch, "origin/main");
        Write(probe, ParentFile, "// parent slice change\n");
        probe.Git("add", "-A");
        probe.Git("commit", "-m", "feat: parent slice change");

        probe.Git("switch", "-c", FeatureBranch, ParentBranch);
        Write(probe, FeatureFile, "// feature change\n");
        probe.Git("add", "-A");
        probe.Git("commit", "-m", "feat: feature change");
        return probe;
    }

    private static void Write(RunStartProbe probe, string relative, string text)
    {
        string path = Path.Combine(probe.Root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, Encoding.UTF8);
    }

    /// <summary>
    /// With the run's base recorded, the review holds only the feature branch's own change:
    /// the parent slice's commits are not judged again.
    /// </summary>
    [Fact]
    public void AStoredBaseMeasuresTheReviewFromThatBase()
    {
        RunBaseState.Write(_home.Path, FeatureBranch, ParentBranch);

        IReadOnlyList<string> paths = ChangedPaths();

        Assert.Contains(FeatureFile, paths);
        Assert.DoesNotContain(ParentFile, paths);
    }

    /// <summary>
    /// With no recorded run the hook keeps its old default: the review is measured from
    /// <c>origin/HEAD</c> and holds both slices' changes.
    /// </summary>
    [Fact]
    public void WithoutAStoredBaseTheReviewFallsBackToOriginHead()
    {
        IReadOnlyList<string> paths = ChangedPaths();

        Assert.Contains(FeatureFile, paths);
        Assert.Contains(ParentFile, paths);
    }

    /// <summary>
    /// The review records which base it measured from, so a fallback is inspectable instead
    /// of silent: the stored base when recorded, the default otherwise.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheReviewRecordsWhichBaseItMeasuredFrom(bool stored)
    {
        if (stored)
        {
            RunBaseState.Write(_home.Path, FeatureBranch, ParentBranch);
        }

        ArbiterFactSet facts = EnrichedFacts();

        Assert.True(
            facts.TryGet("review.base-ref", out ArbiterFact? basis),
            "the review must record the base it measured from.");
        Assert.Equal(stored ? ParentBranch : "origin/HEAD", basis!.Value);
    }

    private IReadOnlyList<string> ChangedPaths()
    {
        ArbiterFactSet facts = EnrichedFacts();

        Assert.True(
            facts.TryGet("review.changed-paths", out ArbiterFact? changed),
            "a review trigger must supply review.changed-paths.");
        return Assert.IsAssignableFrom<IReadOnlyList<string>>(changed!.Value);
    }

    private ArbiterFactSet EnrichedFacts()
    {
        const string prompt = "KYBER-ARBITER: true\nLENS: correctness\n\nApply the lens to the change.";

        ArbiterEvent ev = new()
        {
            Harness = "claude",
            Phase = "pre",
            Target = "review-lens",
            Prompt = prompt,
            HarnessCaller = "code-reviewer",
            Cwd = _probe.Root,
            IsDispatch = true
        };

        TriggerClassification classification = TriggerClassifier.ClassifySingle(ev, "review-lens", prompt);
        ArbiterFactSet facts = TriggerFactBuilder.Build(classification, prompt, KyberWeaveConfig.ProductDefaults);
        return new HookGitFacts(_probe.Root, _home.Path).Enrich(facts, classification, ev);
    }
}
