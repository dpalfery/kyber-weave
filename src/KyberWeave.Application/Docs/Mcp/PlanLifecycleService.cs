using System.Globalization;
using System.Text;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Closeout;

namespace KyberWeave.Mcp;

/// <summary>
/// The plan lifecycle operations behind the finalize and archive tools, for one repository root: the response
/// composition over <see cref="IPlanFinalizeService"/> and <see cref="IPlanCloseoutService"/>.
/// </summary>
public sealed class PlanLifecycleService(
    string repositoryRoot,
    IDocsQueryService query,
    IDocsHostConfiguration hostConfiguration,
    IPlanFinalizeService finalize,
    IPlanCloseoutService closeout,
    IRepositoryFileStore files,
    TimeProvider timeProvider,
    IPathResolver paths,
    IProcessEnvironment environment) : IPlanLifecycleService
{
    private const string ArchiveRefusalNext =
        "next: send these findings to docs-dev as one closeout dispatch (harvest and Closeout mapping). " +
        "No other role fixes them, and no file is moved by hand. Call this tool again when docs-dev returns.";

    private readonly string _repositoryRoot = repositoryRoot ?? throw new ArgumentNullException(nameof(repositoryRoot));
    private readonly IDocsQueryService _query = query ?? throw new ArgumentNullException(nameof(query));

    /// <inheritdoc />
    public string FinalizePlan(string planFile, string approvalMode, string approval) =>
        FinalizeHere(planFile, approvalMode, approval);

    /// <inheritdoc />
    public string ArchivePlan(string planFile) => ArchiveHere(planFile);


    private DateOnly Today() => DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);

    private string FinalizeHere(string planFile, string approvalMode, string approval)
    {
        string provenance = _query.Provenance();
        DiagnosticReport report = new();
        if (!TryResolveLifecyclePlan(planFile, report, out KyberWeaveConfig config, out string plan))
            return LifecycleResponse(provenance, "refused", report, null);

        if (!PlanFinalize.TryParseMode(approvalMode, out PlanApprovalMode mode))
        {
            return LifecycleResponse(provenance, "refused", report,
                $"approvalMode '{approvalMode}' is not 'autonomous' or 'supervised'. Nothing was changed.");
        }

        PlanFinalizeOutcome outcome = finalize.Run(_repositoryRoot, config, plan, mode, approval, Today());
        report.AddRange(outcome.Report.Items);
        if (outcome.Finalized)
            return LifecycleResponse(provenance, "finalized", report, ReadyNext(outcome));
        if (outcome.AlreadyReady)
            return LifecycleResponse(provenance, "already-ready", report, ReadyNext(outcome));
        return LifecycleResponse(provenance, "refused", report, null);
    }

    private static string ReadyNext(PlanFinalizeOutcome outcome) =>
        $"next: TASK_FILE: {outcome.TaskFilePath}\nready queue (dispatch each worker by task id, to the agent its line names when it names one; it fetches its own packet):\n" +
        $"{outcome.ReadyQueue ?? "(unreadable: validate the task file)"}\ndispatch the first ready tasks now.";


    private string ArchiveHere(string planFile)
    {
        string provenance = _query.Provenance();
        DiagnosticReport report = new();
        if (!TryResolveLifecyclePlan(planFile, report, out KyberWeaveConfig config, out string plan))
            return LifecycleResponse(provenance, "refused", report, null);

        PlanCloseoutOutcome outcome = closeout.Run(_repositoryRoot, config, plan, Today(), archive: true);
        report.AddRange(outcome.Report.Items);
        return outcome.Archived
            ? LifecycleResponse(provenance, "archived", report,
                "next: call docs_validate_governed_document_schema with mergeReady true and docs_detect_drift_between_documents_and_code, with the same projectPath.")
            : LifecycleResponse(provenance, "refused", report, ArchiveRefusalNext);
    }

    private bool TryResolveLifecyclePlan(string planFile, DiagnosticReport report, out KyberWeaveConfig config, out string plan)
    {
        plan = string.Empty;
        if (!hostConfiguration.TryLoad(_repositoryRoot, report, out config))
            return false;

        if (string.IsNullOrWhiteSpace(planFile))
        {
            report.Add(new Diagnostic(PlanFinalize.NotAnActivePlan, Severity.Error, "planFile is required.", "plan"));
            return false;
        }

        PlanPathResolution resolution = new PlanFileResolver(_repositoryRoot, config, files, paths, environment).Resolve(planFile, _repositoryRoot);
        if (!resolution.Found)
        {
            string message = resolution.Ambiguous
                ? $"'{planFile}' matches several plans: {string.Join(", ", resolution.Candidates)}. Name one."
                : $"'{planFile}' names no plan under {string.Join(", ", resolution.SearchedFolders)}.";
            report.Add(new Diagnostic(PlanFinalize.NotAnActivePlan, Severity.Error, message, planFile));
            return false;
        }

        plan = resolution.Path!;
        return true;
    }

    public static string LifecycleResponse(string provenance, string result, DiagnosticReport report, string? next)
    {
        ArgumentNullException.ThrowIfNull(report);
        StringBuilder sb = new();
        sb.AppendLine(provenance);
        sb.AppendLine(CultureInfo.InvariantCulture, $"result: {result}");
        foreach (Diagnostic item in report.Items.OrderByDescending(item => item.Severity))
            sb.AppendLine(DocsToolSupport.FormatGateFinding(item));
        if (next is not null)
            sb.AppendLine(next);
        return sb.ToString().TrimEnd();
    }
}
