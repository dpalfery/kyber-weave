using KyberWeave.Core.Squad.Deployment;

namespace KyberWeave.Arbiter.Hooks;

/// <summary>
/// One harness and every fact this repository holds about it: its hook token, the other names by
/// which it is known, and whether it is served by an adapter, rewrites a dispatch, and is trusted.
/// </summary>
/// <remarks>
/// A row is added only when a fact is known for that harness. An empty <see cref="McpClientNames"/>
/// is deliberate, not missing data: no probe, the client's own logs or its own source has shown the
/// name it sends, so a dispatch from it takes the prompt file instead of an inserted prompt.
/// </remarks>
public sealed record HarnessDescriptor
{
    /// <summary>The token the dispatch hook is run under, such as <c>claude</c> or <c>copilot-vscode</c>.</summary>
    public required string Token { get; init; }

    /// <summary>The name a person reads for this harness in diagnostics.</summary>
    public required string DisplayName { get; init; }

    /// <summary>The Squad target this harness deploys, or null for a harness Squad does not target.</summary>
    public SquadTarget? SquadTarget { get; init; }

    /// <summary>The canonical Squad command-line token for <see cref="SquadTarget"/>.</summary>
    public string? SquadToken { get; init; }

    /// <summary>The other spellings <c>squad install</c> accepts for this harness's target.</summary>
    public IReadOnlyList<string> SquadAliases { get; init; } = [];

    /// <summary>
    /// The name the DocGraph MCP catalog uses for this harness, or null when the catalog has no row.
    /// It is not always <see cref="Token"/>: the catalog spells Claude Code <c>claude-code</c> and
    /// VS Code <c>vscode</c>.
    /// </summary>
    public string? McpCatalogName { get; init; }

    /// <summary>
    /// The <c>clientInfo.name</c> values a probe, the client's own logs or its own source showed this
    /// client sending. Empty where no such evidence exists.
    /// </summary>
    public IReadOnlyList<string> McpClientNames { get; init; } = [];

    /// <summary>
    /// A prefix that carries a client name whose remainder is the user's own server key, such as
    /// pi-mcp-adapter's <c>pi-mcp-&lt;server key&gt;</c>. Null when the harness sends a fixed name.
    /// </summary>
    public string? McpClientPrefix { get; init; }

    /// <summary>The project-scope directory a Global receipt written before rc.11 recorded as its file prefix.</summary>
    public string? LegacyGlobalPrefix { get; init; }

    /// <summary>The field a post-dispatch <c>Deny</c> renders as on this harness, or null when it is not advisory.</summary>
    public string? PostDispatchAdvisoryChannel { get; init; }

    /// <summary>Whether the host has a hook adapter that serves this harness.</summary>
    public bool ServedByHookAdapter { get; init; }

    /// <summary>
    /// Whether the hook replaces the dispatch text, so the prompt travels inside the call. False is
    /// deliberate for a harness whose rewrite has not been shown live: its worker takes the prompt file.
    /// </summary>
    public bool RewritesDispatch { get; init; }

    /// <summary>Whether the harness renders its hooks per agent, so a dispatch already names its caller.</summary>
    public bool Trusted { get; init; }

    /// <summary>
    /// Whether a hook payload that names no caller is the root session's. True only where the harness
    /// names every sub-agent's caller, so an absent name means the root.
    /// </summary>
    public bool UnnamedCallerIsRoot { get; init; }
}

