using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Scaffolding;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Review;

/// <summary>
/// Prints the catalogue <c>review:</c> block to stdout and writes no file, so gates shipped
/// in a newer release can be copied into a host configuration by hand.
/// </summary>
/// <remarks>
/// The block is the same one <c>docs init</c> would write: it is rendered by
/// <see cref="ReviewBlockWriter.Plan"/> for the declared technologies (or <c>--technology</c>),
/// which detects each family's project roots the same way. The plan only reads, so the run
/// leaves every file under the path untouched.
/// </remarks>
public sealed class ReviewCatalogueCommand : Command<ReviewCatalogueSettings>
{
    private const int ClientInputExitCode = 2;

    /// <inheritdoc />
    public override int Execute(
        CommandContext context, ReviewCatalogueSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        DiagnosticReport report = new();
        string root = Path.GetFullPath(settings.Path);

        if (!CommandHelpers.TryLoadConfig(root, settings.Config, report, out KyberWeaveConfig config))
        {
            CommandHelpers.Finish(report, settings, "review catalogue", "Gate");
            return 1;
        }

        IReadOnlyList<string> technologies = settings.Technology.Length > 0
            ? settings.Technology
            : config.Ontology.Technologies;

        foreach (string technology in technologies)
        {
            if (!KyberStandardsTemplates.TryGet(technology, out _))
            {
                WriteClientInputError(UnknownTechnologyMessage(technology));
                return ClientInputExitCode;
            }
        }

        ReviewGatesPlan plan = ReviewBlockWriter.Plan(root, technologies);

        if (!plan.ShouldWrite)
        {
            foreach (string technology in plan.Rendered.NoFamily)
            {
                Console.WriteLine($"# no gates shipped for {technology}");
            }

            return 0;
        }

        // Machine output goes through Console, as the JSON reports do: Spectre markup and
        // width folding must not rewrite the block docs init would write.
        string yaml = plan.Rendered.Yaml;
        Console.Write(yaml);
        if (!yaml.EndsWith('\n'))
        {
            Console.WriteLine();
        }

        return 0;
    }

    /// <summary>
    /// Client-input errors go through <see cref="Console"/> rather than Spectre markup, so a
    /// long line (like the unknown-technology hint) is never folded across console width and
    /// the hint stays intact for hosts that match on it.
    /// </summary>
    private static void WriteClientInputError(string message)
    {
        Console.WriteLine($"kyber-weave review catalogue: error: {message}");
    }

    /// <summary>
    /// The unknown-technology message for <paramref name="technology"/>, with the nearest-match
    /// hint. The hint lives behind the internal <c>DocSpecValidator.Nearest</c>, and
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
