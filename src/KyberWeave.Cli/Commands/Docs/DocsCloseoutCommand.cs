using System.Globalization;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Closeout;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Docs;

/// <summary>
/// Does the mechanical half of a plan closeout: checks the closeout evidence the archive gate wants, then archives
/// the plan, removes its task file and updates the plan index and the archived plan inventory.
/// </summary>
public sealed class DocsCloseoutCommand(
    IPlanCloseoutService closeout,
    IRepositoryFileStore files,
    IKyberWeaveConfigRepository configs,
    TimeProvider time,
    IAnsiConsole console) : Command<DocsCloseoutSettings>
{
    /// <summary>The <c>--date</c> value is not a date.</summary>
    public const string InvalidDate = "KW-DOC-CLOSEOUT-005";

    /// <summary>The command description shared by the real registration and the help tests.</summary>
    public const string Description = "Check a finished plan's closeout evidence and, with --archive, archive it.";

    /// <summary>
    /// Reads the plan named by the positional path. Exits 0 when its closeout evidence is complete (and, with
    /// <c>--archive</c>, when it was archived); exits 1 naming each missing condition, or on a usage or I/O error.
    /// Nothing is changed unless every check passes.
    /// </summary>
    public override int Execute(CommandContext context, DocsCloseoutSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        DiagnosticReport report = new();
        DateOnly date = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        if (settings.Date is not null
            && !DateOnly.TryParseExact(settings.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
        {
            report.Add(new Diagnostic(
                InvalidDate, Severity.Error, $"--date '{settings.Date}' is not a date.", "docs closeout",
                Hint: "Write the date as yyyy-MM-dd."));
            CommandHelpers.Finish(console, report, settings, "docs closeout", "Plan");
            return 1;
        }

        string typed = settings.Path;
        string root = PlanArguments.RepositoryRootFor(typed, settings.WorkingDirectory);
        settings.Path = root;
        if (!DocsCommandComposition.TryResolveConfig(configs, settings, report, out KyberWeaveConfig config, out OntologyConfig ontology))
        {
            CommandHelpers.Finish(console, report, settings, "docs closeout", "Plan");
            return 1;
        }

        KyberWeaveConfig effective = config.WithOntology(ontology);
        if (!PlanArguments.TryResolve(root, effective, files, typed, settings.WorkingDirectory, "docs closeout", report, out string relative))
        {
            CommandHelpers.Finish(console, report, settings, "docs closeout", "Plan");
            return 1;
        }

        PlanArguments.Announce(report, settings, relative);
        PlanCloseoutOutcome outcome = closeout.Run(root, effective, relative, date, settings.Archive && !settings.DryRun);
        report.AddRange(outcome.Report.Items);
        report.AddMetric("archived", outcome.Archived);

        CommandHelpers.Finish(console, report, settings, "docs closeout", "Plan");
        return report.HasErrors ? 1 : 0;
    }
}
