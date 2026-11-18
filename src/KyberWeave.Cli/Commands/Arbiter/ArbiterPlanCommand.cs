using KyberWeave.Cli.Rendering;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter.Plans;
using KyberWeave.Core.Diagnostics;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Arbiter;

/// <summary>Prints what the plan parser understood for a plan file.</summary>
public sealed class ArbiterPlanCommand(
    IArbiterPlanService plans,
    IRepositoryFileStore files,
    IKyberWeaveConfigRepository configs,
    IAnsiConsole console,
    PlanArguments planArguments) : Command<ArbiterSettings>
{
    /// <summary>
    /// Parses the plan file named by the positional path and prints its tasks,
    /// files, dependencies, contract rows and out-of-scope paths, plus
    /// <c>KW-ARB-PARSE-00x</c> diagnostics. The execution digest is reported only for a plan with tasks
    /// and no dropped content, as the <c>execution-digest</c> metric in json and once on its own line in
    /// the table, so a task file can record the digest it was written from. An unknown dependency token
    /// (<c>KW-ARB-PARSE-001</c>) withholds it, since the parser drops that token; a task with no Files
    /// line (<c>KW-ARB-PARSE-002</c>) does not. A plan with no parsed task reports
    /// <c>KW-ARB-PLAN-001</c>; a missing or unreadable file reports
    /// <c>KW-ARB-PLAN-002</c> and exits 1.
    /// </summary>
    public override int Execute(CommandContext context, ArbiterSettings settings, CancellationToken cancellationToken)
    {
        DiagnosticReport report = new();
        string root = planArguments.RepositoryRootFor(settings.Path, settings.WorkingDirectory);
        if (!planArguments.TryResolve(root, PlanArguments.ConfigForResolving(root, settings.Config, configs), files, settings.Path, settings.WorkingDirectory, "arbiter plan", report, out string relative))
        {
            CommandHelpers.Finish(console, report, settings, "arbiter plan", "Task");
            return 1;
        }

        if (plans.TryRead(root, relative, settings.Path, report) is not { } read)
        {
            CommandHelpers.Finish(console, report, settings, "arbiter plan", "Task");
            return 1;
        }

        // Only json carries the metric. The table prints the digest on its own unfolded line, and a
        // metric row would print it again, truncated in a narrow cell.
        if (read.Digest is { } digest && settings.FormatFor(console) == OutputFormat.Json)
        {
            report.AddMetric("execution-digest", digest);
        }

        // The prose lines are for a person reading the table. Json, sarif and markdown must
        // carry only the rendered report, or a script reading stdout would parse a non-document.
        if (ReportRenderer.IsHuman(settings.FormatFor(console)))
        {
            Print(read);
        }

        CommandHelpers.Finish(console, report, settings, "arbiter plan", "Task");
        return report.HasErrors ? 1 : 0;
    }

    /// <summary>Runs the command without cancellation; the entry point tests use.</summary>
    public int Execute(CommandContext context, ArbiterSettings settings) =>
        Execute(context, settings, CancellationToken.None);

    private void Print(ArbiterPlanRead read)
    {
        PlanDocument document = read.Document;
        CommandHelpers.WriteUnfolded(console, () => console.MarkupLine($"[bold]Plan:[/] {Markup.Escape(read.ResolvedPath)}"));
        console.MarkupLine($"[grey]Status: {Markup.Escape(document.Status ?? "—")}; mode: {Markup.Escape(document.DevelopmentMode)}.[/]");

        if (read.Digest is { } printed)
        {
            // Written unfolded so a narrow console cannot wrap the 64 characters across lines.
            CommandHelpers.WriteUnfolded(console, () => console.MarkupLine($"Execution digest: {Markup.Escape(printed)}"));
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

            console.Write(tasks);
        }

        if (document.ContractRows.Count > 0)
        {
            console.MarkupLine(
                $"[grey]{document.ContractRows.Count} contract row(s): " +
                $"{Markup.Escape(string.Join(", ", document.ContractRows.Select(row => row.TaskId)))}.[/]");
        }

        if (document.OutOfScope.Count > 0)
        {
            console.MarkupLine(
                $"[grey]Out of scope: {Markup.Escape(string.Join(", ", document.OutOfScope))}.[/]");
        }
    }
}
