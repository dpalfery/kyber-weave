using System.Text.RegularExpressions;
using KyberWeave.Core.Agents.Model;
using KyberWeave.Core.Diagnostics;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace KyberWeave.Core.Agents.Validation;

/// <summary>
/// Validates individual Agent definition files against specification rules.
/// </summary>
public static partial class AgentSpecValidator
{
    public const string RuleMissingName = "KW-AGENT-SPEC-001";
    public const string RuleMissingDescription = "KW-AGENT-SPEC-002";
    public const string RuleMissingInstructions = "KW-AGENT-SPEC-003";
    public const string RuleBrokenFileReference = "KW-AGENT-SPEC-004";

    /// <remarks>
    /// Inline path regex matches backtick paths under scripts/, references/, or assets/ directories.
    /// Pattern: the entire code span contains an optional './' prefix, then the directory and
    /// path with alphanumeric/dash/slash characters.
    /// </remarks>
    private static readonly Regex InlinePathRegex = InlinePathPattern();

    public static DiagnosticReport Validate(AgentModel agent)
    {
        DiagnosticReport report = new DiagnosticReport();

        // 1. Role Name Check
        if (string.IsNullOrWhiteSpace(agent.RoleName))
        {
            report.Add(new Diagnostic(RuleMissingName, Severity.Error,
                "Agent definition must specify a non-empty name / role.",
                Path.GetFileName(agent.FilePath), agent.FilePath));
        }

        // 2. Description Check
        if (string.IsNullOrWhiteSpace(agent.Description))
        {
            report.Add(new Diagnostic(RuleMissingDescription, Severity.Warning,
                $"Agent '{agent.RoleName}' has no description — routing orchestrators may misroute prompts.",
                Path.GetFileName(agent.FilePath), agent.FilePath));
        }

        // 3. Instructions Body Check
        if (string.IsNullOrWhiteSpace(agent.InstructionsBody))
        {
            report.Add(new Diagnostic(RuleMissingInstructions, Severity.Error,
                $"Agent '{agent.RoleName}' has an empty system prompt / instructions body.",
                Path.GetFileName(agent.FilePath), agent.FilePath));
        }

        // 4. Broken File Reference Check
        ValidateBrokenFileReferences(agent, report);

        return report;
    }

    /// <summary>
    /// Validates that file references in agent description and instructions body resolve to existing files or directories.
    /// </summary>
    /// <remarks>
    /// Examines:
    /// - Markdown links (LinkInline from parsed AST)
    /// - Inline backtick paths matching the inline path regex (scripts/, references/, assets/)
    ///
    /// Skips URLs (http/https), anchors (#), mailto:, paths with '..' traversal, absolute paths, and Config Reg tokens.
    ///
    /// If DirectoryPath is null or empty, the check is skipped (cannot resolve relative paths).
    /// </remarks>
    private static void ValidateBrokenFileReferences(AgentModel agent, DiagnosticReport report)
    {
        // Skip if directory path is unavailable
        if (string.IsNullOrWhiteSpace(agent.DirectoryPath))
        {
            return;
        }

        Dictionary<string, (string source, bool exists, string? hint)> references = new(GetPathComparer(agent.DirectoryPath));

        // Extract references from description
        if (!string.IsNullOrWhiteSpace(agent.Description))
        {
            ExtractReferencesFromText(agent.Description, agent.DirectoryPath, references);
        }

        // Extract references from instructions body
        if (!string.IsNullOrWhiteSpace(agent.InstructionsBody))
        {
            ExtractReferencesFromText(agent.InstructionsBody, agent.DirectoryPath, references);
        }

        // Report diagnostics for unresolved references
        foreach (var (reference, (_, exists, hint)) in references)
        {
            if (!exists)
            {
                string resolvedPath = DescribeResolvedPath(agent.DirectoryPath, reference);
                string hintText = hint ?? "Relative file path does not exist. Check the path spelling relative to the agent definition directory.";

                report.Add(new Diagnostic(
                    RuleBrokenFileReference,
                    Severity.Error,
                    $"File reference '{reference}' does not resolve — no file at '{resolvedPath}'.",
                    agent.RoleName,
                    agent.FilePath,
                    hintText));
            }
        }
    }

    /// <summary>
    /// Resolves a reference for the diagnostic message, falling back to the reference itself when it cannot be resolved.
    /// </summary>
    private static string DescribeResolvedPath(string directoryPath, string reference)
    {
        try
        {
            return Path.GetFullPath(Path.Combine(directoryPath, reference));
        }
        catch (Exception exception) when (exception is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return reference;
        }
    }

    /// <summary>
    /// Uses an existing directory entry to probe case lookup without writing to the agent directory.
    /// </summary>
    private static StringComparer GetPathComparer(string directoryPath)
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
        catch (IOException)
        {
            // Keep validating when the filesystem cannot be probed.
        }
        catch (UnauthorizedAccessException)
        {
            // Keep validating when the filesystem cannot be probed.
        }

