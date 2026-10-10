using KyberWeave.Core.Arbiter.Plans;
using KyberWeave.Core.Diagnostics;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Arbiter;

/// <summary>Prints what the plan parser understood for a plan file.</summary>
public sealed class ArbiterPlanCommand : Command<ArbiterSettings>
{
    /// <summary>A plan with no Tasks section: downstream readiness answers <c>no-tasks</c>.</summary>
    public const string NoTasks = "KW-ARB-PLAN-001";

    /// <summary>The plan file is missing or unreadable.</summary>
    public const string PlanNotReadable = "KW-ARB-PLAN-002";

    /// <summary>
    /// Parses the plan file named by the positional path and prints its tasks,
    /// files, dependencies, contract rows and out-of-scope paths, plus
    /// <c>KW-ARB-PARSE-00x</c> diagnostics. A plan without tasks reports
    /// <c>KW-ARB-PLAN-001</c>; a missing or unreadable file reports
    /// <c>KW-ARB-PLAN-002</c> and exits 1.
    /// </summary>
    protected override int Execute(CommandContext context, ArbiterSettings settings, CancellationToken cancellationToken)
    {
        DiagnosticReport report = new();
        string resolved = Path.GetFullPath(settings.Path);
        if (!File.Exists(resolved))
        {
            report.Add(new Diagnostic(
                PlanNotReadable,
                Severity.Error,
                $"Plan file '{settings.Path}' does not exist.",
                settings.Path,
                Hint: "Pass the plan file to print, for example 'arbiter plan docs/plans/plan.md'."));
            CommandHelpers.Finish(report, settings, "arbiter plan", "Task");
            return 1;
        }

        string text;
        try
        {
            text = File.ReadAllText(resolved);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            report.Add(new Diagnostic(
                PlanNotReadable,
                Severity.Error,
                $"Plan file '{resolved}' could not be read: {exception.Message}",
                settings.Path));
            CommandHelpers.Finish(report, settings, "arbiter plan", "Task");
            return 1;
        }

        PlanDocument document = PlanDocumentParser.Parse(text);
        report.AddRange(document.Diagnostics);

        AnsiConsole.MarkupLine($"[bold]Plan:[/] {Markup.Escape(resolved)}");
        AnsiConsole.MarkupLine($"[grey]Status: {Markup.Escape(document.Status ?? "—")}; mode: {Markup.Escape(document.DevelopmentMode)}.[/]");

        if (!document.HasTasks)
        {
            report.Add(new Diagnostic(
                NoTasks,
                Severity.Warning,
                $"Plan '{resolved}' carries no Tasks section.",
                settings.Path,
                Hint: "Add a '## Tasks' section with T1, T2, ... headings, or a spec-grammar checklist; without one, readiness answers no-tasks."));
        }

        if (document.Tasks.Count > 0)
        {
            Table tasks = new Table().Border(TableBorder.Rounded).Expand();
            tasks.AddColumn("Task");
            tasks.AddColumn("Title");
            tasks.AddColumn("Files");
            tasks.AddColumn("Depends on");
            foreach (PlanTask task in document.Tasks)
            {
                tasks.AddRow(
                    new Markup(Markup.Escape(task.Id)),
                    new Markup(Markup.Escape(task.Title)),
                    new Markup(Markup.Escape(task.Files.Count > 0 ? string.Join(", ", task.Files) : "—")),
                    new Markup(Markup.Escape(task.DependsOn.Count > 0 ? string.Join(", ", task.DependsOn) : "—")));
            }

            AnsiConsole.Write(tasks);
        }

        if (document.ContractRows.Count > 0)
        {
            AnsiConsole.MarkupLine(
                $"[grey]{document.ContractRows.Count} contract row(s): " +
                $"{Markup.Escape(string.Join(", ", document.ContractRows.Select(row => row.TaskId)))}.[/]");
        }

        if (document.OutOfScope.Count > 0)
        {
            AnsiConsole.MarkupLine(
                $"[grey]Out of scope: {Markup.Escape(string.Join(", ", document.OutOfScope))}.[/]");
        }

        CommandHelpers.Finish(report, settings, "arbiter plan", "Task");
        return report.HasErrors ? 1 : 0;
    }

    /// <summary>Runs the command without cancellation; the entry point tests use.</summary>
    public int Execute(CommandContext context, ArbiterSettings settings) =>
        Execute(context, settings, CancellationToken.None);
}
