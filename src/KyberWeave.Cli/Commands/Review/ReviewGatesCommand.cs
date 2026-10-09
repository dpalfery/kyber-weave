using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Review;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Review;

/// <summary>Runs the deterministic gates the host declares under <c>review.gates</c>.</summary>
public sealed class ReviewGatesCommand : Command<ReviewGatesSettings>
{
    private const string NoGatesDeclared = "KW-REVIEW-020";
    private const string ReportWriteFailed = "KW-REVIEW-025";

    /// <inheritdoc />
    protected override int Execute(CommandContext context, ReviewGatesSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        DiagnosticReport report = new();
        string root = Path.GetFullPath(settings.Path);

        if (!CommandHelpers.TryLoadConfig(root, settings.Config, report, out KyberWeaveConfig config))
        {
            CommandHelpers.Finish(report, settings, "review gates", "Gate");
            return 1;
        }

        if (config.Review.Gates.Count == 0)
        {
            // Not an error, and deliberately not silent. A review that runs no gates is a
            // review with no evidence, and the reviewer needs to know that is the situation
            // rather than reading an empty gate report as "everything passed".
            report.Add(new Diagnostic(
                NoGatesDeclared,
                Severity.Warning,
                "No gates are declared, so this review has no executed evidence behind it.",
                "review.gates",
                Hint: "Declare the repository's build, test, and analysis commands under " +
                      "review.gates in kyber-weave.yml."));
            CommandHelpers.Finish(report, settings, "review gates", "Gate");
            return 0;
        }

        GateReport gates = GateRunner.Run(
            config.Review,
            root,
            settings.StopOnFailure,
            ChangedPathsSince(root, settings.Base),
            BaseRef(settings.Base));

        foreach (GateResult gate in gates.Gates)
        {
            if (gate.NotApplicableReason is not null)
            {
                report.Add(new Diagnostic(
                    ReviewGateOutcome.NotApplicable,
                    Severity.Info,
                    $"{gate.Id}: {gate.Summary} ({gate.DurationMilliseconds} ms)",
                    gate.Id));
                continue;
            }

            report.Add(new Diagnostic(
                gate.Passed ? ReviewGateOutcome.Passed : ReviewGateOutcome.Failed,
                gate.Passed ? Severity.Info : gate.Blocking ? Severity.Error : Severity.Warning,
                $"{gate.Id}: {gate.Summary} ({gate.DurationMilliseconds} ms)",
                gate.Id));
        }

        report.AddMetric("gates", gates.Gates.Count);
        report.AddMetric("failed", gates.Gates.Count(g => !g.Passed && g.NotApplicableReason is null));
        report.AddMetric("not-applicable", gates.Gates.Count(g => g.NotApplicableReason is not null));

        CommandHelpers.TryWriteReport(settings.Out, ReviewJson.Write(gates), ReportWriteFailed, report);

        CommandHelpers.Finish(report, settings, "review gates", "Gate");
        // Blocking failures are already Error diagnostics; a failed --out write is too.
        // Non-blocking gate failures stay Warning and do not fail the process. Gates that
        // did not apply are Info and never fail it either.
        return report.HasErrors ? 1 : 0;
    }

    /// <summary>
    /// The paths the change touches: the committed diff against <paramref name="baseRef"/>
    /// together with the staged, unstaged and untracked paths. Null when no base was given,
    /// which is how the runner knows <c>applies-when</c> was not evaluated.
    /// </summary>
    private static IReadOnlyCollection<string>? ChangedPathsSince(string root, string? baseRef)
    {
        string? @base = BaseRef(baseRef);
        return @base is null ? null : GitFacts.GetChangedPathsSinceBase(root, @base);
    }

    private static string? BaseRef(string? baseRef) =>
        string.IsNullOrWhiteSpace(baseRef) ? null : baseRef.Trim();
}

/// <summary>Rule identifiers for individual gate outcomes.</summary>
public static class ReviewGateOutcome
{
    /// <summary>A gate that succeeded.</summary>
    public const string Passed = "KW-REVIEW-021";

    /// <summary>A gate that did not succeed.</summary>
    public const string Failed = "KW-REVIEW-022";

    /// <summary>A gate that does not apply to the change and was not executed.</summary>
    public const string NotApplicable = "KW-REVIEW-026";
}