/// <summary>
/// Every harness this repository knows, in one place: the hook token, the Squad target it deploys,
/// the MCP client names it sends, whether an adapter serves it, whether it rewrites a dispatch, and
/// whether its hooks are trusted. Every site that used to list harness tokens reads this instead.
/// </summary>
/// <remarks>
/// <para>
/// The rows are ordered by Squad target so <see cref="SquadTargets"/> keeps the stable presentation
/// order the command line has always used. Copilot has two rows because it has two hook harnesses,
/// one for VS Code and one for the CLI; both name the same squad token, <c>copilot</c>.
/// </para>
/// <para>
/// A rewrite flag of false is a fact, not a gap: ZCode and Command Code stay on the prompt file until
/// a live dispatch shows the worker received the replaced text, and Warp has no dispatch hook. Devin
/// rewrites but has no MCP client name, because no probe, log or source has shown the name it sends.
/// </para>
/// </remarks>
public static class HarnessRegistry
{
    /// <summary>Codex, which rewrites its dispatch and takes an advisory post-dispatch result.</summary>
    public static HarnessDescriptor Codex { get; } = new()
    {
        Token = "codex",
        DisplayName = "Codex",
        SquadTarget = SquadTarget.Codex,
        SquadToken = "codex",
        McpCatalogName = "codex",
        McpClientNames = ["codex-mcp-client"],
        LegacyGlobalPrefix = ".codex/",
        PostDispatchAdvisoryChannel = "additionalContext",
        ServedByHookAdapter = true,
        RewritesDispatch = true,
    };

    /// <summary>Cursor CLI, which rewrites its dispatch and takes an advisory post-dispatch result.</summary>
    public static HarnessDescriptor Cursor { get; } = new()
    {
        Token = "cursor",
        DisplayName = "Cursor",
        SquadTarget = SquadTarget.Cursor,
        SquadToken = "cursor",
        McpCatalogName = "cursor",
        McpClientNames = ["Cursor"],
        LegacyGlobalPrefix = ".cursor/",
        PostDispatchAdvisoryChannel = "additional_context",
        ServedByHookAdapter = true,
        RewritesDispatch = true,
    };

    /// <summary>Claude Code, a trusted harness whose frontmatter hooks render per agent.</summary>
    public static HarnessDescriptor Claude { get; } = new()
    {
        Token = "claude",
        DisplayName = "Claude",
        SquadTarget = SquadTarget.Claude,
        SquadToken = "claude",
        McpCatalogName = "claude-code",
        McpClientNames = ["claude-code"],
        LegacyGlobalPrefix = ".claude/",
        ServedByHookAdapter = true,
        RewritesDispatch = true,
        Trusted = true,
    };

    /// <summary>Copilot in VS Code, the Local-schema harness whose frontmatter hooks render per agent.</summary>
    public static HarnessDescriptor CopilotVsCode { get; } = new()
    {
        Token = "copilot-vscode",
        DisplayName = "Copilot in VS Code",
        SquadTarget = SquadTarget.Copilot,
        SquadToken = "copilot",
        SquadAliases = ["github-copilot"],
        McpCatalogName = "vscode",
        McpClientNames = ["Visual Studio Code", "Visual Studio Code - Insiders"],
        LegacyGlobalPrefix = ".github/",
        ServedByHookAdapter = true,
        RewritesDispatch = true,
        Trusted = true,
    };

    /// <summary>Copilot CLI, a project-wide harness whose hook payload names every sub-agent's caller.</summary>
    public static HarnessDescriptor CopilotCli { get; } = new()
    {
        Token = "copilot-cli",
        DisplayName = "Copilot CLI",
        SquadTarget = SquadTarget.Copilot,
        SquadToken = "copilot",
        SquadAliases = ["github-copilot"],
        McpClientNames = ["copilot-cli"],
        LegacyGlobalPrefix = ".github/",
        PostDispatchAdvisoryChannel = "additionalContext",
        ServedByHookAdapter = true,
        RewritesDispatch = true,
        UnnamedCallerIsRoot = true,
    };

    /// <summary>OpenCode, served by a rendered plugin shim.</summary>
    public static HarnessDescriptor OpenCode { get; } = new()
    {
        Token = "opencode",
        DisplayName = "OpenCode",
        SquadTarget = SquadTarget.OpenCode,
        SquadToken = "opencode",
        McpCatalogName = "opencode",
        McpClientNames = ["opencode"],
        LegacyGlobalPrefix = ".opencode/",
        ServedByHookAdapter = true,
        RewritesDispatch = true,
    };

