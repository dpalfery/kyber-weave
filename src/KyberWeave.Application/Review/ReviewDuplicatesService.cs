using KyberWeave.Cli.Commands.Review;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.CodeGraph;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Review;
using KyberWeave.Domain.CodeGraph;

namespace KyberWeave.Application.Review;

/// <summary>Finds symbols whose bodies are duplicated across the tree, from the CodeGraph index.</summary>
/// <param name="detector">Compares the indexed symbol bodies.</param>
/// <param name="graphs">Reads the CodeGraph index the comparison runs over.</param>
/// <param name="configs">Loads the host's configuration.</param>
/// <param name="files">Writes the duplicates report.</param>
/// <param name="paths">Resolves the path the caller spelled.</param>
/// <param name="environment">Names the working directory a relative path resolves against.</param>
public sealed class ReviewDuplicatesService(
    IDuplicateDetectionService detector,
    ICodeGraphPortsFactory graphs,
    IConfigLoadService configs,
    IWritableFileSystem files,
    IPathResolver paths,
    IProcessEnvironment environment) : IReviewDuplicatesService
{
    /// <inheritdoc />
    public ReviewCommandResult Run(ReviewDuplicatesRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        DiagnosticReport report = new();
        string root = paths.GetFullPath(request.Path, environment.CurrentDirectory);

        if (!configs.TryLoad(root, request.Config, report, out KyberWeaveConfig config, out _))
            return ReviewCommandResultFactory.Reported(1, report);

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

        ReviewReportWriter.TryWrite(
            files,
            paths,
            environment,
            request.Out,
            ReviewJson.Write(duplicates),
            ReviewGateReporting.ReportWriteFailed,
            report);

        // Clusters are Warning by design (the gate proves evidence was produced). A failed
        // --out write is Error and must fail the process, or CI would treat a missing report
        // as a passed gate.
        return ReviewCommandResultFactory.Reported(report.HasErrors ? 1 : 0, report);
    }
}
