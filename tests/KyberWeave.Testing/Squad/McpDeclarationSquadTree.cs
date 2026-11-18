using System.Text.Json;
using System.Text.RegularExpressions;

namespace KyberWeave.Testing.Squad;

/// <summary>
/// An in-memory copy of the shipped Squad source whose <c>mcp.json</c> server declaration, or one agent's capability profile,
/// is replaced by what a test names, so that change is the only variable.
/// </summary>
/// <remarks>
/// A copy rather than an edit keeps the shipped source as the thing under test: everything except the one variable a variant
/// names stays real, including the agent roster the grant is projected onto.
/// </remarks>
public sealed partial class McpDeclarationSquadTree
{
    /// <summary>The profile the read-denied variant points its agent at: the shipped <c>read-only</c> profile with <c>filesystem.read</c> denied.</summary>
    private const string FileReadDeniedProfile = "read-only-no-file-read";

    private const string ReadOnlyProfile = "read-only";

    private static readonly JsonSerializerOptions JsonSerializerOptions = new() { WriteIndented = true };

    private McpDeclarationSquadTree()
    {
        Tree = CanonicalSquadTree.Create();
    }

    /// <summary>Gets the tree the variant is written into.</summary>
    public CanonicalSquadTree Tree { get; }

    /// <summary>Gets the root of the variant's Squad source.</summary>
    public string ProductRoot => Tree.ProductRoot;

    /// <summary>Replaces <c>mcp.json</c> with a declaration of the named servers, keeping the order the caller passes.</summary>
    /// <param name="serverNames">The servers the declaration names.</param>
    /// <returns>The variant.</returns>
    public static McpDeclarationSquadTree WithServers(params string[] serverNames)
    {
        McpDeclarationSquadTree variant = new();

        Dictionary<string, Dictionary<string, object>> servers = new(StringComparer.Ordinal);
        foreach (string name in serverNames)
        {
            servers[name] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["command"] = $"{name}-mcp",
                ["args"] = new[] { "--repo-root", "." }
            };
        }

        Dictionary<string, Dictionary<string, Dictionary<string, object>>> root =
            new(StringComparer.Ordinal) { ["mcpServers"] = servers };
        variant.Tree.Files.WriteAllText(
            variant.Tree.PathOf("mcp.json"),
            JsonSerializer.Serialize(root, JsonSerializerOptions) + "\n");

        return variant;
    }

    /// <summary>
    /// Adds a clone of the <c>read-only</c> profile with <c>filesystem.read: deny</c> and points the named agent at it, narrowing
    /// the agent's Copilot roster because the source validator rejects a read tool against a profile that denies read.
    /// </summary>
    /// <param name="agentName">The agent to repoint.</param>
    /// <returns>The variant.</returns>
    public static McpDeclarationSquadTree WithFileReadDeniedAgent(string agentName)
    {
        McpDeclarationSquadTree variant = new();
        variant.AddReadDeniedVariantOf(ReadOnlyProfile);
        variant.RepointAgent(agentName);
        return variant;
    }

    private void AddReadDeniedVariantOf(string sourceProfileName)
    {
        string path = Tree.PathOf("profiles/capabilities.yml");
        string text = Tree.Files.ReadAllText(path).ReplaceLineEndings("\n");
        string[] lines = text.Split('\n');

        (int start, int end) = LocateProfileBlock(lines, sourceProfileName, path);

        List<string> clone = [.. lines[start..end]];
        clone[0] = $"  {FileReadDeniedProfile}:";

        bool rewroteRead = false;
        for (int index = 0; index < clone.Count; index++)
        {
            if (clone[index].TrimStart().StartsWith("filesystem.read:", StringComparison.Ordinal))
            {
                clone[index] = "      filesystem.read: deny";
                rewroteRead = true;
            }
        }

        if (!rewroteRead)
        {
            throw new InvalidOperationException(
                $"Profile '{sourceProfileName}' in '{path}' declares no 'filesystem.read' permission "
                + $"to deny, so '{FileReadDeniedProfile}' would not differ from it.");
        }

        Tree.Files.WriteAllText(path, text.TrimEnd('\n') + "\n" + string.Join("\n", clone) + "\n");
    }

    private static (int Start, int End) LocateProfileBlock(string[] lines, string profileName, string path)
    {
        int start = Array.IndexOf(lines, $"  {profileName}:");
        if (start < 0)
        {
            throw new InvalidOperationException($"Expected '{path}' to declare profile '{profileName}'.");
        }

        int end = start + 1;
        while (end < lines.Length && !IsProfileHeader(lines[end]))
        {
            end++;
        }

        while (end > start + 1 && string.IsNullOrWhiteSpace(lines[end - 1]))
        {
            end--;
        }

        return (start, end);
    }

    private void RepointAgent(string agentName)
    {
        string path = Tree.PathOf($"agents/{agentName}.md");
        string text = Tree.Files.ReadAllText(path).ReplaceLineEndings("\n");

        string repointed = CapabilityProfileLine().Replace(text, $"capability-profile: {FileReadDeniedProfile}");
        if (string.Equals(repointed, text, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected '{path}' to declare a 'capability-profile' line to repoint.");
        }

        string narrowed = CopilotToolsLine().Replace(repointed, "copilot-tools: [vscode, todo, search, web]");
        if (string.Equals(narrowed, repointed, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected '{path}' to declare a 'copilot-tools' line to narrow.");
        }

        Tree.Files.WriteAllText(path, narrowed);
    }

    private static bool IsProfileHeader(string line) =>
        line.Length > 2 && line[0] == ' ' && line[1] == ' ' && line[2] != ' ' && line[^1] == ':';

    [GeneratedRegex("^capability-profile: .*$", RegexOptions.Multiline)]
    private static partial Regex CapabilityProfileLine();

    [GeneratedRegex("^copilot-tools: .*$", RegexOptions.Multiline)]
    private static partial Regex CopilotToolsLine();
}
