using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Parsing;
using KyberWeave.Tests.Corpus;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The 36 shipped Arbiter rules live in three role files, and the loader merges them into
/// the same set the single file held before the split.
/// </summary>
/// <remarks>
/// The snapshot below is the pre-split set, rule for rule. It is deliberately explicit rather
/// than re-derived from the files it checks: a test that parses the new files and compares
/// them with themselves would pass after a rule's question or effect changed, which is the
/// regression this task exists to prevent. A later task that changes a shipped rule changes
/// this table in the same commit, and a reader sees what changed.
/// </remarks>
public sealed class ArbiterRuleFolderSplitTests
{
    private const string Snapshot = """
        KW-ARB-PLAN-001|delegate|has-tasks, unresolved, names-plan, no-plan|has-tasks=allow, names-plan=allow, no-plan=escalate, unresolved=allow|false|true|Does this dispatch name the plan or specification file its work comes from?
        KW-ARB-SCOPE-001|delegate|task-unknown, in-task-files, beyond-files, no-task-files, in-plan, not-in-plan|beyond-files=allow, in-plan=allow, in-task-files=allow, no-task-files=allow, not-in-plan=escalate, task-unknown=allow|false|true|Is the work this dispatch asks for one of the plan's tasks?
        KW-ARB-SCOPE-002|delegate|no-task, within-task, adds-work, unrelated|adds-work=escalate, no-task=allow, unrelated=escalate, within-task=allow|false|true|Does the delegation ask for work the plan task does not describe?
        KW-ARB-READY-001|delegate|no-task, ready, waiting, overlaps-in-flight, unknown-dependency, returns-unobservable, returns-unread|no-task=allow, overlaps-in-flight=escalate, ready=allow, returns-unobservable=allow, returns-unread=allow, unknown-dependency=escalate, waiting=escalate|false|true|Has every task this one depends on finished?
        KW-ARB-OWNER-001|delegate|no-task, owner, not-owner, no-mapping|no-mapping=allow, no-task=allow, not-owner=escalate, owner=allow|false|true|Does the task name the dispatch target as the specialist to do it?
        KW-ARB-OWNER-002|delegate|target, other|other=allow, target=allow|false|false|Which specialist in the caller's implementation roster owns this task?
        KW-ARB-ROSTER-001|delegate,investigate|listed, no-roster, not-listed|listed=allow, no-roster=allow, not-listed=escalate|false|true|Is the target in the caller's delegates-to?
        KW-ARB-RUN-001|delegate,delegate.planner,investigate|not-applicable, started, not-started|not-applicable=allow, not-started=escalate, started=allow|false|true|Has a run been started for this repository before an orchestrator hands a specialist work that writes files?
        KW-ARB-DIFF-001|delegate.returned|unattributed, out-of-scope, no-task, no-task-files, inside-task, outside-task|inside-task=allow, no-task=allow, no-task-files=allow, out-of-scope=escalate, outside-task=escalate, unattributed=allow|false|true|Are all the files this delegation changed part of the work its task describes?
        KW-ARB-PLANNER-001|delegate.planner|recognised, malformed|malformed=escalate, recognised=allow|false|true|Does this dispatch ask the planner for planning work?
        KW-ARB-PLANNER-DIFF-001|delegate.planner.returned|unattributed, inside, outside|inside=allow, outside=escalate, unattributed=allow|false|true|Did the planner write only inside the plan, spec and todo directories?
        KW-ARB-READONLY-001|investigate.returned|not-read-only, unattributed, unchanged, changed|changed=escalate, not-read-only=allow, unattributed=allow, unchanged=allow|false|true|Did the read-only dispatch leave the tree unchanged?
        KW-ARB-LENS-001|lens.spawn|applies, not-applicable, asked-by-review-seats|applies=allow, asked-by-review-seats=allow, not-applicable=skip|false|true|Does the lens apply to this change?
        KW-ARB-QUOTE-001|lens.returned|present, elsewhere, absent|absent=annotate, elsewhere=annotate, present=allow|false|true|Is each finding's excerpt present at its file:line?
        KW-ARB-PREEX-001|lens.returned|in-hunk, outside-hunk|in-hunk=allow, outside-hunk=annotate|false|true|Is the excerpt inside a changed hunk?
        KW-ARB-CLAIM-001|refute.spawn|supports, contradicts, says_nothing, asked-by-review-verdict|asked-by-review-verdict=allow, contradicts=allow, says_nothing=allow, supports=verify|false|true|Do the quoted code and its surroundings support the finding's claim?
        KW-ARB-GATE-CORROBORATED-001|refute.spawn|corroborated, not-corroborated|corroborated=verify, not-corroborated=allow|false|true|Does a failed gate already corroborate the finding?
        KW-ARB-GATE-001|gate.select|applies, not-applicable|applies=applies, not-applicable=not-applicable|false|true|Does any changed path match the gate's applies-when.paths?
        KW-ARB-PLANREAD-001|delegate|names-plan, mentions-only, no-plan, closeout, not-a-task|closeout=allow, mentions-only=allow, names-plan=escalate, no-plan=allow, not-a-task=allow|false|true|Does the dispatch tell the worker to open or read a plan, specification or todo file?
        KW-ARB-GATE-REPORT-001|delegate.returned|verified, disagrees, no-report, no-gates-declared, not-a-task, ran-gate, missing|disagrees=escalate, missing=escalate, no-gates-declared=allow, no-report=allow, not-a-task=allow, ran-gate=allow, verified=allow|false|true|Does the worker's report say it ran the task gate on its changes?
        KW-ARB-LENS-002|lens.spawn|no-code-change|no-code-change=skip|false|true|Is this lens one that has nothing to judge in a change with no code in it?
        KW-ARB-DIFFICULTY-001|delegate.model|easy, standard, hard|easy=retarget, hard=retarget, standard=retarget|false|true|How hard is this task: easy, standard or hard?
        KW-ARB-LADDER-001|delegate.model|returns-unobservable, first-attempt, profile-unrecorded, top-of-ladder, step-up|first-attempt=keep, profile-unrecorded=keep, returns-unobservable=keep, step-up=retarget, top-of-ladder=keep|false|true|Did the previous attempt at this task fail on a profile below the top of the ladder?
        KW-ARB-NITPICK-001|lens.returned|substantive, nitpick, unsure, asked-by-review-verdict|asked-by-review-verdict=allow, nitpick=drop, substantive=allow, unsure=allow|false|true|Is this lens finding a nitpick, or a substantive defect?
        KW-ARB-NITPICK-002|refute.spawn|dropped|dropped=verify|true|true|Did the nitpick filter drop this finding?
        KW-ARB-DUP-001|refute.spawn|same-defect, different-defect, asked-by-review-verdict|asked-by-review-verdict=allow, different-defect=allow, same-defect=duplicate|false|true|Does an earlier finding of this review name the same defect as the refuted finding?
        KW-ARB-TRIAGE-001|triage.shortcut|trivial, needs-architect|needs-architect=keep, trivial=answer|true|true|Is this defect a trivial single-site fix that needs no architect?
        KW-ARB-INTAKE-001|intake.shortcut|plan, spec|plan=answer, spec=answer|true|true|Does this request call for a written plan or a written specification?
        KW-ARB-DOCIMPACT-001|docs-impact.shortcut|touches, no-doc-change, doc-change, unknown|doc-change=keep, no-doc-change=answer, touches=keep, unknown=keep|true|true|Does this change need no documentation change?
        KW-ARB-FAILURE-001|delegate.failure|same, different, first|different=allow, first=allow, same=same-failure|true|true|Is the current failure the same failure as the previous attempt of this cluster?
        KW-ARB-TRIM-001|sections.select|relevant, not-relevant|not-relevant=advise, relevant=keep|true|true|Does this section of the document bear on the task?
        KW-ARB-COMMENT-001|comment.triage|real, nitpick, already-fixed|already-fixed=advise, nitpick=advise, real=keep|true|true|Is this bot review comment a real defect, a nitpick, or already fixed in the current code?
        KW-ARB-CHECKOUT-001|delegate.returned,delegate.planner.returned,investigate.returned|clean, stray|clean=allow, stray=escalate|false|true|Did this dispatch leave files in the repository's canonical checkout while its run works in its own worktree?
        KW-ARB-MODE-002|delegate.returned|no-task, not-required, failing-run-first, no-failing-run|failing-run-first=allow, no-failing-run=allow, no-task=allow, not-required=allow|false|true|In test-first mode, did the worker's task gate fail on its new tests before it passed?
        KW-ARB-SEVERITY-001|review.verdict|too-high, about-right, too-low|about-right=keep, too-high=advise, too-low=advise|false|true|Does the finding's stated severity match its stated impact?
        KW-ARB-OVERRIDE-001|review.verdict|belongs, does-not-belong, unsure|belongs=advise, does-not-belong=keep, unsure=keep|false|true|Does the finding belong to the class of finding the host's severity override describes?
        """;

