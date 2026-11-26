namespace KyberWeave.Arbiter.Hooks;

/// <summary>
/// Maps the name a harness sends in its MCP <c>initialize</c> request (<c>clientInfo.name</c>) to the harness token
/// the dispatch hook knows.
/// </summary>
/// <remarks>
/// <para>
/// A harness name typed by a model is a guess: a Copilot CLI session sent <c>copilot</c>, which no hook token
/// matches, so its dispatch was read as one no hook can rewrite. The name the client itself sends is the only
/// verified source.
/// </para>
/// <para>
/// A row is added only after evidence showed that client sending that name: a live probe, the client's own logs, or its
/// own source where no probe could run. The comment on the row records which, with the client version and the date. A name with
/// no row, or no name at all, has no token, and the caller treats it as a dispatch no hook rewrites.
/// </para>
/// </remarks>
public static class McpClientHarness
{
    /// <summary>
    /// The token an unverified client is built for: no hook rewrites a dispatch under it, so the prompt is written
    /// to a file the conductor can name rather than being left out of the dispatch.
    /// </summary>
    public const string UnverifiedToken = "unverified";

    private static readonly Dictionary<string, string> Verified = new(StringComparer.OrdinalIgnoreCase)
    {
        // Copilot CLI 1.0.115, probed 2026-11-19.
        ["copilot-cli"] = "copilot-cli",

        // Claude Code 2.1.311, probed 2026-11-19.
        ["claude-code"] = "claude",

        // opencode 2.3.0, probed 2026-11-19.
        ["opencode"] = "opencode",

        // Factory Droid 1.6.0, seen 2026-11-26: 27 task-prompt replies in ~/.factory logs read
        // "unknown MCP client 'factory-cli'", so Droid runs wrote prompt files its rewriting hook never needed.
        ["factory-cli"] = "factory",

        // Cursor CLI 2026.11.19, probed 2026-11-26.
        ["Cursor"] = "cursor",

        // Kilo CLI 7.8.27, probed 2026-11-26.
        ["kilo"] = "kilo",

        // Antigravity CLI (agy) 1.5.14, probed 2026-11-26.
        ["antigravity-client"] = "antigravity",

        // VS Code 1.130.0 and Insiders 1.149.0, read 2026-11-26: the workbench sends product.json's nameLong.
        ["Visual Studio Code"] = "copilot-vscode",
        ["Visual Studio Code - Insiders"] = "copilot-vscode",

        // Codex, read 2026-11-26 from openai/codex main, codex-rs/codex-mcp/src/rmcp_client.rs; not installed here.
        ["codex-mcp-client"] = "codex",
    };

    /// <summary>
    /// pi-mcp-adapter 7.0.1 (read 2026-11-26, server-manager.ts) names its client <c>pi-mcp-&lt;server key&gt;</c>,
    /// so the name carries whatever key the user gave the server in their Pi config.
    /// </summary>
    private const string PiAdapterPrefix = "pi-mcp-";

    /// <summary>The hook's harness token for an MCP client name, or null when no probe, log or source has verified the name.</summary>
    /// <param name="clientName">The <c>clientInfo.name</c> a client sent in its MCP <c>initialize</c> request.</param>
    public static string? TokenFor(string? clientName)
    {
        if (string.IsNullOrWhiteSpace(clientName))
        {
            return null;
        }

        string name = clientName.Trim();
        if (Verified.TryGetValue(name, out string? token))
        {
            return token;
        }

        return name.Length > PiAdapterPrefix.Length && name.StartsWith(PiAdapterPrefix, StringComparison.OrdinalIgnoreCase)
            ? "pi"
            : null;
    }
}
