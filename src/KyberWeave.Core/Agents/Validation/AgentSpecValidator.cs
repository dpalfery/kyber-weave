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

    private static readonly Regex InlinePathRegex = MyRegex();

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

        // 4. Broken Relative File Reference Check
        ValidateRelativeReferences(agent, report);

        return report;
    }

    private static void ValidateRelativeReferences(AgentModel agent, DiagnosticReport report)
    {
        if (string.IsNullOrWhiteSpace(agent.DirectoryPath)) return;

        foreach (string text in new[] { agent.Description, agent.InstructionsBody })
        {
            if (string.IsNullOrWhiteSpace(text)) continue;

            MarkdownDocument document = Markdown.Parse(text);
            foreach (LinkInline link in document.Descendants<LinkInline>())
            {
                if (link.Url is { } url)
                {
                    CheckReference(agent, report, url);
                }
            }

            foreach (Match match in InlinePathRegex.Matches(text))
            {
                CheckReference(agent, report, match.Groups["path"].Value);
            }
        }
    }

    private static void CheckReference(AgentModel agent, DiagnosticReport report, string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return;

        string normalized = candidate.Trim();
        if (normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith('#')
            || normalized.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith('/')
            || normalized.Contains("..", StringComparison.Ordinal)
            || normalized.Contains('<', StringComparison.Ordinal)
            || normalized.Contains('>', StringComparison.Ordinal))
        {
            return;
        }

        if (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized.Substring(2);
        }

        string resolvedPath = Path.GetFullPath(Path.Combine(agent.DirectoryPath, normalized));
        if (File.Exists(resolvedPath) || Directory.Exists(resolvedPath)) return;

        string hint = FindNearestMatchHint(agent.DirectoryPath, normalized)
            ?? "Relative file path does not exist. Check the path spelling relative to the agent definition directory.";

        report.Add(new Diagnostic(
            RuleBrokenFileReference,
            Severity.Error,
            $"File reference '{normalized}' does not resolve — no file at '{resolvedPath}'.",
            Path.GetFileName(agent.FilePath),
            agent.FilePath,
            hint));
    }

    private static string? FindNearestMatchHint(string directoryPath, string referencePath)
    {
        if (!Directory.Exists(directoryPath)) return null;

        string targetName = Path.GetFileName(referencePath.TrimEnd('/'));
        if (string.IsNullOrWhiteSpace(targetName)) return null;

        List<string> candidates = [];
        foreach (string file in Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories))
        {
            candidates.Add(Path.GetRelativePath(directoryPath, file).Replace('\\', '/'));
        }
        foreach (string dir in Directory.EnumerateDirectories(directoryPath, "*", SearchOption.AllDirectories))
        {
            candidates.Add(Path.GetRelativePath(directoryPath, dir).Replace('\\', '/'));
        }

        string? closest = candidates
            .Select(path => new { Path = path, Distance = LevenshteinDistance(Path.GetFileName(path), targetName) })
            .Where(x => x.Distance <= Math.Max(3, targetName.Length / 4))
            .OrderBy(x => x.Distance)
            .ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Path)
            .FirstOrDefault();

        return closest is null ? null : $"Nearest match: '{closest}' in the agent's directory.";
    }

    private static int LevenshteinDistance(string left, string right)
    {
        if (string.IsNullOrEmpty(left)) return string.IsNullOrEmpty(right) ? 0 : right.Length;
        if (string.IsNullOrEmpty(right)) return left.Length;

        int[][] dp = new int[left.Length + 1][];
        for (int i = 0; i <= left.Length; i++)
        {
            dp[i] = new int[right.Length + 1];
        }

        for (int i = 0; i <= left.Length; i++) dp[i][0] = i;
        for (int j = 0; j <= right.Length; j++) dp[0][j] = j;

        for (int i = 1; i <= left.Length; i++)
        {
            for (int j = 1; j <= right.Length; j++)
            {
                int cost = left[i - 1] == right[j - 1] ? 0 : 1;
                dp[i][j] = Math.Min(
                    Math.Min(dp[i - 1][j] + 1, dp[i][j - 1] + 1),
                    dp[i - 1][j - 1] + cost);
            }
        }

        return dp[left.Length][right.Length];
    }

    [GeneratedRegex(@"(?<![A-Za-z0-9._\-/])(?<path>(?:\./)?(?:scripts|references|assets)/[A-Za-z0-9._\-/]+)", RegexOptions.Compiled)]
    private static partial Regex MyRegex();
}
