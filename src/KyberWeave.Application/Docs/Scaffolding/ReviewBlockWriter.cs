using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Review.Catalogue;
using System.Text.RegularExpressions;
using YamlDotNet.Core;

namespace KyberWeave.Core.Docs.Scaffolding;

/// <summary>
/// Renders the catalogue <c>review:</c> block for a repository's declared technologies, and
/// appends it to a host configuration, or reports why it wrote nothing.
/// </summary>
/// <remarks>
/// <para>
/// The block is appended as text, never round-tripped through a YAML emitter, for the same
/// reason <see cref="HostConfigYaml"/> edits the docs root as text: a round trip drops the
/// operator's comments, reorders keys, and loses unknown ones. Only the appended lines are
/// new; every other byte of the file is returned verbatim, in the file's own newline style.
/// </para>
/// <para>
/// The final text is validated through the review loader with the repository root before
/// anything is written, so a catalogue that cannot load fails <c>docs init</c> instead of
/// the host's first review. Validation passes the repository root (unlike the rootless
/// in-memory load) because a gate's <c>working-directory</c> is checked for existence
/// against it; without the root every nested-root gate would be refused as uncheckable.
/// </para>
/// </remarks>
public sealed partial class ReviewBlockWriter : IReviewGateCatalogue
{
    private readonly IRepositoryFileStore _files;
    private readonly IRepositoryFileLister _lister;
    private readonly IRepositoryTreeProbe _tree;

