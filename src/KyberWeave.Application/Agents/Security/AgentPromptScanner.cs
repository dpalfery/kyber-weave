using KyberWeave.Core.Agents.Model;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Security;

namespace KyberWeave.Core.Agents.Security;

/// <summary>
/// Security scanner for agent system prompts and manifests. Applies the same instruction-surface
/// heuristics as skill scanning (injection, hidden comments, base64, secrets, provenance),
/// with KW-AGENT-SEC-* rule codes.
/// </summary>
public static class AgentPromptScanner
{
    /// <summary>Ignore-previous-instructions / safety-bypass style injection (KW-AGENT-SEC-001).</summary>
    public const string RuleSafetyBypass = "KW-AGENT-SEC-001";

    /// <summary>OpenAI-style API key finding (KW-AGENT-SEC-024). Other secret codes are KW-AGENT-SEC-020–025.</summary>
    public const string RuleHardcodedSecret = "KW-AGENT-SEC-024";

    public static DiagnosticReport Scan(AgentModel agent)
    {
        DiagnosticReport report = new DiagnosticReport();
        string text = $"{agent.Description}\n{agent.InstructionsBody}";
        InstructionSurfaceRuleCodes codes = InstructionSurfaceRuleCodes.ForAgents;
        string subject = agent.RoleName;
        string file = agent.FilePath;

        report.AddRange(InstructionSurfaceScanner.ScanProse(text, subject, file, codes, "agent"));

        // Prefer top-level frontmatter keys; also accept nested metadata.* style keys.
        Dictionary<string, string> meta = agent.FrontmatterOrMetadata;
        meta.TryGetValue("license", out string? license);
        if (string.IsNullOrWhiteSpace(license))
            meta.TryGetValue("License", out license);

        report.AddRange(InstructionSurfaceScanner.ScanProvenance(meta, license, subject, file, codes));
        return report;
    }
}
