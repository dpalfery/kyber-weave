using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Arbiter;

/// <summary>Lists the effective Arbiter rules: id, trigger, step, question, answers, effects and facts.</summary>
public sealed class ArbiterRulesCommand : Command<ArbiterSettings>
{
    /// <summary>
    /// Prints one row per rule (one rule per trigger, sharing the rule id).
    /// With <c>--trigger</c>, only that trigger's rules are listed; an unknown
    /// trigger exits 1 and names the known triggers. Exits 1 when the
    /// configuration itself fails to load.
    /// </summary>
    protected override int Execute(CommandContext context, ArbiterSettings settings, CancellationToken cancellationToken)
    {
        DiagnosticReport report = new();
        if (!ArbiterCommandComposition.TryResolveConfig(settings, report, out KyberWeaveConfig config, out _))
        {
            CommandHelpers.Finish(report, settings, "arbiter rules", "Rule");
            return 1;
        }

        IReadOnlyList<ArbiterRule> rules = config.Arbiter.Rules;
        if (settings.Trigger is not null)
        {
            try
            {
                ArbiterTriggerFamilies.ForTrigger(settings.Trigger);
            }
            catch (ArgumentException exception)
            {
                report.Add(new Diagnostic(
                    RuleValidator.MalformedSection,
                    Severity.Error,
                    exception.Message,
                    "--trigger",
                    Hint: "List every trigger's rules by omitting --trigger."));
                CommandHelpers.Finish(report, settings, "arbiter rules", "Rule");
                return 1;
            }

            rules = [.. rules.Where(rule =>
                string.Equals(rule.Trigger, settings.Trigger, StringComparison.Ordinal))];
        }

        Table table = new Table().Border(TableBorder.Rounded).Expand();
        table.AddColumn("Id");
        table.AddColumn("Trigger");
        table.AddColumn("Step");
        table.AddColumn("Question");
        table.AddColumn("Answers");
        table.AddColumn("Effects");
        table.AddColumn("Facts");
        foreach (ArbiterRule rule in rules.OrderBy(r => r.Id, StringComparer.Ordinal).ThenBy(r => r.Trigger, StringComparer.Ordinal))
        {
            table.AddRow(
                new Markup(Markup.Escape(rule.Id)),
                new Markup(Markup.Escape(rule.Trigger)),
                new Markup(Markup.Escape(StepFor(rule))),
                new Markup(Markup.Escape(rule.Question)),
                new Markup(Markup.Escape(string.Join(", ", rule.Answers))),
                new Markup(Markup.Escape(string.Join(", ", rule.Effects.Select(pair => $"{pair.Key} -> {pair.Value}")))),
                new Markup(Markup.Escape(string.Join(", ", FactsRead(rule)))));
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[grey]{rules.Count} rule(s) listed.[/]");

        CommandHelpers.Finish(report, settings, "arbiter rules", "Rule");
        return report.HasErrors ? 1 : 0;
    }

    /// <summary>Runs the command without cancellation; the entry point tests use.</summary>
    public int Execute(CommandContext context, ArbiterSettings settings) =>
        Execute(context, settings, CancellationToken.None);

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
