using System.Text.RegularExpressions;

namespace KyberWeave.Core.Security;

/// <summary>
/// Shared regex surfaces for scanning untrusted instruction prose (skills and agents).
/// Pattern text is shared; callers attach their own stable KW-* rule codes.
/// </summary>
public static partial class InstructionSurfacePatterns
{
    public static readonly Regex HtmlComment =
        MyRegex();

    public static readonly Regex Base64Blob =
        new("[A-Za-z0-9+/]{120,}={0,2}", RegexOptions.Compiled);

    public static readonly Regex HtmlCommentInstructionHints =
        new(@"\b(ignore|disregard|system|instruction|tool|exfiltrat|send|password|token|secret)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Prompt-injection / persona-hijack / exfiltration phrasing in instruction bodies.</summary>
    public static readonly (string Id, Regex Rx, string Label)[] Injection =
    [
        ("ignore-previous", new(@"ignore\s+(all\s+)?(previous|prior|above)\s+instructions", RegexOptions.IgnoreCase | RegexOptions.Compiled), "prompt-injection: 'ignore previous instructions'"),
        ("disregard-guidelines", new(@"disregard\s+(your|all|the)\s+(guidelines|rules|instructions|policy|policies)", RegexOptions.IgnoreCase | RegexOptions.Compiled), "prompt-injection: 'disregard your guidelines'"),
        ("system-override", new(@"\b(system\s+override|override\s+(the\s+)?system\s+prompt|reveal\s+(your\s+)?system\s+prompt)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "prompt-injection: system-prompt override/exfiltration"),
        ("persona-hijack", new(@"you\s+are\s+now\s+(a|an|the)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "persona hijack: 'you are now …'"),
        ("exfiltration", new(@"\b(exfiltrat|send\s+(the\s+)?(api\s+key|secret|token|credential|password)s?\s+to|forward\s+.*\s+to\s+https?://)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "data exfiltration verb"),
        ("bypass-sandbox", new(@"\b(bypass\s+(the\s+)?sandbox|disable\s+user\s+approval|skip\s+confirmation)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "safety-gate bypass directive"),
    ];

    public static readonly (string Id, Regex Rx, string Label)[] Scripts =
    [
        ("curl-pipe-sh", new(@"curl\s+[^\n|]*\|\s*(ba)?sh", RegexOptions.IgnoreCase | RegexOptions.Compiled), "remote code execution: 'curl … | sh'"),
        ("wget-pipe-sh", new(@"wget\s+[^\n|]*\|\s*(ba)?sh", RegexOptions.IgnoreCase | RegexOptions.Compiled), "remote code execution: 'wget … | sh'"),
        ("eval-base64", new(@"eval\s*\(?\s*\$?\(?\s*.*base64\s+(--?d|--decode)", RegexOptions.IgnoreCase | RegexOptions.Compiled), "obfuscated execution: eval of base64-decoded content"),
        ("destructive", new(@"\b(rm\s+-rf\s+[~/]|:\(\)\s*\{\s*:\|:&\s*\};:)", RegexOptions.Compiled), "destructive command"),
    ];

    public static readonly (string Id, Regex Rx, string Label)[] Secrets =
    [
        ("aws-key", new("AKIA[0-9A-Z]{16}", RegexOptions.Compiled), "hardcoded AWS access key id"),
        ("github-token", new("gh[pousr]_[A-Za-z0-9]{36,}", RegexOptions.Compiled), "hardcoded GitHub token"),
        ("private-key", new(@"-----BEGIN\s+(RSA|EC|OPENSSH|PGP|DSA)?\s*PRIVATE KEY-----", RegexOptions.Compiled), "embedded private key"),
        ("slack-token", new("xox[baprs]-[A-Za-z0-9-]{10,}", RegexOptions.Compiled), "hardcoded Slack token"),
        ("openai-key", new(@"\bsk-[A-Za-z0-9]{20,}\b", RegexOptions.Compiled), "hardcoded OpenAI-style API key"),
        ("password-assignment", new(@"Password\s*=\s*[^\s;]+", RegexOptions.IgnoreCase | RegexOptions.Compiled), "hardcoded password assignment"),
    ];

    [GeneratedRegex("<!--(.*?)-->", RegexOptions.Compiled | RegexOptions.Singleline)]
    private static partial Regex MyRegex();
}
