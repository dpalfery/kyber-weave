using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// <c>KW-ARB-PLANREAD-001</c>: a dispatch whose prompt sends the worker into the plan is
/// escalated. The read guard is advisory where hooks are, so on a harness whose dispatch hook
/// fires this is the enforcement the guard cannot be.
/// </summary>
/// <remarks>
/// <para>
/// The rule runs on the dispatch prompt with the routing header block removed. The header names
/// the plan by design — <c>PLAN_FILE:</c> is routing metadata the worker is told not to read —
/// so a rule that read the raw prompt would fire on every dispatch, which is why the header
/// case below is a test of its own.
/// </para>
/// <para>
/// The rules are evaluated through <see cref="RuleEngine" /> one at a time rather than through
/// the evaluator, because the other shipped delegate rules answer from facts this fixture does
/// not supply and their escalations would hide which rule escalated.
/// </para>
/// <para>
/// The design also says the rule "may not fire where dispatch hooks are unconfirmed". That is a
/// property of hook installation rather than of the rule — where no hook runs, no rule runs —
/// so it is documented in ADR 0053 and the dispatch-coverage todo, and is not pinned here.
/// </para>
/// </remarks>
public sealed class ArbiterPlanReadRuleTests
{
    private const string PlanReadRule = "KW-ARB-PLANREAD-001";

    private static readonly string ShippedRulesPath =
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "src", "KyberWeave.Core", "Arbiter", "Rules", "default-rules.yml");

    /// <summary>A dispatch prompt with the routing header block, a task, and a body.</summary>
    private static string Prompt(string body) =>
        $"KYBER-ARBITER: true\nPLAN_FILE: docs/plans/2026-11-07-ready-tasks.md\nTASK: T26\n\n{body}";

    /// <summary>The shipped rule, or a failure that names the file it should be in.</summary>
    private static ArbiterRule Rule()
    {
        return Assert.Single(
            ArbiterConfig.ProductDefaults.Rules,
            rule => string.Equals(rule.Id, PlanReadRule, StringComparison.Ordinal));
    }

    /// <summary>The facts a delegate dispatch supplies for <paramref name="prompt"/>.</summary>
    /// <remarks>
    /// The ontology's docs root is <c>docs</c>, as in every other planning-directory test here:
    /// <see cref="KyberWeaveConfig.ProductDefaults" /> places it at <c>6-Docs</c>, so a prompt naming
    /// <c>docs/plans/…</c> would name no configured planning directory and the rule could not fire.
    /// </remarks>
    private static ArbiterFactSet Facts(string prompt, string target = "csharp-dev")
    {
        return TriggerFactBuilder.Build(
            "delegate",
            target,
            prompt,
            "conductor",
            "harness",
            new KyberWeaveConfig
            {
                Ontology = OntologyConfig.ProductDefaults.WithDocsRoot("docs"),
            });
    }

    /// <summary>The shipped rule's step-0 answer for those facts.</summary>
    private static string Answer(string prompt, string target = "csharp-dev") => RuleEngine.Decide(Rule(), Facts(prompt, target));

    /// <summary>The effect the shipped rule produces for those facts.</summary>
    private static string Effect(string prompt, string target = "csharp-dev")
    {
        ArbiterOutcome outcome = RuleEngine.EvaluateStep0([Rule()], Facts(prompt, target), "delegate");
        return outcome.CombinedEffect;
    }

    /// <summary>
    /// The case the rule exists for: the prompt tells the worker to open the plan, so the guard
    /// that cannot stop the read is replaced by a dispatch that does not happen.
    /// </summary>
    [Fact]
    public void APromptThatNamesAPlanningPathGoesToTheModel()
    {
        // The detector reads prose, so a named planning path is undecidable at step 0 and the model is asked
        // whether the worker is told to read it (ADR 0072). Only a confident names-plan escalates.
        string prompt = Prompt(
            "Read `docs/plans/2026-11-07-ready-tasks.md` and implement the task it describes in " +
            "`src/KyberWeave.Core/RunStart/RunStartPlanner.cs`.");

        Assert.Equal(RuleEngine.Undecidable, Answer(prompt));
        Assert.Equal(RuleEffects.Allow, Effect(prompt));
        Assert.Equal(RuleEffects.Escalate, Rule().Effects["names-plan"]);
        Assert.Contains("names-plan", Rule().Ask!.Criteria!.Keys);
    }

    /// <summary>
    /// Lexical variations (leading ./, duplicate slashes, /./, /../) must still be identified as
    /// planning paths and reach the model question rather than bypassing the rule.
    /// </summary>
    [Theory]
    [InlineData("./docs/plans/2026-11-07-ready-tasks.md")]
    [InlineData("docs//plans/2026-11-07-ready-tasks.md")]
    [InlineData("docs/./plans/2026-11-07-ready-tasks.md")]
    [InlineData("docs/sub/../plans/2026-11-07-ready-tasks.md")]
    public void APromptThatNamesALexicallyVariantPlanningPathGoesToTheModel(string path)
    {
        string prompt = Prompt(
            $"Read `{path}` and implement the task it describes in `src/KyberWeave.Core/RunStart/RunStartPlanner.cs`.");

        Assert.Equal(RuleEngine.Undecidable, Answer(prompt));
    }

    /// <summary>
    /// The header names the plan on purpose: it is how the Arbiter identifies the work, and the
    /// worker is told the path is routing metadata, not something to read. A rule that read the
    /// raw prompt would escalate every dispatch in a run.
    /// </summary>
    [Fact]
    public void TheRoutingHeaderAloneDoesNotEscalateTheDispatch()
    {
        string prompt = Prompt(
            "Implement the reader in `src/KyberWeave.Core/RunStart/RunStartPlanner.cs` and report the total.");

        Assert.Equal("no-plan", Answer(prompt));
        Assert.Equal(RuleEffects.Allow, Effect(prompt));
    }

    /// <summary>
    /// Closeout is the one dispatch that must read the plan: `docs-dev` harvests its decisions
    /// and archives it. The documentation capability profile is what makes that legitimate, so
    /// exempting it is a rule of the target, not a favour to one agent name.
    /// </summary>
    [Fact]
    public void ADocumentationTargetMayNameThePlanForCloseout()
    {
        string prompt = Prompt(
            "Harvest the durable decisions from `docs/plans/2026-11-07-ready-tasks.md` into canonical " +
            "documentation, then archive it.");

        Assert.Equal(RuleEffects.Allow, Effect(prompt, target: "docs-dev"));
    }

    /// <summary>
    /// Ids are permanent, so the new rule joins the file without renumbering any of the
    /// eighteen, and the count in the file's own header is part of the shipped contract: a
    /// reader checking how many rules ship must not have to count them.
    /// </summary>
    [Fact]
    public void TheShippedFileDeclaresTheRuleAsTheTwentySixth()
    {
        Assert.True(
            File.Exists(ShippedRulesPath),
            $"the shipped rules file must live at '{ShippedRulesPath}'.");
        string[] lines = File.ReadAllLines(ShippedRulesPath);

        Assert.Contains("34 shipped Arbiter rules", lines[0], StringComparison.Ordinal);

        string[] ids =
        [
            .. lines
                .Select(line => line.Trim())
                .Where(line => line.StartsWith("- id: ", StringComparison.Ordinal))
                .Select(line => line["- id: ".Length..].Trim())
        ];

        Assert.Equal(34, ids.Length);
        Assert.Contains(PlanReadRule, ids);
        Assert.Equal("KW-ARB-CHECKOUT-001", ids[^1]);
        Assert.Equal("delegate", Rule().Trigger);
    }
}
