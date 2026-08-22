using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using KyberWeave.Core.CodeGraph;
using KyberWeave.Core.Configuration;

namespace KyberWeave.Core.Review;

/// <summary>One member of a duplicate cluster.</summary>
/// <param name="Name">Bare symbol name. Two members of a cluster need not share it.</param>
/// <param name="QualifiedName">Fully qualified name, when the index recorded one.</param>
/// <param name="File">Repository-relative path.</param>
/// <param name="StartLine">1-based first line of the declaration.</param>
/// <param name="EndLine">1-based last line of the declaration.</param>
public sealed record DuplicateMember(
    string Name,
    string QualifiedName,
    string File,
    int StartLine,
    int EndLine);

/// <summary>A set of symbols whose normalized bodies are identical.</summary>
/// <param name="Id">Stable identifier, derived from the body hash rather than from ordering.</param>
/// <param name="NormalizedLines">How many lines the shared body is, after normalization.</param>
/// <param name="Members">Every symbol carrying that body, ordered by file then line.</param>
public sealed record DuplicateCluster(
    string Id,
    int NormalizedLines,
    IReadOnlyList<DuplicateMember> Members);

/// <summary>The duplicates gate's result document.</summary>
/// <param name="Schema">Format identifier, for readers that must tolerate later versions.</param>
/// <param name="IndexPath">Where the CodeGraph index was looked for.</param>
/// <param name="IndexAvailable">Whether it was found and read.</param>
/// <param name="IndexModifiedUtc">
/// When the index file was last written. The index syncs behind the working tree, so a
/// consumer weighing a cluster against uncommitted edits needs to know how stale it is.
/// </param>
/// <param name="UnavailableReason">Why the index could not be read, when it could not.</param>
/// <param name="MinimumLines">The threshold this run applied.</param>
/// <param name="SymbolsConsidered">Bodies that met the threshold and were hashed.</param>
/// <param name="SymbolsUnreadable">
/// Bodies skipped because the index named a file or span the working tree no longer has.
/// Reported rather than swallowed: a high count means the index is stale, and every cluster
/// in the same run is suspect.
/// </param>
/// <param name="Clusters">Duplicate clusters, largest body first.</param>
public sealed record DuplicateReport(
    string Schema,
    string IndexPath,
    bool IndexAvailable,
    DateTime? IndexModifiedUtc,
    string? UnavailableReason,
    int MinimumLines,
    int SymbolsConsidered,
    int SymbolsUnreadable,
    IReadOnlyList<DuplicateCluster> Clusters)
{
    /// <summary>The current duplicates report format.</summary>
    public const string CurrentSchema = "kyber-weave.review-duplicates/v1";
}

/// <summary>
/// Finds symbols whose bodies are the same code written twice, from the CodeGraph index and
/// the working tree.
/// </summary>
/// <remarks>
/// <para>
/// This exists because nothing else in the chain can answer the question. JetBrains retired
/// <c>dupfinder</c>, and the inspections InspectCode still ships that touch duplication —
/// <c>DuplicatedStatements</c>, <c>RedundantOverload</c> — are scoped to one statement or one
/// method group. Neither sees across files, which is where duplication that matters lives.
/// </para>
/// <para>
/// The comparison is exact equality of normalized bodies, deliberately. A similarity
/// threshold has a false-positive rate that has to be tuned against a corpus, and a review
/// gate that cries wolf is worse than one with modest recall: its findings stop being read.
/// Exact matching has a false-positive rate of zero by construction, so the first release
/// buys precision and leaves recall on the table.
/// </para>
/// <para>
/// Normalization is conservative for the same reason. Blank lines, brace-only lines, and
/// whole-line comments are dropped, and interior whitespace is collapsed — differences no
/// reader would call a difference. Trailing comments are deliberately <em>not</em> stripped:
/// doing that safely means knowing whether a <c>//</c> sits inside a string literal, and a
/// normalizer that gets that wrong merges two bodies that differ, which is the one failure
/// mode this gate must not have.
/// </para>
/// </remarks>
public static class DuplicateDetector
{
    /// <summary>
    /// The CodeGraph node kinds whose bodies are worth comparing. Frozen so one caller
    /// cannot change what every other caller — and <see cref="Detect"/> itself — compares.
    /// </summary>
    public static ImmutableArray<string> ComparableKinds { get; } = ["method", "function"];