        return OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    }

    /// <summary>
    /// Extracts file references from Markdown text, including markdown links and inline backtick paths.
    /// </summary>
    private static void ExtractReferencesFromText(string text, string directoryPath, Dictionary<string, (string source, bool exists, string? hint)> references)
    {
        // Parse as Markdown to extract LinkInline elements
        MarkdownDocument document = Markdown.Parse(text);

        foreach (LinkInline link in document.Descendants<LinkInline>())
        {
            if (link.Url is { } url)
            {
                ConsiderReference(url, directoryPath, references);
            }
        }

        // Only complete inline-code spans can name a path.
        foreach (CodeInline code in document.Descendants<CodeInline>())
        {
            Match match = InlinePathRegex.Match(code.Content);
            if (match.Success)
            {
                ConsiderReference(match.Groups["path"].Value, directoryPath, references);
            }
        }
    }

    /// <summary>
    /// Evaluates a single reference path and adds it to the references dictionary if valid.
    /// </summary>
    private static void ConsiderReference(string target, string directoryPath, Dictionary<string, (string source, bool exists, string? hint)> references)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        // Skip URLs, anchors, mailto:, and config tokens
        if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            target.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            target.StartsWith('#') ||
            target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
            target.StartsWith('<') && target.EndsWith('>'))
        {
            return;
        }

        // Strip fragment (anchor) suffix for resolution
        string normalized = target;
        int fragmentIndex = normalized.IndexOf('#', StringComparison.Ordinal);
        if (fragmentIndex >= 0)
        {
            normalized = normalized.Substring(0, fragmentIndex);
        }

        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        // Strip leading ./
        if (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized.Substring(2);
        }

        // Skip paths with traversal attempts
        if (normalized.Contains("..", StringComparison.Ordinal))
        {
            return;
        }

        // Skip absolute paths
        if (Path.IsPathRooted(normalized) ||
            normalized.Length >= 2 && char.IsAsciiLetter(normalized[0]) && normalized[1] == ':' ||
            normalized.StartsWith('\\') ||
            normalized.StartsWith("//", StringComparison.Ordinal))
        {
            return;
        }

        // Check if already processed
        if (references.ContainsKey(normalized))
        {
            return;
        }

        // Resolve and check existence
        bool exists = ResolvesOnDisk(directoryPath, normalized);
        string? hint = !exists ? FindNearestMatch(directoryPath, normalized) : null;

        references[normalized] = ("reference", exists, hint);
    }

    /// <summary>
    /// Checks if a relative path resolves to an existing file or directory on disk.
    /// </summary>
    private static bool ResolvesOnDisk(string directoryPath, string relativePath)
    {
        try
        {
            string fullPath = Path.GetFullPath(Path.Combine(directoryPath, relativePath));
            string baseFull = Path.GetFullPath(directoryPath);

            // Prevent directory traversal escapes
            if (!fullPath.StartsWith(baseFull, StringComparison.Ordinal))
            {
                return false;
            }

            return File.Exists(fullPath) || Directory.Exists(fullPath);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Finds the nearest existing file or directory match to a broken reference using edit distance.
    /// </summary>
    /// <remarks>
    /// Searches only the referenced directory, so every suggestion stays inside the agent directory.
    /// Returns the best match or null if none found.
    /// Uses Levenshtein distance to compute similarity.
    /// </remarks>
    private static string? FindNearestMatch(string directoryPath, string brokenReference)
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

            if (!Directory.Exists(searchDirectory))
            {
                return null;
            }

            foreach (string file in Directory.EnumerateFileSystemEntries(searchDirectory))
            {
                string fileName = Path.GetFileName(file);
                int distance = LevenshteinDistance(referenceName, fileName);
                candidates.Add((Path.GetRelativePath(directoryPath, file), distance));
            }
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        // Return best match if distance is reasonable (≤ 3 edits)
        if (candidates.Count > 0)
        {
            var best = candidates.OrderBy(c => c.distance).FirstOrDefault();
            if (best.distance <= 3)
            {
                return $"Nearest match: '{best.path}' relative to the agent's directory.";
            }
        }

        return null;
    }

    /// <summary>
    /// Computes the Levenshtein distance between two strings (edit distance).
    /// </summary>
    private static int LevenshteinDistance(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        int[] row = new int[b.Length + 1];
        for (int i = 0; i <= b.Length; i++)
        {
            row[i] = i;
        }

        for (int i = 1; i <= a.Length; i++)
        {
            int prevDiag = i - 1;
            row[0] = i;

            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                int temp = row[j];
                row[j] = Math.Min(
                    Math.Min(row[j] + 1, row[j - 1] + 1),
                    prevDiag + cost);
                prevDiag = temp;
            }
        }

        return row[b.Length];
    }

    [GeneratedRegex(@"\A(?<path>(?:\./)?(?:scripts|references|assets)/[A-Za-z0-9._\-/]+)\z", RegexOptions.Compiled)]
    private static partial Regex InlinePathPattern();
}
