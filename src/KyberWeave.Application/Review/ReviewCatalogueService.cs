using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Scaffolding;
using KyberWeave.Core.Review;
using KyberWeave.Core.Review.Catalogue;

namespace KyberWeave.Application.Review;

/// <summary>Prints the catalogue <c>review:</c> block for the technologies a host declares.</summary>
/// <param name="configs">Loads the host's configuration.</param>
/// <param name="lister">Lists the repository files the project roots are detected over.</param>
/// <param name="files">Reads the files a gate's condition names.</param>
/// <param name="paths">Resolves the path the caller spelled.</param>
/// <param name="environment">Names the working directory a relative path resolves against.</param>
public sealed class ReviewCatalogueService(
    IConfigLoadService configs,
    IRepositoryFileLister lister,
    IFileSystem files,
    IPathResolver paths,
    IProcessEnvironment environment) : IReviewCatalogueService
{
    /// <inheritdoc />
    public ReviewCommandResult Run(
        ReviewCatalogueRequest request,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(output);

        DiagnosticReport report = new();
        string root;
        try
        {
            root = paths.GetFullPath(request.Path, environment.CurrentDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException
                                       or System.Security.SecurityException)
        {
            report.Add(new Diagnostic(
                IDocsHostConfiguration.ConfigLoadErrorCode,
                Severity.Error,
                $"Cannot resolve path '{request.Path}': {ex.Message}",
                "kyber-weave.yml",
                request.Path));
            return ReviewCommandResultFactory.Reported(1, report);
        }

        if (!configs.TryLoad(root, request.Config, report, out KyberWeaveConfig config, out _))
            return ReviewCommandResultFactory.Reported(1, report);

        // A host may declare a technology no template ships: `docs init` scaffolds it as stubs. Its gates are
        // reported as not shipped, not refused, because the technology is the host's own choice.
        HashSet<string> declared = new(config.Ontology.Technologies, StringComparer.OrdinalIgnoreCase);

        // Validation is case-insensitive, so slugs map to their canonical spelling before the case-sensitive family match.
        IReadOnlyList<string> technologies = (request.Technologies.Count > 0
            ? request.Technologies
            : config.Ontology.Technologies).Select(technology => CanonicalSlug(technology, declared)).ToList();

        foreach (string technology in technologies)
        {
            if (!KyberStandardsTemplates.TryGet(technology, out _) && !declared.Contains(technology))
                return ReviewCommandResultFactory.Refused(UnknownTechnologyMessage(technology));
        }

        ReviewGatesPlan plan = ReviewCataloguePlanner.Plan(
            root, technologies, lister.List(root), new GateCatalogueFacts(files.FileExists, files.ReadAllText));

        if (!plan.ShouldWrite)
        {
            foreach (string technology in plan.Rendered.NoFamily)
                output.WriteLine($"# no gates shipped for {technology}");

            return new ReviewCommandResult(0);
        }

        // Machine output goes through the output writer, as the JSON reports do: Spectre markup and width folding must
        // not rewrite the block docs init would write.
        string yaml = plan.Rendered.Yaml;
        output.Write(yaml);
        if (!yaml.EndsWith('\n'))
            output.WriteLine();

        return new ReviewCommandResult(0);
    }

    /// <summary>The canonical spelling of a known slug, so any casing the validator accepts renders.</summary>
    private static string CanonicalSlug(string technology, IReadOnlySet<string> declared)
    {
        string? known = KyberStandardsTemplates.All.FirstOrDefault(
            slug => string.Equals(slug, technology, StringComparison.OrdinalIgnoreCase));
        known ??= declared.FirstOrDefault(
            slug => string.Equals(slug, technology, StringComparison.OrdinalIgnoreCase));
        return known ?? technology;
    }

    /// <summary>
    /// The unknown-technology message for <paramref name="technology"/>, with the nearest-match
    /// hint. The hint comes from <c>StringDistance.Nearest</c>, but
    /// <see cref="KyberStandardsTemplates.Render"/> is the public surface that owns the message,
    /// so the hint is harvested from its failure rather than reimplemented where it could drift.
    /// </summary>
    private static string UnknownTechnologyMessage(string technology)
    {
        try
        {
            KyberStandardsTemplates.Render(technology, "kyber-weave", "1970-01-01");
        }
        catch (ArgumentException ex)
        {
            return ex.Message;
        }

        return $"Unknown technology '{technology}'.";
    }
}
