using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Review;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Review;

/// <summary>
/// Computes the review verdict from the council's findings and the gate report.
/// </summary>
public sealed class ReviewVerdictCommand : Command<ReviewVerdictSettings>
{
    /// <summary>An input document that could not be read.</summary>
    public const string UnreadableInput = "KW-REVIEW-023";

    /// <summary>The computed verdict.</summary>
    public const string Verdict = "KW-REVIEW-024";

    /// <inheritdoc />
    public override int Execute(CommandContext context, ReviewVerdictSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        DiagnosticReport report = new();
        string root = Path.GetFullPath(settings.Path);

        if (!CommandHelpers.TryLoadConfig(root, settings.Config, report, out KyberWeaveConfig config))
        {
            CommandHelpers.Finish(report, settings, "review verdict", "Review");
            return 1;
        }

        if (!TryRead(settings.Findings, ReviewJson.ReadFindings, report, out FindingsReport? findings))
        {
            CommandHelpers.Finish(report, settings, "review verdict", "Review");
            return 1;
        }

        // A missing gate report is not an error: a change may legitimately be reviewed
        // before any gate exists. It does mean nothing was executed, and the absence of a
        // blocking gate result is visible to the engine rather than mistaken for a pass.
        GateReport? gates = null;
        if (settings.Gates is not null &&
            !TryRead(settings.Gates, ReviewJson.ReadGates, report, out gates))
        {
            CommandHelpers.Finish(report, settings, "review verdict", "Review");
            return 1;
        }

        ReviewOutcome outcome = VerdictEngine.Evaluate(
            findings!.ToScope(),
            findings.Findings,
            gates?.Gates ?? [],
            config.Review,
            DateOnly.FromDateTime(DateTime.UtcNow),
            gates?.Coverage);

        report.AddRange(outcome.Diagnostics);
        report.Add(new Diagnostic(
            Verdict,
            outcome.Verdict == ReviewVerdict.Approve ? Severity.Info : Severity.Error,
            $"{Describe(outcome.Verdict)} — risk {outcome.Risk.ToString().ToUpperInvariant()}, " +
            $"{outcome.Accepted.Count} finding(s) accepted, {outcome.Dropped.Count} dropped.",
            "review"));

        report.AddMetric("verdict", outcome.Verdict.ToString());
        report.AddMetric("risk", outcome.Risk.ToString());
        report.AddMetric("accepted", outcome.Accepted.Count);
        report.AddMetric("dropped", outcome.Dropped.Count);

        CommandHelpers.Finish(report, settings, "review verdict", "Review");
        return outcome.ExitCode;
    }

    private static string Describe(ReviewVerdict verdict) => verdict switch
    {
        ReviewVerdict.Approve => "APPROVE",
        ReviewVerdict.RequestChanges => "REQUEST CHANGES",
        ReviewVerdict.NeedsHuman => "NEEDS HUMAN",
        _ => verdict.ToString()
    };

    private static bool TryRead<T>(
        string? path,
        Func<string, T> read,
        DiagnosticReport report,
        out T? value)
        where T : class
    {
        value = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            report.Add(new Diagnostic(
                UnreadableInput,
                Severity.Error,
                "No findings document was given. Pass --findings <path>.",
                "review"));
            return false;
        }

        string full = Path.GetFullPath(path);
        if (!File.Exists(full))
        {
            report.Add(new Diagnostic(
                UnreadableInput,
                Severity.Error,
                $"Input document not found: {full}",
                "review",
                full));
            return false;
        }

        try
        {
            value = read(File.ReadAllText(full));
            return true;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException)
        {
            report.Add(new Diagnostic(
                UnreadableInput,
                Severity.Error,
                $"Could not read '{full}': {ex.Message}",
                "review",
                full));
            return false;
        }
    }
}
