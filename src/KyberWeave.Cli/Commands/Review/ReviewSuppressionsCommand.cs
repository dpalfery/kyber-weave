using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Review;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Review;

/// <summary>
///     Refuses a warning suppression the change adds unless it states a reason, and leaves the suppressions that
///     were already in the file alone.
/// </summary>
/// <remarks>
/// <para>
///     This repository asks for "a reason you can state" rather than banning suppressions, and 21 files already
///     carry one. So the gate judges added lines only: a change that leaves a committed suppression untouched
///     passes, and a change that adds one without saying why fails with the file and line named.
/// </para>
/// <para>
///     Exit codes follow the review branch: <c>0</c> when every added suppression states a reason or the change
///     adds none, <c>1</c> when at least one does not or the change could not be read, and <c>2</c> for an option
///     value that is refused before anything runs. An unreadable change is a failure rather than a pass, because a
///     gate that cannot see the change has no evidence either way.
/// </para>
/// </remarks>
public sealed class ReviewSuppressionsCommand : Command<ReviewSuppressionsSettings>
{
    /// <summary>A suppression the change added without stating a reason.</summary>
    public const string UnreasonedSuppression = "KW-REVIEW-036";

    /// <summary>The change could not be read, so no line was scanned.</summary>
    public const string ChangeUnreadable = "KW-REVIEW-037";

    /// <summary>An option value the command refuses before it reads anything.</summary>
    private const string InvalidInput = "KW-REVIEW-028";

    private const int ClientInputExitCode = 2;

    /// <inheritdoc />
    public override int Execute(
        CommandContext context, ReviewSuppressionsSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        DiagnosticReport report = new();
        string root;
        try
        {
            root = Path.GetFullPath(settings.Path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException
                                       or System.Security.SecurityException)
        {
            report.Add(new Diagnostic(
                ChangeUnreadable,
                Severity.Error,
                $"Cannot resolve path '{settings.Path}': {ex.Message}",
                "review suppressions",
                settings.Path));
            CommandHelpers.Finish(report, settings, "review suppressions", "Suppression");
            return 1;
        }

        string baseRef = settings.BaseRef ?? SuppressionScan.DefaultBase;
        if (ChangedFiles.ValidateBase(baseRef) is { } baseProblem)
        {
            WriteClientInputError($"{InvalidInput}: {baseProblem}");
            return ClientInputExitCode;
        }

        SuppressionScanResult result = SuppressionScan.Scan(root, baseRef);

        if (result.Failure is { } failure)
        {
            report.Add(new Diagnostic(
                ChangeUnreadable,
                Severity.Error,
                $"Nothing was scanned: the change against '{baseRef}' could not be read ({failure}).",
                "review suppressions",
                Hint: "Run this inside a git repository that already has a commit, or name the ref to compare " +
                      "with using --base."));
            CommandHelpers.Finish(report, settings, "review suppressions", "Suppression");
            return 1;
        }

        foreach (SuppressionFinding finding in result.Findings)
        {
            report.Add(new Diagnostic(
                UnreasonedSuppression,
                Severity.Error,
                $"{finding.Path}:{finding.Line}: the added {finding.Form} states no reason.",
                finding.Path,
                finding.Path,
                Hint: $"Say why the suppression is needed: put '{SuppressionScan.ReasonToken} <why>' on the same " +
                      $"line or the line above. The line reads: {finding.Excerpt}",
                StartLine: finding.Line,
                EndLine: finding.Line));
        }

        CommandHelpers.Finish(report, settings, "review suppressions", "Suppression");
        return report.HasErrors ? 1 : 0;
    }

    /// <summary>
    /// Client-input errors go through <see cref="Console"/> rather than Spectre markup, so a ref or a path
    /// containing brackets is printed as written instead of being read as markup.
    /// </summary>
    private static void WriteClientInputError(string message)
    {
        Console.WriteLine($"kyber-weave review suppressions: error: {message}");
    }
}
