using System.Text.RegularExpressions;
using KyberWeave.Core.Text;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace KyberWeave.Core.Parsing;

/// <summary>
/// Controls how inline paths in Markdown text are scanned for file references.
/// </summary>
public enum InlinePathScanMode
{
    /// <summary>
    /// Matches only complete inline code spans (e.g. `references/guide.md`).
    /// </summary>
    CodeInlineOnly,

    /// <summary>
    /// Matches target subdirectory paths anywhere in unfenced body text.
    /// </summary>
    UnfencedText
}

/// <summary>
/// Options configuring file reference extraction, normalization, and validation.
/// </summary>
public sealed class FileReferenceOptions
{
    /// <summary>
    /// The inline path scanning mode.
    /// </summary>
    public InlinePathScanMode InlineScanMode { get; init; } = InlinePathScanMode.CodeInlineOnly;

    /// <summary>
    /// Whether to skip paths containing directory traversal ("..").
    /// </summary>
    public bool SkipPathTraversal { get; init; } = true;

    /// <summary>
    /// Whether to skip configuration registry tokens (e.g. &lt;docs-root&gt;).
    /// </summary>
    public bool SkipConfigRegTokens { get; init; } = true;

    /// <summary>
    /// Whether to skip foreign and absolute filesystem paths (e.g. C:/, /etc, UNC).
    /// </summary>
    public bool SkipForeignAbsolutePaths { get; init; } = true;

    /// <summary>
    /// Whether to strip URI fragment anchors (#...) before disk resolution.
    /// </summary>
    public bool StripFragments { get; init; } = true;

    /// <summary>
    /// Default options for agent specification validation.
    /// </summary>
    public static FileReferenceOptions AgentDefault { get; } = new()
    {
        InlineScanMode = InlinePathScanMode.CodeInlineOnly,
        SkipPathTraversal = true,
        SkipConfigRegTokens = true,
        SkipForeignAbsolutePaths = true,
        StripFragments = true
    };

    /// <summary>
    /// Default options for skill definition parsing.
    /// </summary>
    public static FileReferenceOptions SkillDefault { get; } = new()
    {
        InlineScanMode = InlinePathScanMode.UnfencedText,
        SkipPathTraversal = false,
        SkipConfigRegTokens = true,
        SkipForeignAbsolutePaths = true,
        StripFragments = true
    };
}

/// <summary>
/// Represents a file reference extracted from Markdown text and resolved against an owning directory.
/// </summary>
/// <param name="Reference">The normalized relative path reference.</param>
/// <param name="ResolvedFullPath">The absolute file path on disk, or null if unresolvable.</param>
/// <param name="Exists">True when the resolved path exists on disk as a file or directory.</param>
/// <param name="IsPathTraversal">True when the reference attempted directory traversal outside the owning directory.</param>
/// <param name="NearestMatch">The nearest existing matching path relative to the owning directory, or null if none found.</param>
public readonly record struct ExtractedFileReference(
    string Reference,
    string? ResolvedFullPath,
    bool Exists,
    bool IsPathTraversal,
    string? NearestMatch
);

/// <summary>
/// Extracts and validates file references from Markdown documents and text spans.
/// </summary>
public static partial class FileReferenceExtractor
{
    private static readonly char[] PathSeparators = ['/', '\\'];

    [GeneratedRegex(@"\A(?<path>(?:\./)?(?:scripts|references|assets)/[A-Za-z0-9._\-/]+)\z", RegexOptions.Compiled)]
    internal static partial Regex FullInlineSpanRegex();

    [GeneratedRegex(@"(?<path>(?:\./)?(?:scripts|references|assets)/[A-Za-z0-9._\-/]+)", RegexOptions.Compiled)]
    internal static partial Regex UnfencedPathRegex();

    [GeneratedRegex(@"\A(?<path>(?:\./)?[A-Za-z0-9._\-/]+/[A-Za-z0-9._\-]+\.[A-Za-z0-9]+)\z", RegexOptions.Compiled)]
    private static partial Regex RelativePathSpanRegex();

