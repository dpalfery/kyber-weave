using System.ComponentModel;
using System.Threading;
using System.Text.Json;
using System.Text.Json.Nodes;
using KyberWeave.Core.Skills.Model;
using KyberWeave.Core.Skills.Parsing;
using KyberWeave.Core.Skills.Routing;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Skills;

public sealed class RouteSettings : CommandSettings
{
    [CommandArgument(0, "[prompt]")]
    [Description("A user prompt to simulate routing for. Omit when using --eval.")]
    public string? Prompt { get; set; }

    [CommandOption("-s|--skills <PATH>")]
    [Description("Path to the skills root. Defaults to current directory.")]
    [DefaultValue(".")]
    public string SkillsPath { get; set; } = ".";

    [CommandOption("--eval <FILE>")]
    [Description("Run a routing eval file (YAML) of prompt/expected cases instead of a single prompt.")]
    public string? EvalFile { get; set; }

    [CommandOption("--min-accuracy <VALUE>")]
    [Description("In --eval mode, minimum routing accuracy (0-1) to pass. Default 0.9.")]
    [DefaultValue(0.9)]
    public double MinAccuracy { get; set; } = 0.9;

    [CommandOption("--strategy <NAME>")]
    [Description("Routing strategy: lexical (default; offline/deterministic).")]
    [DefaultValue("lexical")]
    public string Strategy { get; set; } = "lexical";

    [CommandOption("--threshold <VALUE>")]
    [Description("Fire threshold: minimum score for a skill to be considered selected. Default 0.08.")]
    [DefaultValue(0.08)]
    public double Threshold { get; set; } = 0.08;

    [CommandOption("--json")]
    [Description("Emit JSON instead of a table.")]
    public bool Json { get; set; }
}

public sealed class RouteCommand : Command<RouteSettings>
{
    protected override int Execute(CommandContext context, RouteSettings settings, CancellationToken cancellationToken)
    {
        SkillSet set = SkillLoader.LoadSet(settings.SkillsPath);
        if (set.Count == 0)
        {
            AnsiConsole.MarkupLine($"[red]No skills found under '{Markup.Escape(settings.SkillsPath)}'.[/]");
            return 2;
        }

        IRoutingStrategy strategy = settings.Strategy.ToUpperInvariant() switch
        {
            // Hook for embedding / llm-judge strategies; lexical is the offline default.
            _ => new LexicalRoutingStrategy { FireThreshold = settings.Threshold }
        };

        return settings.EvalFile is not null
            ? RunEval(settings, set, strategy)
            : RunSingle(settings, set, strategy);
    }

    public int Execute(CommandContext context, RouteSettings settings) => Execute(context, settings, CancellationToken.None);

    private static int RunSingle(RouteSettings settings, SkillSet set, IRoutingStrategy strategy)
    {
        if (string.IsNullOrWhiteSpace(settings.Prompt))
        {
            AnsiConsole.MarkupLine("[red]Provide a prompt, or use --eval <file>.[/]");
            return 2;
        }

        RoutingResult result = strategy.Route(settings.Prompt!, set);

        if (settings.Json)
        {
            JsonObject obj = new JsonObject
            {
                ["prompt"] = settings.Prompt,
                ["fired"] = result.Fired,
                ["selected"] = result.SelectedSkill,
                ["margin"] = Math.Round(result.Margin, 4),
                ["ranked"] = new JsonArray(
                    result.Ranked.Select(c => (JsonNode)new JsonObject
                    {
                        ["skill"] = c.SkillName,
                        ["score"] = Math.Round(c.Score, 4)
                    }).ToArray())
            };
            Console.WriteLine(obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        AnsiConsole.MarkupLine($"Prompt: [italic]\"{Markup.Escape(settings.Prompt!)}\"[/]");
        AnsiConsole.MarkupLine(result.Fired
            ? $"Would fire: [green bold]{Markup.Escape(result.SelectedSkill!)}[/] (margin over runner-up: {result.Margin:F3})"
            : $"[yellow]No skill clears the fire threshold ({strategy.Name} @ {settings.Threshold}).[/]");

        Table table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Rank"); table.AddColumn("Skill"); table.AddColumn("Score");
        int rank = 1;
        foreach (RoutingCandidate c in result.Ranked.Take(8))
        {
            string mark = rank == 1 && result.Fired ? "[green]→[/] " : "  ";
            table.AddRow($"{mark}{rank}", Markup.Escape(c.SkillName), $"{c.Score:F3}");
            rank++;
        }
        AnsiConsole.Write(table);
        return 0;
    }

    private static int RunEval(RouteSettings settings, SkillSet set, IRoutingStrategy strategy)
    {
        if (!File.Exists(settings.EvalFile))
        {
            AnsiConsole.MarkupLine($"[red]Eval file not found: {Markup.Escape(settings.EvalFile!)}[/]");
            return 2;
        }

        RoutingEvalFile evalFile = RoutingEvalFile.Load(settings.EvalFile!);
        RoutingEvalSummary summary = new RoutingEvaluator(strategy).Evaluate(evalFile, set);

        if (settings.Json)
        {
            JsonObject obj = new JsonObject
            {
                ["accuracy"] = Math.Round(summary.Accuracy, 4),
                ["passed"] = summary.Passed,
                ["total"] = summary.Total,
                ["minAccuracy"] = settings.MinAccuracy,
                ["cases"] = new JsonArray(
                    summary.Results.Select(r => (JsonNode)new JsonObject
                    {
                        ["prompt"] = r.Case.Prompt,
                        ["expected"] = r.ExpectedLabel,
                        ["actual"] = r.ActualLabel,
                        ["passed"] = r.Passed
                    }).ToArray())
            };
            Console.WriteLine(obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            Table table = new Table().Border(TableBorder.Rounded).Expand();
            table.AddColumn(""); table.AddColumn("Prompt"); table.AddColumn("Expected"); table.AddColumn("Actual");
            foreach (RoutingCaseResult r in summary.Results)
            {
                string glyph = r.Passed ? "[green]✔[/]" : "[red]✘[/]";
                table.AddRow(glyph, Markup.Escape(Trunc(r.Case.Prompt, 50)), Markup.Escape(r.ExpectedLabel), Markup.Escape(r.ActualLabel));
            }
            AnsiConsole.Write(table);
            string color = summary.Accuracy >= settings.MinAccuracy ? "green" : "red";
            AnsiConsole.MarkupLine($"Routing accuracy: [{color}]{summary.Accuracy:P0}[/] ({summary.Passed}/{summary.Total}), threshold {settings.MinAccuracy:P0}.");
        }

        return summary.Accuracy >= settings.MinAccuracy ? 0 : 1;
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
}