    /// <summary>Creates the writer over the file reads and repository listing it needs.</summary>
    /// <param name="files">Reads the repository root's files.</param>
    /// <param name="lister">Lists the repository's files, for project-root detection.</param>
    /// <param name="tree">Answers the path questions the review configuration parser asks.</param>
    public ReviewBlockWriter(
        IRepositoryFileStore files, IRepositoryFileLister lister, IRepositoryTreeProbe tree)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _lister = lister ?? throw new ArgumentNullException(nameof(lister));
        _tree = tree ?? throw new ArgumentNullException(nameof(tree));
    }

    /// <summary>
    /// True when <paramref name="configText"/> already carries a top-level <c>review:</c> key.
    /// The key must start in column zero, so a commented <c># review:</c> does not count.
    /// </summary>
    public static bool HasReviewSection(string configText)
    {
        ArgumentNullException.ThrowIfNull(configText);
        return ReviewKey().IsMatch(configText);
    }

    /// <summary>
    /// Renders the catalogue for <paramref name="declaredTechnologies"/>, detecting each
    /// family's project roots under <paramref name="repoRoot"/> with that family's own root
    /// rules. The test-runner choice (MTP versus VSTest) is read from the repository's
    /// <c>global.json</c> by the render itself.
    /// </summary>
    public ReviewGatesPlan Plan(string repoRoot, IReadOnlyList<string> declaredTechnologies)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentNullException.ThrowIfNull(declaredTechnologies);

        IReadOnlyList<GateCatalogueFamily> families = GateCatalogueRenderer.LoadEmbedded();

        // No declared technology maps to a family: there is nothing to detect and nothing to
        // write, so the repository is never even walked. The render would be an empty block;
        // the plan carries the no-family list instead.
        HashSet<string> familyTechnologies = new(StringComparer.Ordinal);
        foreach (GateCatalogueFamily family in families)
        {
            foreach (string technology in family.Technologies)
            {
                familyTechnologies.Add(technology);
            }
        }

        if (!declaredTechnologies.Any(familyTechnologies.Contains))
        {
            RenderedReviewBlock empty = new(
                string.Empty, [], [], [.. declaredTechnologies.Distinct(StringComparer.Ordinal)]);
            return new ReviewGatesPlan(empty, false);
        }

        IReadOnlyList<string> repositoryFiles = _lister.List(repoRoot);
        Dictionary<string, ProjectRootSet> rootsByFamily = new(StringComparer.Ordinal);
        foreach (GateCatalogueFamily family in families)
        {
            rootsByFamily[family.Family] = ProjectRootDetection.Detect(
                repositoryFiles,
                [.. family.Markers],
                family.Nesting,
                family.Pair,
                family.Fallback?.ToArray());
        }

        GateCatalogueFacts facts = new(_files.FileExists, _files.ReadAllText);
        RenderedReviewBlock rendered = GateCatalogueRenderer.Render(
            declaredTechnologies, rootsByFamily, repoRoot, facts);
        return new ReviewGatesPlan(rendered, true);
    }

    /// <summary>
    /// The rendered block with its top-level <c>review:</c> key line first and the header
    /// comment directly beneath it, so the appended section opens with the key.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>docs init</c> finds an existing section by scanning for that key in column zero,
    /// and the written section is read from the key to the end of the file. A header-first
    /// block would bury the key behind its own comments, so the key leads and the header
    /// follows it. Comments are legal directly under a mapping key, so the text still loads
    /// unchanged — and the per-gate comments, grouping, and id order are untouched.
    /// </para>
    /// </remarks>
    public static string KeyFirstYaml(RenderedReviewBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);

        string[] lines = block.Yaml.Split('\n');
        int key = Array.FindIndex(
            lines, line => string.Equals(line.TrimEnd('\r'), "review:", StringComparison.Ordinal));
        if (key < 0)
        {
            throw new InvalidOperationException(
                "The rendered review block has no top-level 'review:' key.");
        }

        string header = string.Join('\n', lines, 0, key);
        string body = string.Join('\n', lines, key + 1, lines.Length - key - 1);
        return lines[key] + "\n" + (header.Length > 0 ? header + "\n" : string.Empty) + body;
    }

    /// <summary>
    /// Returns <paramref name="configText"/> with the rendered block appended after one blank
    /// line, in the file's existing newline style. Throws before anything is written when the
    /// final text does not load.
    /// </summary>
    public string AppendBlock(string configText, RenderedReviewBlock block, string repoRoot)
    {
        ArgumentNullException.ThrowIfNull(configText);
        ArgumentNullException.ThrowIfNull(block);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);

        string keyFirst = KeyFirstYaml(block);
        string newline = configText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string normalized = newline == "\n"
            ? keyFirst
            : keyFirst.Replace("\n", newline, StringComparison.Ordinal);

        string prefix = configText;
        if (prefix.Length > 0 && !EndsWithNewline(prefix))
        {
            prefix += newline;
        }

        string finalText = prefix + newline + normalized;
        ValidateFinalText(finalText, repoRoot, block);
        return finalText;
    }

    /// <summary>
    /// Throws when <paramref name="finalText"/> does not load, so the caller writes nothing.
    /// The failure carries <c>KW-CONFIG-001</c> and names the first rendered gate, because a
    /// catalogue that cannot load is a catalogue bug the tests catch first.
    /// </summary>
    public void ValidateFinalText(string finalText, string repoRoot, RenderedReviewBlock block)
    {
        ArgumentNullException.ThrowIfNull(finalText);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentNullException.ThrowIfNull(block);

        try
        {
            KyberWeaveYamlDocument document = KyberWeaveYamlParser.Parse(finalText);
            ReviewConfigParser.Merge(
                ReviewConfig.ProductDefaults,
                document.Review,
                repoRoot,
                ReviewConfigParser.UnknownKeyWarnings(finalText),
                new ReviewWorkingDirectoryFacts(_tree.DirectoryExists, _tree.LeadsOutsideThroughSymlink));
        }
        catch (Exception ex) when (ex is YamlException or InvalidDataException)
        {
            string gate = block.Written.Count > 0
                ? block.Written[0].GateId
                : block.CommentedOut.Count > 0
                    ? block.CommentedOut[0].GateId
                    : "review";
            throw new InvalidDataException(
                $"{IDocsHostConfiguration.ConfigLoadErrorCode}: The rendered review gates do not load " +
                $"so nothing was written (gate '{gate}'): {ex.Message}",
                ex);
        }
    }

    /// <summary>
    /// The rendered gate ids absent from the host-owned <c>review:</c> section, live gates
    /// first in render order, each listed once. Reported as information; the exit code is unchanged.
    /// </summary>
    public static IReadOnlyList<string> MissingGateIds(
        ReviewConfig? hostReview, RenderedReviewBlock rendered)
    {
        ArgumentNullException.ThrowIfNull(rendered);

        HashSet<string> hostIds = new(StringComparer.Ordinal);
        if (hostReview is not null)
        {
            foreach (ReviewGate gate in hostReview.Gates)
            {
                hostIds.Add(gate.Id);
            }

            foreach (IReadOnlyList<ReviewTaskGate> gates in hostReview.TaskGates.Values)
            {
                foreach (ReviewTaskGate gate in gates)
                {
                    hostIds.Add(gate.Id);
                }
            }
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        List<string> missing = [];
        foreach ((string _, string gateId) in rendered.Written)
        {
            if (!hostIds.Contains(gateId) && seen.Add(gateId))
            {
                missing.Add(gateId);
            }
        }

        foreach ((string gateId, string _) in rendered.CommentedOut)
        {
            if (!hostIds.Contains(gateId) && seen.Add(gateId))
            {
                missing.Add(gateId);
            }
        }

        return missing;
    }

    private static bool EndsWithNewline(string text)
    {
        return text.EndsWith('\n') || text.EndsWith('\r');
    }

    [GeneratedRegex(@"^review[ \t]*:", RegexOptions.Multiline)]
    private static partial Regex ReviewKey();
}
