using System.Globalization;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Closeout;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Docs;

/// <summary>
/// Does the mechanical half of finalizing an approved Draft plan: records the approval, sets the plan and its index
/// row to Ready and writes its task file. The same step the docs server's finalize tool runs.
/// </summary>
public sealed class DocsFinalizeCommand(
    IPlanFinalizeService finalize,
    IRepositoryFileStore files,
    IKyberWeaveConfigRepository configs,
    TimeProvider time,
    IAnsiConsole console,
    PlanArguments plans) : Command<DocsFinalizeSettings>
{
    /// <summary>A <c>--date</c> or <c>--approval-mode</c> value is not usable.</summary>
    public const string InvalidOption = "KW-DOC-FINALIZE-007";

    /// <summary>The command description shared by the real registration and the help tests.</summary>
    public const string Description = "Finalize an approved Draft plan: record the approval, set it to Ready and write its task file.";

    /// <summary>
    /// Reads the plan named by the positional path. Exits 0 when it was finalized or was already Ready with a current
    /// task file; exits 1 naming what is wrong otherwise, having changed nothing.
    /// </summary>
    public override int Execute(CommandContext context, DocsFinalizeSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        DiagnosticReport report = new();
        DateOnly date = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        if (settings.Date is not null
            && !DateOnly.TryParseExact(settings.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
        {
            return Fail(report, settings, $"--date '{settings.Date}' is not a date.", "Write the date as yyyy-MM-dd.");
        }

        if (!PlanFinalize.TryParseMode(settings.ApprovalMode, out PlanApprovalMode mode))
        {
            return Fail(report, settings, $"--approval-mode '{settings.ApprovalMode}' is not 'autonomous' or 'supervised'.",
                "Pass --approval-mode autonomous or --approval-mode supervised.");
        }

        string typed = settings.Path;
        string root = plans.RepositoryRootFor(typed, settings.WorkingDirectory);
        settings.Path = root;
        if (!DocsCommandComposition.TryResolveConfig(configs, settings, report, out KyberWeaveConfig config, out OntologyConfig ontology))
        {
            CommandHelpers.Finish(console, report, settings, "docs finalize", "Plan");
            return 1;
        }

        KyberWeaveConfig effective = config.WithOntology(ontology);
        if (!plans.TryResolve(root, effective, files, typed, settings.WorkingDirectory, "docs finalize", report, out string relative))
        {
            CommandHelpers.Finish(console, report, settings, "docs finalize", "Plan");
            return 1;
        }

        plans.Announce(report, settings, relative);
        PlanFinalizeOutcome outcome = finalize.Run(root, effective, relative, mode, settings.Approval, date);
        report.AddRange(outcome.Report.Items);
        report.AddMetric("finalized", outcome.Finalized);
        report.AddMetric("task-file", outcome.TaskFilePath);

        CommandHelpers.Finish(console, report, settings, "docs finalize", "Plan");
        return report.HasErrors ? 1 : 0;
    }

    private int Fail(DiagnosticReport report, DocsFinalizeSettings settings, string message, string hint)
    {
        report.Add(new Diagnostic(InvalidOption, Severity.Error, message, "docs finalize", Hint: hint));
        CommandHelpers.Finish(console, report, settings, "docs finalize", "Plan");
        return 1;
    }
}