    /// <summary>
    /// Extracts file references from a single Markdown text string.
    /// </summary>
    public static IReadOnlyList<ExtractedFileReference> ExtractFromText(
        string text,
        string directoryPath,
        FileReferenceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(directoryPath);

        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        MarkdownDocument document = Markdown.Parse(text);
        return ExtractFromDocument(document, text, directoryPath, options);
    }

    /// <summary>
    /// Extracts file references from multiple Markdown text strings.
    /// </summary>
    public static IReadOnlyList<ExtractedFileReference> ExtractFromTexts(
        IEnumerable<string> texts,
        string directoryPath,
        FileReferenceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(texts);
        ArgumentNullException.ThrowIfNull(directoryPath);

        options ??= FileReferenceOptions.AgentDefault;
        StringComparer comparer = GetPathComparer(directoryPath);
        Dictionary<string, ExtractedFileReference> results = new(comparer);

        foreach (string text in texts)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            MarkdownDocument document = Markdown.Parse(text);
            ExtractInternal(document, text, directoryPath, options, results, comparer);
        }

        return results.Values.ToArray();
    }

    /// <summary>
    /// Extracts file references from an existing parsed Markdig <see cref="MarkdownDocument"/>.
    /// </summary>
    public static IReadOnlyList<ExtractedFileReference> ExtractFromDocument(
        MarkdownDocument document,
        string rawMarkdown,
        string directoryPath,
        FileReferenceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(directoryPath);

        options ??= FileReferenceOptions.AgentDefault;
        StringComparer comparer = GetPathComparer(directoryPath);
        Dictionary<string, ExtractedFileReference> results = new(comparer);

        ExtractInternal(document, rawMarkdown, directoryPath, options, results, comparer);

        return results.Values.ToArray();
    }

    private static void ExtractInternal(
        MarkdownDocument document,
        string rawMarkdown,
        string directoryPath,
        FileReferenceOptions options,
        Dictionary<string, ExtractedFileReference> results,
        StringComparer comparer)
    {
        foreach (LinkInline link in document.Descendants<LinkInline>())
        {
            if (link.Url is { } url)
            {
                string targetUrl = url;
                if (!string.IsNullOrWhiteSpace(rawMarkdown) &&
                    (rawMarkdown.Contains("(<" + url + ">", StringComparison.Ordinal) ||
                     rawMarkdown.Contains("(<" + url + "#", StringComparison.Ordinal) ||
                     rawMarkdown.Contains("<" + url + ">", StringComparison.Ordinal)))
                {
                    targetUrl = "<" + url + ">";
                }

                ProcessReference(targetUrl, directoryPath, options, results, comparer);
            }
        }

        if (options.InlineScanMode == InlinePathScanMode.CodeInlineOnly)
        {
            foreach (CodeInline code in document.Descendants<CodeInline>())
            {
                Match match = FullInlineSpanRegex().Match(code.Content);
                if (match.Success)
                {
                    ProcessReference(match.Groups["path"].Value, directoryPath, options, results, comparer);
                }
                else
                {
                    Match relMatch = RelativePathSpanRegex().Match(code.Content);
                    if (relMatch.Success)
                    {
                        ProcessReference(relMatch.Groups["path"].Value, directoryPath, options, results, comparer);
                    }
                }
            }
        }
        else if (options.InlineScanMode == InlinePathScanMode.UnfencedText && !string.IsNullOrWhiteSpace(rawMarkdown))
        {
            foreach (Match match in UnfencedPathRegex().Matches(rawMarkdown))
            {
                ProcessReference(match.Groups["path"].Value, directoryPath, options, results, comparer);
            }
        }
    }

    private static void ProcessReference(
        string target,
        string directoryPath,
        FileReferenceOptions options,
        Dictionary<string, ExtractedFileReference> results,
        StringComparer comparer)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        string trimmed = target.Trim();

        // Skip URLs, anchors, and mailto:
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith('#') ||
            trimmed.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Skip config tokens (e.g. <docs-root>)
        if (options.SkipConfigRegTokens && trimmed.StartsWith('<') && trimmed.EndsWith('>'))
        {
            return;
        }

        string normalized = trimmed;

        // Strip fragment (anchor) suffix if requested
        if (options.StripFragments)
        {
            int fragmentIndex = normalized.IndexOf('#', StringComparison.Ordinal);
            if (fragmentIndex >= 0)
            {
                normalized = normalized.Substring(0, fragmentIndex);
            }
        }

        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        // Strip leading ./
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized.Substring(2);
        }

        // Skip foreign and absolute filesystem paths
        if (options.SkipForeignAbsolutePaths && (
            Path.IsPathRooted(normalized) ||
            (normalized.Length >= 2 && char.IsAsciiLetter(normalized[0]) && normalized[1] == ':') ||
            normalized.StartsWith('\\') ||
            normalized.StartsWith("//", StringComparison.Ordinal)))
        {
            return;
        }

        // Handle directory traversal ("..")
        if (normalized.Contains("..", StringComparison.Ordinal))
        {
            if (options.SkipPathTraversal)
            {
                return;
            }

            if (!results.ContainsKey(normalized))
            {
                results[normalized] = new ExtractedFileReference(
                    Reference: normalized,
                    ResolvedFullPath: null,
                    Exists: false,
                    IsPathTraversal: true,
                    NearestMatch: null);
            }

            return;
        }

        // Deduplicate
        if (results.ContainsKey(normalized))
        {
            return;
        }

        bool exists = ResolvesOnDisk(directoryPath, normalized, comparer);
        string? resolvedFullPath = null;

        try
        {
            resolvedFullPath = Path.GetFullPath(Path.Combine(directoryPath, normalized));
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            resolvedFullPath = normalized;
        }

        string? nearestMatch = !exists ? FindNearestMatch(directoryPath, normalized) : null;

        results[normalized] = new ExtractedFileReference(
            Reference: normalized,
            ResolvedFullPath: resolvedFullPath,
            Exists: exists,
            IsPathTraversal: false,
            NearestMatch: nearestMatch);
    }

    /// <summary>
    /// Checks if a relative path resolves to an existing file or directory on disk,
    /// verifying that it does not escape the directory and respects filesystem case sensitivity.
    /// </summary>
    public static bool ResolvesOnDisk(string directoryPath, string relativePath, StringComparer? comparer = null)
    {
        try
        {
            while (relativePath.StartsWith("./", StringComparison.Ordinal))
            {
                relativePath = relativePath.Substring(2);
            }

            string fullPath = Path.GetFullPath(Path.Combine(directoryPath, relativePath));
            string baseFull = Path.GetFullPath(directoryPath);

            string basePrefix = baseFull.EndsWith(Path.DirectorySeparatorChar)
                ? baseFull
                : baseFull + Path.DirectorySeparatorChar;

            if (!fullPath.Equals(baseFull, StringComparison.Ordinal) &&
                !fullPath.StartsWith(basePrefix, StringComparison.Ordinal))
            {
                return false;
            }

            if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
            {
                return false;
            }

            comparer ??= GetPathComparer(directoryPath);

            if (comparer == StringComparer.Ordinal)
            {
                string currentDir = baseFull;
                string[] segments = relativePath.Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries);

                foreach (string segment in segments)
                {
                    if (segment == ".")
                    {
                        continue;
                    }

                    if (!Directory.Exists(currentDir))
                    {
                        return false;
                    }

                    bool matched = false;
                    foreach (string entry in Directory.EnumerateFileSystemEntries(currentDir))
                    {
                        if (string.Equals(Path.GetFileName(entry), segment, StringComparison.Ordinal))
                        {
                            matched = true;
                            currentDir = entry;
                            break;
                        }
                    }

                    if (!matched)
                    {
                        return false;
                    }
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or PathTooLongException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Uses an existing directory entry to probe case lookup without writing to the directory.
    /// </summary>
    /// <remarks>
    /// Probes case sensitivity by checking if an existing file or directory can be resolved with inverted casing.
    /// Falls back to OS default if probing is impossible (e.g. empty directory tree or access denied).
    /// </remarks>
    public static StringComparer GetPathComparer(string directoryPath)
    {
        try
        {
            DirectoryInfo? directory = new DirectoryInfo(Path.GetFullPath(directoryPath));
            while (directory is not null)
            {
                if (Directory.Exists(directory.FullName))
                {
                    string[] entries = Directory.EnumerateFileSystemEntries(directory.FullName).ToArray();
                    foreach (string entry in entries)
                    {
                        if (!File.Exists(entry) && !Directory.Exists(entry))
                        {
                            continue;
                        }

                        string name = Path.GetFileName(entry);
                        int letterIndex = 0;
                        while (letterIndex < name.Length && !char.IsAsciiLetter(name[letterIndex]))
                        {
                            letterIndex++;
                        }

                        if (letterIndex == name.Length)
                        {
                            continue;
                        }

                        char[] alternateName = name.ToCharArray();
                        alternateName[letterIndex] = char.IsUpper(alternateName[letterIndex])
                            ? char.ToLowerInvariant(alternateName[letterIndex])
                            : char.ToUpperInvariant(alternateName[letterIndex]);
                        string alternate = new string(alternateName);
                        string alternatePath = Path.Combine(directory.FullName, alternate);

                        if (!File.Exists(alternatePath) && !Directory.Exists(alternatePath))
                        {
                            return StringComparer.Ordinal;
                        }

                        return entries.Any(candidate =>
                            string.Equals(Path.GetFileName(candidate), alternate, StringComparison.Ordinal))
                            ? StringComparer.Ordinal
                            : StringComparer.OrdinalIgnoreCase;
                    }
                }

                directory = directory.Parent;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Keep validating when the filesystem cannot be probed.
        }

        return OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    }

    /// <summary>
    /// Finds the nearest existing file or directory match to a broken reference within the specified directory.
    /// </summary>
    /// <remarks>
    /// Searches only the referenced subdirectory within <paramref name="directoryPath"/>,
    /// ensuring no suggestions escape the parent directory boundary.
    /// Computes similarity using <see cref="StringDistance.Levenshtein"/>.
    /// </remarks>
    public static string? FindNearestMatch(string directoryPath, string brokenReference)
    {
        string referenceName = Path.GetFileName(brokenReference);
        if (string.IsNullOrWhiteSpace(referenceName))
        {
            return null;
        }

        List<(string path, int distance)> candidates = new();

        try
        {
            string? relativeDirectory = Path.GetDirectoryName(brokenReference);
            string searchDirectory = Path.GetFullPath(Path.Combine(directoryPath, relativeDirectory ?? string.Empty));
            string baseFull = Path.GetFullPath(directoryPath);

            string basePrefix = baseFull.EndsWith(Path.DirectorySeparatorChar)
                ? baseFull
                : baseFull + Path.DirectorySeparatorChar;

            if (!searchDirectory.Equals(baseFull, StringComparison.Ordinal) &&
                !searchDirectory.StartsWith(basePrefix, StringComparison.Ordinal))
            {
                return null;
            }

            if (!Directory.Exists(searchDirectory))
            {
                return null;
            }

            foreach (string file in Directory.EnumerateFileSystemEntries(searchDirectory))
            {
                string fileName = Path.GetFileName(file);
                int distance = StringDistance.Levenshtein(referenceName, fileName);
                string relPath = Path.GetRelativePath(directoryPath, file).Replace('\\', '/');
                candidates.Add((relPath, distance));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (candidates.Count > 0)
        {
            (string path, int distance) best = candidates
                .OrderBy(static c => c.distance)
                .ThenBy(static c => c.path, StringComparer.Ordinal)
                .FirstOrDefault();

            if (best.distance <= 3)
            {
                return best.path;
            }
        }

        return null;
    }
}
