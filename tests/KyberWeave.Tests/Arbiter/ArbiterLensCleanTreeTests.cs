using System.Text;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// <c>KW-ARB-LENS-001</c> must not skip the <c>correctness</c> and <c>test-adequacy</c> lenses
/// on a committed tree. A per-task commit makes a clean tree the normal case, so a rule that
/// reads only the dirty set skips both lenses for almost every council.
/// </summary>
/// <remarks>
/// <para>
/// The defect is a vacuous truth: the skip fires when every changed path is Markdown, and an
/// empty set satisfies that condition, so a clean tree loses both lenses without any rule
/// appearing to be wrong. The first test pins the fact that carries the truth — on a committed
/// tree the review's changed paths are not empty — and the second pins the decision the fact
/// feeds.
/// </para>
/// <para>
/// The repository is built through <see cref="RunStartProbe" />, the suite's one allow-listed
/// driver of a real git repository with a real origin: a second spawn site would have to be
/// bounded in the spawn-site audit, and the change under test needs a base to be compared
/// against, which that fixture supplies as <c>main</c> and <c>origin/main</c>.
/// </para>
/// </remarks>
public sealed class ArbiterLensCleanTreeTests : IDisposable
{
    private const string ChangedFile = "src/Payment.cs";

    private readonly RunStartProbe _probe = CommittedChangeOnAFeatureBranch();

    public void Dispose()
    {
        _probe.Dispose();
    }

    /// <summary>
    /// A feature branch one commit ahead of <c>main</c>, with nothing uncommitted: the tree a
    /// per-task commit leaves behind.
    /// </summary>
    private static RunStartProbe CommittedChangeOnAFeatureBranch()
    {
        RunStartProbe probe = RunStartProbe.Create();
        probe.Git("switch", "-c", "claude/feat/lens-applicability", "origin/main");

        string changed = Path.Combine(probe.Root, ChangedFile.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(changed)!);
        File.WriteAllText(changed, "// one committed change\n", Encoding.UTF8);

        probe.Git("add", "-A");
        probe.Git("commit", "-m", "feat: one committed change");
        return probe;
    }

    /// <summary>
    /// The facts a <c>lens.spawn</c> supplies, after the hook's git reader has run over the
    /// clean tree.
    /// </summary>
    private ArbiterFactSet LensFacts(string lens)
    {
        string prompt = $"KYBER-ARBITER: true\nLENS: {lens}\n\nApply the lens to the change.";

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
        Assert.Equal("lens.spawn", classification.Trigger);

        ArbiterFactSet facts = TriggerFactBuilder.Build(classification, prompt, KyberWeaveConfig.ProductDefaults);
        return new HookGitFacts(_probe.Root).Enrich(facts, classification, ev);
    }

    /// <summary>
    /// The review's changed paths are what the passed change contains, not what happens to be
    /// uncommitted when the council starts. An empty set here is the defect that loses both
    /// lenses.
    /// </summary>
    [Fact]
    public void ACommittedChangeIsStillInTheReviewChangedPaths()
    {
        ArbiterFactSet facts = LensFacts("correctness");

        Assert.True(
            facts.TryGet("review.changed-paths", out ArbiterFact? changed),
            "a lens.spawn must supply review.changed-paths; without the fact the rule cannot judge applicability.");
        IReadOnlyList<string> paths = Assert.IsAssignableFrom<IReadOnlyList<string>>(changed!.Value);

        Assert.Contains(ChangedFile, paths);
    }

    /// <summary>
    /// Both lenses stay applicable on a committed tree. The rule still defers to the model when
    /// a real Markdown-only change arrives; what it must not do is answer "not-applicable" from
    /// a set that is empty because the work was committed.
    /// </summary>
    /// <param name="lens">The lens the fixture spawns.</param>
    [Theory]
    [InlineData("correctness")]
    [InlineData("test-adequacy")]
    public void OnACommittedTreeTheLensIsNotSkipped(string lens)
    {
        ArbiterOutcome outcome = RuleEngine.EvaluateStep0(
            ArbiterConfig.ProductDefaults.Rules,
            LensFacts(lens),
            "lens.spawn");

        ArbiterRuleAnswer answer = Assert.Single(
            outcome.RuleAnswers,
            ruleAnswer => string.Equals(ruleAnswer.RuleId, "KW-ARB-LENS-001", StringComparison.Ordinal));

        Assert.NotEqual("not-applicable", answer.Answer);
        Assert.NotEqual(RuleEffects.Skip, outcome.CombinedEffect);
    }
}
