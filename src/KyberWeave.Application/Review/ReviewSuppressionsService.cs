using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Review;

namespace KyberWeave.Application.Review;

/// <summary>Refuses a warning suppression the change adds unless it states a reason.</summary>
/// <param name="scanner">Scans the lines the change adds.</param>
/// <param name="files">Probes the repository path.</param>
/// <param name="paths">Resolves the path the caller spelled.</param>
/// <param name="environment">Names the working directory a relative path resolves against.</param>
public sealed class ReviewSuppressionsService(
    ISuppressionScanService scanner,
    IFileSystem files,
    IPathResolver paths,
    IProcessEnvironment environment) : IReviewSuppressionsService
{
    /// <inheritdoc />
    public ReviewCommandResult Run(ReviewSuppressionsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        DiagnosticReport report = new();
        string root;
        try
        {
            root = paths.GetFullPath(request.Path, environment.CurrentDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException
                                       or System.Security.SecurityException)
        {
            return ReviewCommandResultFactory.Refused($"{ReviewDiagnosticCodes.InvalidInput}: Cannot resolve path '{request.Path}': {ex.Message}");
        }

        if (!files.DirectoryExists(root))
            return ReviewCommandResultFactory.Refused($"{ReviewDiagnosticCodes.InvalidInput}: '{root}' is not a directory");

        string baseRef = request.BaseRef ?? SuppressionScan.DefaultBase;
        if (GitChangeParsing.ValidateBase(baseRef) is { } baseProblem)
            return ReviewCommandResultFactory.Refused($"{ReviewDiagnosticCodes.InvalidInput}: {baseProblem}");

        SuppressionScanResult result = scanner.Scan(root, baseRef);

        if (result.Failure is { } failure)
        {
            report.Add(new Diagnostic(
                ReviewSuppressionOutcome.ChangeUnreadable,
                Severity.Error,
                $"Nothing was scanned: the change against '{baseRef}' could not be read ({failure}).",
                "review suppressions",
                Hint: "Run this inside a git repository that already has a commit, or name the ref to compare " +
                      "with using --base."));
            return ReviewCommandResultFactory.Reported(1, report);
        }

        foreach (SuppressionFinding finding in result.Findings)
        {
            report.Add(new Diagnostic(
                ReviewSuppressionOutcome.NoReason,
                Severity.Error,
                $"{finding.Path}:{finding.Line}: the added {finding.Form} states no reason.",
                finding.Path,
                finding.Path,
                Hint: $"Say why the suppression is needed: put '{SuppressionScan.ReasonToken} <why>' on the same " +
                      $"line or the line above. The line reads: {finding.Excerpt}",
                StartLine: finding.Line,
                EndLine: finding.Line));
        }

        return ReviewCommandResultFactory.Reported(report.HasErrors ? 1 : 0, report);
    }
}
