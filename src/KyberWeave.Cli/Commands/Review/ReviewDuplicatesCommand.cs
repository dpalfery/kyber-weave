using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.CodeGraph;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Review;
using KyberWeave.Domain.CodeGraph;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Review;

/// <summary>
/// Finds symbols whose bodies are duplicated across the tree, from the CodeGraph index.
/// </summary>
/// <remarks>
/// Declared as a gate under <c>review.gates</c> like any other. It exits 0 whether or not it
/// finds clusters, for the same reason the analyzer gate does: what a gate proves is that the
/// evidence was produced, and whether a cluster is this change's problem is the reviewing
/// council's question, not this command's. A failed <c>--out</c> write is different — the
/// evidence was not produced — and fails the process.
/// </remarks>
public sealed class ReviewDuplicatesCommand(IDuplicateDetectionService detector, ICodeGraphPortsFactory graphs) : Command<ReviewDuplicatesSettings>
{
    private const string ReportWriteFailed = "KW-REVIEW-025";

    /// <inheritdoc />
    public override int Execute(CommandContext context, ReviewDuplicatesSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        DiagnosticReport report = new();
        string root = Path.GetFullPath(settings.Path);

        if (!CommandHelpers.TryLoadConfig(root, settings.Config, report, out KyberWeaveConfig config))
        {
            CommandHelpers.Finish(report, settings, "review duplicates", "Cluster");
            return 1;
        }

        ReviewDuplicates options = config.Review.Duplicates;
        CodeGraphPorts graph = graphs.ForRepository(root);
        DuplicateReport duplicates = detector.Analyze(root, graph.Resolver, graph.Enumerator, options);

        if (!duplicates.IndexAvailable)
        {
            // Not an error, and deliberately not silent — indexing is the user's decision,
            // and the root AGENTS.md rule is to skip CodeGraph rather than demand it. What
            // must not happen is a review reading an absent check as a passed one.
            report.Add(new Diagnostic(
                ReviewDuplicateOutcome.NoIndex,
                Severity.Warning,
                $"No CodeGraph index was read, so duplicate detection did not run: {duplicates.UnavailableReason}",
                duplicates.IndexPath,
                Hint: "Run 'codegraph init' at the repository root, or drop this gate from review.gates."));
        }

        foreach (DuplicateCluster cluster in duplicates.Clusters)
        {
            DuplicateMember first = cluster.Members[0];
            string others = string.Join(
                ", ",
                cluster.Members.Skip(1).Select(m => $"{m.Name} at {m.File}:{m.StartLine}"));

            report.Add(new Diagnostic(
                ReviewDuplicateOutcome.Cluster,
                Severity.Warning,
                $"{cluster.Id}: '{first.Name}' shares a {cluster.NormalizedLines}-line body with {others}.",
                $"{first.File}:{first.StartLine}",
                Hint: "A change to one of these must be made to all of them; nothing links them."));
        }

        if (duplicates.SymbolsUnreadable > 0)
        {
            report.Add(new Diagnostic(
                ReviewDuplicateOutcome.StaleIndex,
                Severity.Warning,
                $"{duplicates.SymbolsUnreadable} indexed symbols name a file or span the working tree no longer has.",
                duplicates.IndexPath,
                Hint: "Run 'codegraph sync' — every cluster in this run was computed against a stale index."));
        }

        report.AddMetric("clusters", duplicates.Clusters.Count);
        report.AddMetric("symbols", duplicates.SymbolsConsidered);

        CommandHelpers.TryWriteReport(settings.Out, ReviewJson.Write(duplicates), ReportWriteFailed, report);

        CommandHelpers.Finish(report, settings, "review duplicates", "Cluster");
        // Clusters are Warning by design (the gate proves evidence was produced). A failed
        // --out write is Error and must fail the process, or CI would treat a missing report
        // as a passed gate.
        return report.HasErrors ? 1 : 0;
    }
}

/// <summary>Rule identifiers for duplicate-detection outcomes.</summary>
public static class ReviewDuplicateOutcome
{
    /// <summary>No CodeGraph index was available, so the gate produced no evidence.</summary>
    public const string NoIndex = "KW-REVIEW-030";

    /// <summary>A set of symbols sharing one body.</summary>
    public const string Cluster = "KW-REVIEW-031";

    /// <summary>The index disagrees with the working tree.</summary>
    public const string StaleIndex = "KW-REVIEW-032";
}