    /// <summary>Kilo, served by a rendered plugin shim.</summary>
    public static HarnessDescriptor Kilo { get; } = new()
    {
        Token = "kilo",
        DisplayName = "Kilo",
        SquadTarget = SquadTarget.Kilo,
        SquadToken = "kilo",
        McpClientNames = ["kilo"],
        LegacyGlobalPrefix = ".kilo/",
        ServedByHookAdapter = true,
        RewritesDispatch = true,
    };

    /// <summary>Antigravity, which rewrites through its <c>overwrite</c> field.</summary>
    public static HarnessDescriptor Antigravity { get; } = new()
    {
        Token = "antigravity",
        DisplayName = "Antigravity",
        SquadTarget = SquadTarget.Antigravity,
        SquadToken = "antigravity",
        McpClientNames = ["antigravity-client"],
        LegacyGlobalPrefix = ".agents/",
        ServedByHookAdapter = true,
        RewritesDispatch = true,
    };

    /// <summary>Warp, which has no dispatch hook, so it always takes the prompt file.</summary>
    public static HarnessDescriptor Warp { get; } = new()
    {
        Token = "warp",
        DisplayName = "Warp",
        SquadTarget = SquadTarget.Warp,
        SquadToken = "warp",
        LegacyGlobalPrefix = ".warp/",
    };

    /// <summary>Factory Droid, served by an owned command hook.</summary>
    public static HarnessDescriptor Factory { get; } = new()
    {
        Token = "factory",
        DisplayName = "Factory",
        SquadTarget = SquadTarget.Factory,
        SquadToken = "factory",
        SquadAliases = ["factory-droids"],
        McpCatalogName = "factory",
        McpClientNames = ["factory-cli"],
        LegacyGlobalPrefix = ".factory/",
        ServedByHookAdapter = true,
        RewritesDispatch = true,
    };

    /// <summary>Pi, whose MCP adapter sends a per-server-key client name.</summary>
    public static HarnessDescriptor Pi { get; } = new()
    {
        Token = "pi",
        DisplayName = "Pi",
        SquadTarget = SquadTarget.Pi,
        SquadToken = "pi",
        McpClientPrefix = "pi-mcp-",
        LegacyGlobalPrefix = ".pi/",
        ServedByHookAdapter = true,
        RewritesDispatch = true,
    };

    /// <summary>ZCode, which takes the prompt file until its rewrite check passes.</summary>
    public static HarnessDescriptor ZCode { get; } = new()
    {
        Token = "zcode",
        DisplayName = "ZCode",
        SquadTarget = SquadTarget.ZCode,
        SquadToken = "zcode",
        McpCatalogName = "zcode",
        LegacyGlobalPrefix = ".zcode/",
        ServedByHookAdapter = false,
        RewritesDispatch = false,
    };

    /// <summary>Devin, which rewrites but whose MCP client name is unverified.</summary>
    public static HarnessDescriptor Devin { get; } = new()
    {
        Token = "devin",
        DisplayName = "Devin",
        SquadTarget = SquadTarget.Devin,
        SquadToken = "devin",
        LegacyGlobalPrefix = ".devin/",
        ServedByHookAdapter = true,
        RewritesDispatch = true,
    };

    /// <summary>Command Code, which takes the prompt file until its rewrite check passes.</summary>
    public static HarnessDescriptor CommandCode { get; } = new()
    {
        Token = "commandcode",
        DisplayName = "Command Code",
        SquadTarget = SquadTarget.CommandCode,
        SquadToken = "commandcode",
        SquadAliases = ["command-code"],
        LegacyGlobalPrefix = ".commandcode/",
        ServedByHookAdapter = true,
        RewritesDispatch = false,
    };

    /// <summary>Every harness, in Squad-target order.</summary>
    public static IReadOnlyList<HarnessDescriptor> Rows { get; } =
    [
        Codex,
        Cursor,
        Claude,
        CopilotVsCode,
        CopilotCli,
        OpenCode,
        Kilo,
        Antigravity,
        Warp,
        Factory,
        Pi,
        ZCode,
        Devin,
        CommandCode,
    ];

