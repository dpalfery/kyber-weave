using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Arbiter;

/// <summary>Lists the effective Arbiter rules: id, trigger, step, question, answers, effects and facts.</summary>
public sealed class ArbiterRulesCommand(IArbiterConfigResolver configs, IAnsiConsole console) : Command<ArbiterSettings>
{
    /// <summary>
    /// Prints one row per rule (one rule per trigger, sharing the rule id).
    /// With <c>--trigger</c>, only that trigger's rules are listed; an unknown
    /// trigger exits 1 and names the known triggers. Exits 1 when the
    /// configuration itself fails to load.
    /// </summary>
    public override int Execute(CommandContext context, ArbiterSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        DiagnosticReport report = new();
        if (!configs.TryResolve(settings.Path, settings.Config, report, out KyberWeaveConfig config, out _))
        {
            CommandHelpers.Finish(console, report, settings, "arbiter rules", "Rule");
            return 1;
        }

        IReadOnlyList<ArbiterRule>? rules = FilterRules(config.Arbiter.Rules, settings.Trigger, report);
        if (rules is null)
        {
            CommandHelpers.Finish(console, report, settings, "arbiter rules", "Rule");
            return 1;
        }

        Table table = BuildRulesTable(rules);

        // Unfolded so a wide table is not split mid-word, which would break a column name such as "For-each".
        CommandHelpers.WriteUnfolded(console, () => console.Write(table));
        console.MarkupLine($"[grey]{rules.Count} rule(s) listed.[/]");

        CommandHelpers.Finish(console, report, settings, "arbiter rules", "Rule");
        return report.HasErrors ? 1 : 0;
    }

    /// <summary>Runs the command without cancellation; the entry point tests use.</summary>
    public int Execute(CommandContext context, ArbiterSettings settings) =>
        Execute(context, settings, CancellationToken.None);

    /// <summary>Validates and applies <c>--trigger</c>; an unknown trigger is reported and null returned.</summary>
    private static IReadOnlyList<ArbiterRule>? FilterRules(IReadOnlyList<ArbiterRule> rules, string? trigger, DiagnosticReport report)
    {
        if (trigger is null)
            return rules;

        try
        {
            ArbiterTriggerFamilies.ForTrigger(trigger);
        }
        catch (ArgumentException exception)
        {
            report.Add(new Diagnostic(
                RuleValidator.MalformedSection,
                Severity.Error,
                exception.Message,
                "--trigger",
                Hint: "List every trigger's rules by omitting --trigger."));
            return null;
        }

        return [.. rules.Where(rule => string.Equals(rule.Trigger, trigger, StringComparison.Ordinal))];
    }

    /// <summary>Builds the one-row-per-rule table. Sized to its content: under an unfolded console width, Expand pads it to the width.</summary>
    private static Table BuildRulesTable(IReadOnlyList<ArbiterRule> rules)
    {
        Table table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Id");
        table.AddColumn("Trigger");
        table.AddColumn("Step");
        table.AddColumn("Question");
        table.AddColumn("Answers");
        table.AddColumn("Effects");
        table.AddColumn("Facts");
        table.AddColumn("Shadow");
        table.AddColumn("Gates");
        table.AddColumn("For-each");
        foreach (ArbiterRule rule in rules.OrderBy(r => r.Id, StringComparer.Ordinal).ThenBy(r => r.Trigger, StringComparer.Ordinal))
        {
            table.AddRow(
                new Markup(Markup.Escape(rule.Id)),
                new Markup(Markup.Escape(rule.Trigger)),
                new Markup(Markup.Escape(StepFor(rule))),
                new Markup(Markup.Escape(rule.Question)),
                new Markup(Markup.Escape(string.Join(", ", rule.Answers))),
                new Markup(Markup.Escape(string.Join(", ", rule.Effects.Select(pair => $"{pair.Key} -> {pair.Value}")))),
                new Markup(Markup.Escape(string.Join(", ", FactsRead(rule)))),
                new Markup(rule.Shadow ? "shadow" : "enforced"),
                new Markup(ArbiterRuleAdvisory.IsAdvisory(rule) ? "advisory" : "—"),
                new Markup(Markup.Escape(rule.Ask?.ForEach ?? "—")));
        }

        return table;
    }

    private static string StepFor(ArbiterRule rule)
    {
        bool step0 = rule.Decide is { Count: > 0 };
        bool step1 = rule.Ask is not null;
        return (step0, step1) switch
        {
            (true, true) => "0+1",
            (true, false) => "0",
            (false, true) => "1",
            _ => "—",
        };
    }

    private static IReadOnlyList<string> FactsRead(ArbiterRule rule)
    {
        HashSet<string> facts = new(StringComparer.Ordinal);
        if (rule.Decide is not null)
        {
            foreach (RuleDecideClause clause in rule.Decide)
                CollectPredicateFacts(clause.When, facts);
        }

        if (rule.Ask is not null)
        {
            foreach (string value in rule.Ask.State.Values)
                facts.Add(value);
        }

        return [.. facts.Order(StringComparer.Ordinal)];
    }

    private static void CollectPredicateFacts(RulePredicate predicate, HashSet<string> facts)
    {
        foreach (RulePredicate child in predicate.Children)
            CollectPredicateFacts(child, facts);

        if (predicate.Fact is not null)
            facts.Add(predicate.Fact);

        if (predicate.Operand is { IsFactRef: true, FactName: not null })
            facts.Add(predicate.Operand.FactName);
    }
}