    private static readonly (string Role, int Count)[] Roles =
    [
        ("conductor", 21),
        ("architect", 2),
        ("reviewer", 13)
    ];

    [Fact]
    public void TheRulesAreSplitByRoleWithTheExpectedCounts()
    {
        foreach ((string role, int expected) in Roles)
        {
            ArbiterDefaultRulesYaml document = ParseRole(role);

            Assert.NotNull(document.Rules);
            Assert.Equal(expected, document.Rules.Count);
        }
    }

    [Fact]
    public void EachRuleIsInExactlyOneFile()
    {
        Dictionary<string, string> owner = new(StringComparer.Ordinal);

        foreach ((string role, int _) in Roles)
        {
            foreach (ArbiterRuleYaml rule in ParseRole(role).Rules!)
            {
                Assert.True(
                    owner.TryAdd(rule.Id!, role),
                    $"Rule '{rule.Id}' is declared in both '{owner.GetValueOrDefault(rule.Id!)}' and '{role}'.");
            }
        }

        Assert.Equal(36, owner.Count);

        foreach (string line in Snapshot.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string id = line.Split('|')[0];
            Assert.True(owner.ContainsKey(id), $"Rule '{id}' is declared in no role file.");
        }
    }

    [Fact]
    public void TheLoadedRuleSetEqualsThePreSplitSet()
    {
        ArbiterConfig defaults = ArbiterConfigLoader.LoadEmbeddedDefaults();
        ILookup<string, ArbiterRule> byId = defaults.Rules.ToLookup(rule => rule.Id, StringComparer.Ordinal);

        HashSet<string> expectedIds = new(StringComparer.Ordinal);

        foreach (string line in Snapshot.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = line.Split('|');
            string id = parts[0];
            expectedIds.Add(id);

            string[] triggers = parts[1].Split(',');
            string[] answers = parts[2].Split(", ");
            Dictionary<string, string> effects = parts[3]
                .Split(", ")
                .Select(pair => pair.Split('='))
                .ToDictionary(pair => pair[0], pair => pair[1], StringComparer.Ordinal);
            bool shadow = bool.Parse(parts[4]);
            bool enabled = bool.Parse(parts[5]);
            string question = parts[6];

            List<ArbiterRule> loaded = byId[id].ToList();
            Assert.Equal(triggers.Length, loaded.Count);
            Assert.Equal(triggers.Order(StringComparer.Ordinal), loaded.Select(rule => rule.Trigger).Order(StringComparer.Ordinal));

            foreach (ArbiterRule rule in loaded)
            {
                Assert.Equal(question, rule.Question);
                Assert.Equal(answers, rule.Answers);
                Assert.Equal(
                    effects.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToList(),
                    rule.Effects.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToList());
                Assert.Equal(shadow, rule.Shadow);
                Assert.Equal(enabled, rule.Enabled);
            }
        }

        Assert.Equal(expectedIds, byId.Select(group => group.Key).ToHashSet(StringComparer.Ordinal));
    }

    [Fact]
    public void TheTestPatternsAndLensPathsSurviveTheSplit()
    {
        ArbiterConfig defaults = ArbiterConfigLoader.LoadEmbeddedDefaults();

        Assert.Equal(
            ["tests/**", "**/*.test.*", "**/*.spec.*", "**/*Tests.cs"],
            defaults.TestFilePatterns);
        Assert.Equal(2, defaults.LensPaths.Count);
        Assert.Contains(defaults.LensPaths, entry => entry.Lens == "correctness");
        Assert.Contains(defaults.LensPaths, entry => entry.Lens == "test-adequacy");
    }

    private static ArbiterDefaultRulesYaml ParseRole(string role)
    {
        string path = $"products/kyber-squad/standards/{role}/{role}.arbiter-rules.yml";

        Assert.True(CheckoutCorpusSource.Checkout.Exists(path), $"The '{role}' role rules file is missing: {path}.");
        ArbiterDefaultRulesYaml? document =
            MarkdownFrontmatterReader.Deserializer.Deserialize<ArbiterDefaultRulesYaml>(
                CheckoutCorpusSource.Checkout.ReadText(path));
        Assert.NotNull(document);
        return document;
    }
}