    private static readonly Dictionary<string, HarnessDescriptor> ByTokenIndex =
        Rows.ToDictionary(row => row.Token, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, HarnessDescriptor> ByMcpClientNameIndex = BuildMcpClientNameIndex();

    private static readonly Dictionary<string, HarnessDescriptor> ByMcpCatalogNameIndex = Rows
        .Where(row => row.McpCatalogName is not null)
        .ToDictionary(row => row.McpCatalogName!, StringComparer.Ordinal);

    /// <summary>The harness tokens whose hook replaces the dispatch text.</summary>
    public static IReadOnlySet<string> RewriteTokens { get; } =
        Rows.Where(row => row.RewritesDispatch).Select(row => row.Token).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The harness tokens whose hooks render per agent and are trusted without a header.</summary>
    public static IReadOnlySet<string> TrustedTokens { get; } =
        Rows.Where(row => row.Trusted).Select(row => row.Token).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every Squad target this registry knows, in the registry's order.</summary>
    public static IReadOnlyList<SquadTarget> SquadTargets { get; } =
        Rows.Where(row => row.SquadTarget is not null)
            .Select(row => row.SquadTarget!.Value)
            .Distinct()
            .ToArray();

    /// <summary>The project-scope prefixes a Global receipt written before rc.11 recorded, by squad token.</summary>
    public static IReadOnlyDictionary<string, string> LegacyGlobalPrefixes { get; } = Rows
        .Where(row => row.SquadToken is not null && row.LegacyGlobalPrefix is not null)
        .DistinctBy(row => row.SquadToken)
        .ToDictionary(row => row.SquadToken!, row => row.LegacyGlobalPrefix!, StringComparer.Ordinal);

    /// <summary>Finds a row by its hook token, or null when the token is not in the registry.</summary>
    public static HarnessDescriptor? ByToken(string? token) =>
        token is not null && ByTokenIndex.TryGetValue(token.Trim(), out HarnessDescriptor? row) ? row : null;

    /// <summary>Finds a row by the name the DocGraph MCP catalog uses, or null when no row uses it.</summary>
    public static HarnessDescriptor? ByMcpCatalogName(string name) =>
        ByMcpCatalogNameIndex.TryGetValue(name, out HarnessDescriptor? row) ? row : null;

    /// <summary>Whether <paramref name="harness"/> rewrites the dispatch prompt.</summary>
    public static bool RewritesDispatch(string? harness) =>
        harness is not null && RewriteTokens.Contains(harness.Trim());

    /// <summary>Whether <paramref name="harness"/> renders per-agent hooks and is trusted without a header.</summary>
    public static bool IsTrusted(string harness) => TrustedTokens.Contains(harness);

    /// <summary>Whether an absent caller on <paramref name="harness"/> means the root session.</summary>
    public static bool UnnamedCallerIsRoot(string? harness) => ByToken(harness)?.UnnamedCallerIsRoot == true;

    /// <summary>The canonical Squad token for <paramref name="target"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the registry has no row for the target.</exception>
    public static string SquadTokenFor(SquadTarget target) =>
        Rows.FirstOrDefault(row => row.SquadTarget == target)?.SquadToken
        ?? throw new ArgumentOutOfRangeException(nameof(target), target, "Unknown Squad target.");

    /// <summary>The hook's harness token for a verified MCP client name, or null when none is verified.</summary>
    public static string? TokenForMcpClientName(string? clientName)
    {
        if (string.IsNullOrWhiteSpace(clientName))
        {
            return null;
        }

        string name = clientName.Trim();
        if (ByMcpClientNameIndex.TryGetValue(name, out HarnessDescriptor? row))
        {
            return row.Token;
        }

        foreach (HarnessDescriptor candidate in Rows)
        {
            if (candidate.McpClientPrefix is { } prefix
                && name.Length > prefix.Length
                && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return candidate.Token;
            }
        }

        return null;
    }

    private static Dictionary<string, HarnessDescriptor> BuildMcpClientNameIndex()
    {
        Dictionary<string, HarnessDescriptor> index = new(StringComparer.OrdinalIgnoreCase);
        foreach (HarnessDescriptor row in Rows)
        {
            foreach (string name in row.McpClientNames)
            {
                index[name] = row;
            }
        }

        return index;
    }
}
