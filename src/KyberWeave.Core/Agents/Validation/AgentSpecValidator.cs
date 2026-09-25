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
    /// Pattern: negative lookbehind to avoid partial matches, then optional './' prefix, then the
    /// directory and path with alphanumeric/dash/slash characters.
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

        Dictionary<string, (string source, bool exists, string? hint)> references = new(StringComparer.OrdinalIgnoreCase);

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
        foreach (var (reference, (source, exists, hint)) in references)
        {
            if (!exists)
            {
                string resolvedPath = Path.GetFullPath(Path.Combine(agent.DirectoryPath, reference));
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

        // Extract inline paths matching the regex pattern
        foreach (Match match in InlinePathRegex.Matches(text))
        {
            string path = match.Groups["path"].Value;
            ConsiderReference(path, directoryPath, references);
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
        if (target.StartsWith("http://", StringComparison.Ordinal) ||
            target.StartsWith("https://", StringComparison.Ordinal) ||
            target.StartsWith('#') ||
            target.StartsWith("mailto:", StringComparison.Ordinal) ||
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
        if (Path.IsPathRooted(normalized))
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
    /// Searches the agent's directory and one level up. Returns the best match or null if none found.
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

        // Search agent's directory
        if (Directory.Exists(directoryPath))
        {
            foreach (string file in Directory.EnumerateFileSystemEntries(directoryPath))
            {
                string fileName = Path.GetFileName(file);
                int distance = LevenshteinDistance(referenceName, fileName);
                candidates.Add((fileName, distance));
            }
        }

        // Search one level up if no close matches locally
        if (candidates.Count == 0 && !string.IsNullOrWhiteSpace(directoryPath))
        {
            string? parentDir = Directory.GetParent(directoryPath)?.FullName;
            if (parentDir is { } && Directory.Exists(parentDir))
            {
                foreach (string file in Directory.EnumerateFileSystemEntries(parentDir))
                {
                    string fileName = Path.GetFileName(file);
                    int distance = LevenshteinDistance(referenceName, fileName);
                    candidates.Add((fileName, distance));
                }
            }
        }

        // Return best match if distance is reasonable (≤ 3 edits)
        if (candidates.Count > 0)
        {
            var best = candidates.OrderBy(c => c.distance).FirstOrDefault();
            if (best.distance <= 3)
            {
                return $"Nearest match: '{best.path}' in the agent's directory.";
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

    [GeneratedRegex(@"(?<![A-Za-z0-9._\-/])(?<path>(?:\./)?(?:scripts|references|assets)/[A-Za-z0-9._\-/]+)", RegexOptions.Compiled)]
    private static partial Regex InlinePathPattern();
}
