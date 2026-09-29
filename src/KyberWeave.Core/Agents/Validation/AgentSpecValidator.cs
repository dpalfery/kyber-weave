using KyberWeave.Core.Agents.Model;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Parsing;

namespace KyberWeave.Core.Agents.Validation;

/// <summary>
/// Validates individual Agent definition files against specification rules.
/// </summary>
public static class AgentSpecValidator
{
    public const string RuleMissingName = "KW-AGENT-SPEC-001";
    public const string RuleMissingDescription = "KW-AGENT-SPEC-002";
    public const string RuleMissingInstructions = "KW-AGENT-SPEC-003";
    public const string RuleBrokenFileReference = "KW-AGENT-SPEC-004";

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
    /// Uses <see cref="FileReferenceExtractor"/> with <see cref="FileReferenceOptions.AgentDefault"/> to extract
    /// markdown links and inline code paths, skipping URLs, anchors, traversal paths, and absolute paths.
    /// If DirectoryPath is null or empty, the check is skipped (cannot resolve relative paths).
    /// </remarks>
    private static void ValidateBrokenFileReferences(AgentModel agent, DiagnosticReport report)
    {
        if (string.IsNullOrWhiteSpace(agent.DirectoryPath))
        {
            return;
        }

        List<string> textsToScan = [];
        if (!string.IsNullOrWhiteSpace(agent.Description))
        {
            textsToScan.Add(agent.Description);
        }

        if (!string.IsNullOrWhiteSpace(agent.InstructionsBody))
        {
            textsToScan.Add(agent.InstructionsBody);
        }

        if (textsToScan.Count == 0)
        {
            return;
        }

        IReadOnlyList<ExtractedFileReference> references = FileReferenceExtractor.ExtractFromTexts(
            textsToScan,
            agent.DirectoryPath,
            FileReferenceOptions.AgentDefault);

        foreach (ExtractedFileReference reference in references)
        {
            if (!reference.Exists)
            {
                string resolvedPath = reference.ResolvedFullPath ?? DescribeResolvedPath(agent.DirectoryPath, reference.Reference);
                string hintText = reference.NearestMatch is not null
                    ? $"Nearest match: '{reference.NearestMatch}' relative to the agent's directory."
                    : FallbackHint();

                report.Add(new Diagnostic(
                    RuleBrokenFileReference,
                    Severity.Error,
                    $"File reference '{reference.Reference}' does not resolve — no file at '{resolvedPath}'.",
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

    private static string FallbackHint() =>
        "Relative file path does not exist. Check the path spelling relative to the agent definition directory.";
}
