using System.Text.RegularExpressions;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Review.Catalogue;
using YamlDotNet.Core;

namespace KyberWeave.Core.Docs.Scaffolding;

/// <summary>
/// What <c>docs init</c> decided about the catalogue review gates: whether it wrote them,
/// left a host-owned block alone, or stayed out entirely.
/// </summary>
/// <param name="OptedOut">True when the operator passed <c>--no-review-gates</c>.</param>
/// <param name="AlreadyPresent">True when the host configuration already had a <c>review:</c> section.</param>
/// <param name="Written">True when this run appended the rendered block.</param>
/// <param name="WrittenGates">Each live gate written, as the technology it serves and its final id.</param>
/// <param name="CommentedOutGates">Each gate written commented out, with the reason it is not live.</param>
/// <param name="NoFamily">Declared technologies with no catalogue family, so no gates ship for them.</param>
/// <param name="MissingFromExisting">
/// Rendered gate ids absent from a host-owned <c>review:</c> section, reported as information only.
/// </param>
public sealed record ReviewGatesOutcome(
    bool OptedOut,
    bool AlreadyPresent,
    bool Written,
    IReadOnlyList<(string Technology, string GateId)> WrittenGates,
    IReadOnlyList<(string GateId, string Reason)> CommentedOutGates,
    IReadOnlyList<string> NoFamily,
    IReadOnlyList<string> MissingFromExisting);

/// <summary>
/// The rendered block <c>docs init</c> may write, and whether it may write it at all.
/// </summary>
/// <param name="Rendered">The catalogue render for the declared technologies.</param>
/// <param name="ShouldWrite">
/// False when no declared technology has a catalogue entry; there is then no block to write.
/// </param>
public sealed record ReviewGatesPlan(RenderedReviewBlock Rendered, bool ShouldWrite);

/// <summary>
/// Appends the rendered catalogue <c>review:</c> block to the host configuration, or reports
/// why it wrote nothing.
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
public static partial class ReviewBlockWriter
{
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
    public static ReviewGatesPlan Plan(string repoRoot, IReadOnlyList<string> declaredTechnologies)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentNullException.ThrowIfNull(declaredTechnologies);

        IReadOnlyList<GateCatalogueFamily> families = GateCatalogue.LoadEmbedded();

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

        Dictionary<string, ProjectRootSet> rootsByFamily = new(StringComparer.Ordinal);
        foreach (GateCatalogueFamily family in families)
        {
            rootsByFamily[family.Family] = ProjectRootDetector.Detect(
                repoRoot, [.. family.Markers], family.Nesting, family.Pair, family.Fallback?.ToArray());
        }

        RenderedReviewBlock rendered = GateCatalogue.Render(declaredTechnologies, rootsByFamily, repoRoot);
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
    public static string AppendBlock(string configText, RenderedReviewBlock block, string repoRoot)
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
    public static void ValidateFinalText(string finalText, string repoRoot, RenderedReviewBlock block)
    {
        ArgumentNullException.ThrowIfNull(finalText);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentNullException.ThrowIfNull(block);

        try
        {
            KyberWeaveYamlDocument document = KyberWeaveYamlParser.Parse(finalText);
            ReviewConfigLoader.Merge(
                ReviewConfig.ProductDefaults,
                document.Review,
                repoRoot,
                ReviewConfigLoader.UnknownKeyWarnings(finalText));
        }
        catch (Exception ex) when (ex is YamlException or InvalidDataException)
        {
            string gate = block.Written.Count > 0
                ? block.Written[0].GateId
                : block.CommentedOut.Count > 0
                    ? block.CommentedOut[0].GateId
                    : "review";
            throw new InvalidDataException(
                $"{KyberWeaveConfigLoader.ConfigLoadErrorCode}: The rendered review gates do not load " +
                $"so nothing was written (gate '{gate}'): {ex.Message}",
                ex);
        }
    }

    /// <summary>
    /// The rendered gate ids absent from the host-owned <c>review:</c> section, live gates
    /// first in render order, each listed once. Reported as information; the exit code is unchanged.
    /// </summary>
    public static IReadOnlyList<string> MissingGateIds(ReviewConfig? hostReview, RenderedReviewBlock rendered)
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
