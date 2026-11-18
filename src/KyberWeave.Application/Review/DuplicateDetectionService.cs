using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Review;
using KyberWeave.Domain.CodeGraph;

namespace KyberWeave.Application.Review;

/// <summary>Measures declarations' bodies and reports the ones that repeat verbatim.</summary>
/// <remarks>
/// <para>
/// This exists because nothing else in the chain can answer the question. JetBrains retired
/// <c>dupfinder</c>, and the ReSharper inspections that touch duplication —
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
public sealed class DuplicateDetectionService(IContainedFileReader files, IRepositoryFileStore store) : IDuplicateDetectionService
{
    /// <inheritdoc />
    /// <remarks>
    /// Takes the two CodeGraph ports separately rather than the concrete adapter, so the symbol enumerator is consumed
    /// as a port and a fake can stand in for it.
    /// </remarks>
    public DuplicateReport Analyze(
        string repoRoot,
        ICodeGraphResolver graph,
        ICodeGraphSymbolEnumerator symbols,
        ReviewDuplicates options)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(symbols);

        if (!graph.IsAvailable)
        {
            return DuplicateDetector.Unavailable(
                graph.DatabasePath,
                graph.UnavailableReason ?? "The CodeGraph index could not be read.",
                options);
        }

        List<CodeGraphNode> comparable = DuplicateDetector.ComparableKinds
            .SelectMany(symbols.NodesOfKind)
            .ToList();

        DateTime? modified = store.FileExists(graph.DatabasePath)
            ? store.GetLastWriteTimeUtc(graph.DatabasePath)
            : null;

        return Detect(repoRoot, comparable, options, graph.DatabasePath, modified);
    }

    /// <summary>
    /// Clusters the given symbols by normalized body. <paramref name="repoRoot"/> is the tree
    /// the index's relative paths resolve against.
    /// </summary>
    /// <remarks>
    /// <see cref="DuplicateDetector.ComparableKinds"/> is applied here, not only at the call site. The CLI
    /// still enumerates by kind because that is how the CodeGraph port is queried; Detect
    /// still filters so a mixed list cannot widen the comparison.
    /// </remarks>
    public DuplicateReport Detect(
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
        List<(string Key, int Lines, DuplicateMember Member)> measured = [];
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
            measured.Add((Hash(body), body.Count, new DuplicateMember(
                symbol.Name,
                symbol.QualifiedName,
                symbol.FilePath,
                symbol.StartLine,
                symbol.EndLine)));
        }

        IReadOnlyList<DuplicateCluster> clusters = DuplicateClusters.Merge(measured);

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

    private static bool IsComparableKind(string kind)
    {
        foreach (string comparable in DuplicateDetector.ComparableKinds)
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

    private string[]? ReadFile(string repoRoot, string relativePath, Dictionary<string, string[]?> cache)
    {
        if (cache.TryGetValue(relativePath, out string[]? cached)) return cached;

        string full = Path.Combine(repoRoot, relativePath.Replace('\\', Path.DirectorySeparatorChar));
        string[]? lines = null;

        try
        {
            // The same cap every other read of a path a seat or an index names applies: an oversized file is unreadable.
            // A symbolic link on the path can lead out of the tree, so a file reached through one is unreadable here, as
            // it is for every other read of a path an index or a seat names.
            if (files.IsContainedFile(repoRoot, relativePath))
                lines = files.ReadLines(full, ReviewSeatInput.MaxFileBytes);
        }
        catch (Exception ex) when (ReviewSeatInput.IsFileFailure(ex) || ex is InvalidDataException)
        {
            lines = null;
        }

        cache[relativePath] = lines;
        return lines;
    }
}
