using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter.Plans;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.FileSystem;

namespace KyberWeave.Application.Arbiter;

/// <summary>Reads one plan file and parses it, for the <c>arbiter plan</c> listing.</summary>
/// <param name="files">Reads the plan file.</param>
/// <param name="paths">Resolves the plan's path against the repository root.</param>
public sealed class ArbiterPlanService(IFileSystem files, IPathResolver paths) : IArbiterPlanService
{
    /// <inheritdoc />
    public ArbiterPlanRead? TryRead(
        string root,
        string relative,
        string argument,
        DiagnosticReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        string resolved = paths.GetFullPath(relative, root);
        if (!files.FileExists(resolved))
        {
            report.Add(new Diagnostic(
                IArbiterPlanService.PlanNotReadable,
                Severity.Error,
                $"Plan file '{argument}' does not exist.",
                argument,
                Hint: "Pass the plan file to print, for example 'arbiter plan docs/plans/plan.md'."));
            return null;
        }

        string text;
        try
        {
            text = files.ReadAllText(resolved);
        }
        catch (Exception exception) when (FileFailure.Is(exception))
        {
            report.Add(new Diagnostic(
                IArbiterPlanService.PlanNotReadable,
                Severity.Error,
                $"Plan file '{resolved}' could not be read: {exception.Message}",
                argument));
            return null;
        }

        PlanDocument document = PlanDocumentParser.Parse(text);
        report.AddRange(document.Diagnostics);

        // Fail closed: a digest is withheld when the parser dropped content. KW-ARB-PARSE-001 (an unknown
        // dependency token) is removed from the parse, so a digest over it would describe a plan that does
        // not say what it says. KW-ARB-PARSE-002 (a task with no Files line) is a completeness finding: the
        // task keeps its text, dependencies and contract, so its digest is offered. The findings are
        // warnings, so HasErrors does not catch them; the code check does. An empty Tasks section has
        // HasTasks but no task to digest.
        bool droppedContent = document.Diagnostics.Any(finding => finding.Code == PlanDocumentParser.UnknownDependencyRule);
        string? digest = !droppedContent && document.HasTasks && document.Tasks.Count > 0
            ? PlanDocumentParser.ExecutionDigest(document)
            : null;

        // Zero parsed tasks gets the finding whether or not a Tasks heading exists. Either way there is
        // no execution, and the operator needs the finding to learn why no digest is printed.
        if (document.Tasks.Count == 0)
        {
            report.Add(new Diagnostic(
                IArbiterPlanService.NoTasks,
                Severity.Warning,
                document.HasTasks
                    ? $"Plan '{resolved}' has a Tasks section, but no task in it parsed."
                    : $"Plan '{resolved}' carries no Tasks section.",
                argument,
                Hint: "Add T1, T2, ... headings under '## Tasks' (adding the section if missing), or a spec-grammar checklist; without one, readiness answers no-tasks."));
        }

        return new ArbiterPlanRead(resolved, document, digest);
    }
}
