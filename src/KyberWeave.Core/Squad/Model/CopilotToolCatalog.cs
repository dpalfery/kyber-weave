namespace KyberWeave.Core.Squad.Model;

/// <summary>Known GitHub Copilot custom-agent tools and their deterministic emission order.</summary>
/// <remarks>
/// Canonical agents declare exact Copilot membership because the semantic capability lattice
/// cannot losslessly express granular edit operations. A Copilot-specific capability profile
/// remains an upper bound: every governed tool names the capability that must be explicitly
/// allowed before source loads, without widening the shared cross-harness profile.
/// </remarks>
internal static class CopilotToolCatalog
{
    // Shared tools lead so every agent's tools list shares the longest possible prefix
    // (prompt-cache friendly). Order: vscode, read, todo, MCP, search, execute, edit, web, agent.
    // The coarse tool-set names at the end remain valid for agents not yet narrowed to
    // explicit tools; they sort after every explicit entry.
    internal static IReadOnlyList<string> OrderedTools { get; } =
    [
        "vscode/runCommand",
        "vscode/toolSearch",
        "vscode/askQuestions",
        "read/problems",
        "read/readFile",
        "read/viewImage",
        "read/terminalSelection",
        "read/terminalLastCommand",
        "read/getTaskOutput",
        "todo",
        "codegraph/*",
        "kyber-weave/*",
        "context7/resolve-library-id",
        "context7/query-docs",
        "search",
        "execute/getTerminalOutput",
        "execute/killTerminal",
        "execute/sendToTerminal",
        "execute/runTask",
        "execute/createAndRunTask",
        "execute/runInTerminal",
        "execute/testFailure",
        "edit/createDirectory",
        "edit/createFile",
        "edit/editFiles",
        "edit/rename",
        "web",
        "agent",
        "vscode",
        "tool_search",
        "read",
        "context7/*",
        "execute",
        "edit"
    ];

    internal static IReadOnlyDictionary<string, string?> RequiredCapabilities { get; } =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["vscode"] = null,
            // tool_search only surfaces definitions for tools the agent's allow-list
            // already grants (Copilot groups it under the vscode tool set), so it
            // confers no new capability and stays ungoverned like vscode and todo.
            ["tool_search"] = null,
            ["read"] = "filesystem.read",
            ["todo"] = null,
            ["codegraph/*"] = "filesystem.read",
            ["kyber-weave/*"] = "filesystem.read",
            ["context7/*"] = "filesystem.read",
            ["context7/resolve-library-id"] = "filesystem.read",
            ["context7/query-docs"] = "filesystem.read",
            ["search"] = "filesystem.search",
            ["execute"] = "process.execute",
            ["web"] = "network.read",
            ["edit"] = "filesystem.write",
            ["agent"] = "delegate",
            ["edit/createDirectory"] = "filesystem.write",
            ["edit/createFile"] = "filesystem.write",
            ["edit/editFiles"] = "filesystem.write",
            ["edit/rename"] = "filesystem.write",
            ["vscode/runCommand"] = "process.execute",
            ["vscode/toolSearch"] = null,
            ["vscode/askQuestions"] = null,
            ["execute/getTerminalOutput"] = "process.execute",
            ["execute/killTerminal"] = "process.execute",
            ["execute/sendToTerminal"] = "process.execute",
            ["execute/runTask"] = "process.execute",
            ["execute/createAndRunTask"] = "process.execute",
            ["execute/runInTerminal"] = "process.execute",
            ["execute/testFailure"] = "process.execute",
            ["read/problems"] = "filesystem.read",
            ["read/readFile"] = "filesystem.read",
            ["read/viewImage"] = "filesystem.read",
            ["read/terminalSelection"] = "filesystem.read",
            ["read/terminalLastCommand"] = "filesystem.read",
            ["read/getTaskOutput"] = "filesystem.read"
        };

    internal static IReadOnlyList<string> Normalize(IEnumerable<string> tools)
    {
        HashSet<string> membership = tools.ToHashSet(StringComparer.Ordinal);
        return OrderedTools.Where(membership.Contains).ToArray();
    }
}
