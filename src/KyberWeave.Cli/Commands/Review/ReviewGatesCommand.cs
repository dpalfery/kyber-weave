using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Review;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Review;

/// <summary>Runs the deterministic gates the host declares under <c>review.gates</c>.</summary>
public sealed class ReviewGatesCommand : Command<ReviewGatesSettings>
{
    /// <summary>Raised when the host has declared no gates to run.</summary>
    public const string NoGatesDeclared = "KW-REVIEW-020";

    /// <inheritdoc />
    public override int Execute(CommandContext context, ReviewGatesSettings settings)
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

        GateReport gates = GateRunner.Run(config.Review, root, settings.StopOnFailure);

        foreach (GateResult gate in gates.Gates)
        {
            report.Add(new Diagnostic(
                gate.Passed ? ReviewGateOutcome.Passed : ReviewGateOutcome.Failed,
                gate.Passed ? Severity.Info : gate.Blocking ? Severity.Error : Severity.Warning,
                $"{gate.Id}: {gate.Summary} ({gate.DurationMilliseconds} ms)",
                gate.Id));
        }

        report.AddMetric("gates", gates.Gates.Count);
        report.AddMetric("failed", gates.Gates.Count(g => !g.Passed));

        if (settings.Out is not null)
        {
            string outPath = Path.GetFullPath(settings.Out);
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            File.WriteAllText(outPath, ReviewJson.Write(gates));
        }

        CommandHelpers.Finish(report, settings, "review gates", "Gate");
        return gates.Gates.Any(g => g.Blocking && !g.Passed) ? 1 : 0;
    }
}

/// <summary>Rule identifiers for individual gate outcomes.</summary>
public static class ReviewGateOutcome
{
    /// <summary>A gate that succeeded.</summary>
    public const string Passed = "KW-REVIEW-021";

    /// <summary>A gate that did not succeed.</summary>
    public const string Failed = "KW-REVIEW-022";
}