    /// <summary>
    /// Clusters the given symbols by normalized body. <paramref name="repoRoot"/> is the tree
    /// the index's relative paths resolve against.
    /// </summary>
    /// <remarks>
    /// <see cref="ComparableKinds"/> is applied here, not only at the call site. The CLI
    /// still enumerates by kind because that is how the CodeGraph port is queried; Detect
    /// still filters so a mixed list cannot widen the comparison.
    /// </remarks>
    public static DuplicateReport Detect(
        string repoRoot,
        IReadOnlyList<CodeGraphNode> symbols,
        ReviewDuplicates options,
        string indexPath,
        DateTime? indexModifiedUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentNullException.ThrowIfNull(symbols);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(indexPath);

        Dictionary<string, string[]?> fileCache = new(StringComparer.Ordinal);
        Dictionary<string, (int Lines, List<DuplicateMember> Members)> byBody = new(StringComparer.Ordinal);
        int considered = 0;
        int unreadable = 0;

        foreach (CodeGraphNode symbol in symbols)
        {
            if (!IsComparableKind(symbol.Kind)) continue;
            if (symbol.LineSpan == 0 || symbol.FilePath.Length == 0) continue;

            string[]? lines = ReadFile(repoRoot, symbol.FilePath, fileCache);
            if (lines is null || symbol.EndLine > lines.Length)
            {
                // The index names a file or a span the working tree does not have. That is
                // staleness, not a duplicate, and it is counted rather than guessed at.
                unreadable++;
                continue;
            }

            IReadOnlyList<string> body = Normalize(lines, symbol.StartLine, symbol.EndLine);
            if (body.Count < options.MinimumLines) continue;

            considered++;
            string key = Hash(body);

            if (!byBody.TryGetValue(key, out (int Lines, List<DuplicateMember> Members) entry))
            {
                entry = (body.Count, []);
                byBody[key] = entry;
            }

            entry.Members.Add(new DuplicateMember(
                symbol.Name,
                symbol.QualifiedName,
                symbol.FilePath,
                symbol.StartLine,
                symbol.EndLine));
        }

        List<DuplicateCluster> clusters = byBody
            .Where(pair => pair.Value.Members.Count > 1)
            .Select(pair => new DuplicateCluster(
                ClusterId(pair.Key),
                pair.Value.Lines,
                pair.Value.Members
                    .OrderBy(m => m.File, StringComparer.Ordinal)
                    .ThenBy(m => m.StartLine)
                    .ToArray()))
            .OrderByDescending(c => c.NormalizedLines)
            .ThenBy(c => c.Id, StringComparer.Ordinal)
            .ToList();

        return new DuplicateReport(
            DuplicateReport.CurrentSchema,
            indexPath,
            IndexAvailable: true,
            indexModifiedUtc,
            UnavailableReason: null,
            options.MinimumLines,
            considered,
            unreadable,
            clusters);
    }

    /// <summary>The report for a run that found no index to read.</summary>
    public static DuplicateReport Unavailable(string indexPath, string reason, ReviewDuplicates options)
    {
        ArgumentNullException.ThrowIfNull(indexPath);
        ArgumentNullException.ThrowIfNull(options);

        return new DuplicateReport(
            DuplicateReport.CurrentSchema,
            indexPath,
            IndexAvailable: false,
            IndexModifiedUtc: null,
            reason,
            options.MinimumLines,
            SymbolsConsidered: 0,
            SymbolsUnreadable: 0,
            Clusters: []);
    }

    private static bool IsComparableKind(string kind)
    {
        foreach (string comparable in ComparableKinds)
        {
            if (string.Equals(comparable, kind, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Reduces a declaration to the lines that carry meaning, dropping the first of them.
    /// </summary>
    /// <remarks>
    /// The first surviving line is the signature, and dropping it is the point: two methods
    /// with different names and identical bodies are the duplication this gate is looking
    /// for, and keeping the signature would hide exactly those.
    /// </remarks>
    private static IReadOnlyList<string> Normalize(IReadOnlyList<string> fileLines, int startLine, int endLine)
    {
        List<string> normalized = [];

        for (int i = startLine - 1; i < endLine && i < fileLines.Count; i++)
        {
            string line = fileLines[i].Trim();
            if (line.Length == 0) continue;
            if (IsCommentLine(line)) continue;
            if (line is "{" or "}" or "};") continue;

            normalized.Add(CollapseWhitespace(line));
        }

        return normalized.Count <= 1 ? [] : normalized[1..];
    }

    private static bool IsCommentLine(string trimmed) =>
        trimmed.StartsWith("//", StringComparison.Ordinal)
        || trimmed.StartsWith("/*", StringComparison.Ordinal)
        || trimmed.StartsWith('*')
        || trimmed.StartsWith('#');

    private static string CollapseWhitespace(string line)
    {
        StringBuilder builder = new(line.Length);
        bool inWhitespace = false;

        foreach (char c in line)
        {
            if (char.IsWhiteSpace(c))
            {
                inWhitespace = true;
                continue;
            }

            if (inWhitespace && builder.Length > 0) builder.Append(' ');
            inWhitespace = false;
            builder.Append(c);
        }

        return builder.ToString();
    }

    private static string Hash(IReadOnlyList<string> body)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', body)));
        return Convert.ToHexStringLower(digest);
    }

    private static string ClusterId(string hash) =>
        string.Create(CultureInfo.InvariantCulture, $"dup-{hash[..8]}");

    private static string[]? ReadFile(string repoRoot, string relativePath, Dictionary<string, string[]?> cache)
    {
        if (cache.TryGetValue(relativePath, out string[]? cached)) return cached;

        string full = Path.Combine(repoRoot, relativePath.Replace('\\', Path.DirectorySeparatorChar));
        string[]? lines = null;

        try
        {
            if (File.Exists(full)) lines = File.ReadAllLines(full);
        }
        catch (IOException)
        {
            lines = null;
        }
        catch (UnauthorizedAccessException)
        {
            lines = null;
        }

        cache[relativePath] = lines;
        return lines;
    }
}
