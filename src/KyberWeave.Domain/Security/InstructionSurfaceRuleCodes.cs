namespace KyberWeave.Core.Security;

/// <summary>
/// Maps shared instruction-surface pattern ids to artifact-specific KW-* rule codes.
/// </summary>
public sealed class InstructionSurfaceRuleCodes
{
    public required IReadOnlyDictionary<string, string> Injection { get; init; }
    public required string HtmlComment { get; init; }
    public required string Base64Blob { get; init; }
    public required IReadOnlyDictionary<string, string> Scripts { get; init; }
    public required IReadOnlyDictionary<string, string> Secrets { get; init; }
    public required string MissingAuthor { get; init; }
    public required string MissingVersion { get; init; }
    public required string MissingLicense { get; init; }

    public static InstructionSurfaceRuleCodes ForSkills { get; } = new()
    {
        Injection = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ignore-previous"] = "KW-SKILL-SEC-001",
            ["disregard-guidelines"] = "KW-SKILL-SEC-002",
            ["system-override"] = "KW-SKILL-SEC-003",
            ["persona-hijack"] = "KW-SKILL-SEC-004",
            ["exfiltration"] = "KW-SKILL-SEC-005",
            ["bypass-sandbox"] = "KW-SKILL-SEC-008",
        },
        HtmlComment = "KW-SKILL-SEC-006",
        Base64Blob = "KW-SKILL-SEC-007",
        Scripts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["curl-pipe-sh"] = "KW-SKILL-SEC-010",
            ["wget-pipe-sh"] = "KW-SKILL-SEC-011",
            ["eval-base64"] = "KW-SKILL-SEC-012",
            ["destructive"] = "KW-SKILL-SEC-013",
        },
        Secrets = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["aws-key"] = "KW-SKILL-SEC-020",
            ["github-token"] = "KW-SKILL-SEC-021",
            ["private-key"] = "KW-SKILL-SEC-022",
            ["slack-token"] = "KW-SKILL-SEC-023",
            ["openai-key"] = "KW-SKILL-SEC-024",
            ["password-assignment"] = "KW-SKILL-SEC-025",
        },
        MissingAuthor = "KW-SKILL-SEC-030",
        MissingVersion = "KW-SKILL-SEC-031",
        MissingLicense = "KW-SKILL-SEC-032",
    };

    public static InstructionSurfaceRuleCodes ForAgents { get; } = new()
    {
        Injection = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ignore-previous"] = "KW-AGENT-SEC-001",
            ["disregard-guidelines"] = "KW-AGENT-SEC-002",
            ["system-override"] = "KW-AGENT-SEC-003",
            ["persona-hijack"] = "KW-AGENT-SEC-004",
            ["exfiltration"] = "KW-AGENT-SEC-005",
            ["bypass-sandbox"] = "KW-AGENT-SEC-008",
        },
        HtmlComment = "KW-AGENT-SEC-006",
        Base64Blob = "KW-AGENT-SEC-007",
        Scripts = new Dictionary<string, string>(StringComparer.Ordinal),
        Secrets = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["aws-key"] = "KW-AGENT-SEC-020",
            ["github-token"] = "KW-AGENT-SEC-021",
            ["private-key"] = "KW-AGENT-SEC-022",
            ["slack-token"] = "KW-AGENT-SEC-023",
            ["openai-key"] = "KW-AGENT-SEC-024",
            ["password-assignment"] = "KW-AGENT-SEC-025",
        },
        MissingAuthor = "KW-AGENT-SEC-030",
        MissingVersion = "KW-AGENT-SEC-031",
        MissingLicense = "KW-AGENT-SEC-032",
    };
}
