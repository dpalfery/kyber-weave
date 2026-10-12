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
    internal static IReadOnlyList<string> OrderedTools { get; } =
    [
        "vscode",
        "read",
        "todo",
        "codegraph/*",
        "kyber-weave/*",
        "context7/*",
        "search",
        "execute",
        "web",
        "edit",
        "agent",
        "edit/createDirectory",
        "edit/createFile",
        "edit/editFiles",
        "edit/rename",
        "vscodeGeneral/rename",
        "vscode/runCommand",
        "vscode/toolSearch",
        "vscode/askQuestions",
        "execute/getTerminalOutput",
        "execute/killTerminal",
        "execute/sendToTerminal",
        "execute/runTask",
        "execute/createAndRunTask",
        "execute/runInTerminal",
        "execute/testFailure",
        "read/problems",
        "read/readFile",
        "read/viewImage",
        "read/terminalSelection",
        "read/terminalLastCommand",
        "read/getTaskOutput",
        "vscodeTasks/createAndRunTask",
        "vscodeTasks/runTask",
        "vscodeTasks/getTaskOutput",
        "vscodeTasks/problems",
        "vscodeGeneral/toolSearch",
        "vscodeGeneral/testFailure"
    ];

    internal static IReadOnlyDictionary<string, string?> RequiredCapabilities { get; } =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["vscode"] = null,
            ["read"] = "filesystem.read",
            ["todo"] = null,
            ["codegraph/*"] = "filesystem.read",
            ["kyber-weave/*"] = "filesystem.read",
            ["context7/*"] = "filesystem.read",
            ["search"] = "filesystem.search",
            ["execute"] = "process.execute",
            ["web"] = "network.read",
            ["edit"] = "filesystem.write",
            ["agent"] = "delegate",
            ["edit/createDirectory"] = "filesystem.write",
            ["edit/createFile"] = "filesystem.write",
            ["edit/editFiles"] = "filesystem.write",
            ["edit/rename"] = "filesystem.write",
            ["vscodeGeneral/rename"] = "filesystem.write",
            ["vscode/runCommand"] = null,
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
            ["read/getTaskOutput"] = "filesystem.read",
            ["vscodeTasks/createAndRunTask"] = "process.execute",
            ["vscodeTasks/runTask"] = "process.execute",
            ["vscodeTasks/getTaskOutput"] = "process.execute",
            ["vscodeTasks/problems"] = "process.execute",
            ["vscodeGeneral/toolSearch"] = null,
            ["vscodeGeneral/testFailure"] = "process.execute"
        };

    internal static IReadOnlyList<string> Normalize(IEnumerable<string> tools)
    {
        HashSet<string> membership = tools.ToHashSet(StringComparer.Ordinal);
        return OrderedTools.Where(membership.Contains).ToArray();
    }
}
