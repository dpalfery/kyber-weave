using System.ComponentModel;
using KyberWeave.Cli.Rendering;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Skills.Validation;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Skills;

public sealed class LintSettings : AnalysisSettings
{
    [CommandOption("--min-desc-score <SCORE>")]
    [Description("Minimum description routing score (0-100) to pass. Default 70.")]
    [DefaultValue(70)]
    public int MinDescriptionScore { get; set; } = 70;

    [CommandOption("--explain")]
    [Description("Print the per-skill description score breakdown (table format only).")]
    public bool Explain { get; set; }
}

public sealed class LintCommand : Command<LintSettings>
{
    public override int Execute(CommandContext context, LintSettings settings)
    {
        var report = new DiagnosticReport();
        var set = CommandHelpers.LoadOrReport(settings.Path, report);

        var linter = new RoutingLinter { MinDescriptionScore = settings.MinDescriptionScore };

        if (set is not null)
        {
            foreach (var skill in set.Skills)
                report.AddRange(linter.LintSkill(skill));
            report.AddRange(linter.LintSet(set));

            if (settings.Explain && settings.ParsedFormat == OutputFormat.Table)
            {
                AnsiConsole.WriteLine();
                foreach (var skill in set.Skills)
                {
                    var score = DescriptionScorer.Score(skill);
                    var name = skill.Frontmatter.Name ?? skill.DirectoryName;
                    var color = score.Total >= settings.MinDescriptionScore ? "green" : "yellow";
                    AnsiConsole.MarkupLine($"[bold]{Markup.Escape(name)}[/] — routing score [{color}]{score.Total}/100[/]");
                    var t = new Table().Border(TableBorder.Minimal);
                    t.AddColumn("Dimension"); t.AddColumn("Score"); t.AddColumn("Detail");
                    foreach (var c in score.Components)
                        t.AddRow(Markup.Escape(c.Name), $"{c.Points}/{c.MaxPoints}", Markup.Escape(c.Detail));
                    AnsiConsole.Write(t);
                    AnsiConsole.WriteLine();
                }
            }
        }

        CommandHelpers.Finish(report, settings, "skill lint", "Skill");
        // Lint errors (name collisions) gate; warnings do not fail by default.
        return report.HasErrors ? 1 : 0;
    }
}
