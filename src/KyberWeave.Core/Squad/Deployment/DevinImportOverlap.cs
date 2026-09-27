using System.Text.Json;

namespace KyberWeave.Core.Squad.Deployment;

/// <summary>
/// One tree Devin loads alongside <c>.devin/</c> that defines identities <c>.devin/</c>
/// already defines.
/// </summary>
/// <param name="SourceRoot">The overlapping tree, relative to the working directory, e.g. <c>.claude/skills</c>.</param>
/// <param name="Kind"><c>skill</c> or <c>agent</c>.</param>
/// <param name="ImportSetting">
/// The <c>read_config_from</c> key that turns the import off, or null when Devin reads the
/// tree natively and it cannot be turned off.
/// </param>
/// <param name="Identities">The duplicated identities, in stable order.</param>
public sealed record DevinImportOverlapEntry(
    string SourceRoot,
    string Kind,
    string? ImportSetting,
    IReadOnlyList<string> Identities);

/// <summary>The outcome of checking a working directory for identities Devin would load twice.</summary>
/// <param name="Overlaps">Each overlapping tree Devin would still load, in stable order.</param>
public sealed record DevinImportOverlapReport(IReadOnlyList<DevinImportOverlapEntry> Overlaps);

/// <summary>
/// Finds the Squad identities Devin would load twice in one workspace, so <c>squad doctor</c>
/// can say so before an operator discovers it as two <c>code-reviewer</c> profiles.
/// </summary>
/// <remarks>
/// <para>
/// <c>DevinRenderer</c> writes only <c>.devin/agents/</c> and <c>.devin/skills/</c>, but Devin
/// reads more than its own tree. It loads <c>.agents/agents/</c> and <c>.agents/skills/</c>
/// natively — where <c>AntigravityRenderer</c> writes — and by default imports other tools'
/// trees through <c>read_config_from</c>: <c>.claude/skills/</c> under <c>claude</c>,
/// <c>.github/skills/</c> under <c>copilot</c>, and <c>.windsurf/skills/</c> under
/// <c>windsurf</c> (docs.devin.ai/cli/reference/configuration/read-config-from, read
/// 2026-09-27). The extensibility overview also lists custom subagents among what the
/// <c>.claude/</c> import brings in, while the import reference does not, so
/// <c>.claude/agents/</c> is reported too: a false warning costs a line of output, a missed
/// duplicate costs an agent the operator did not choose.
/// </para>
/// <para>
/// This is a report, not a fix. Writing Devin's configuration to switch an import off would
/// also switch off what else that import carries — the <c>claude</c> key covers
/// <c>CLAUDE.md</c> rules and Claude's MCP servers as well as its skills — and that trade is
/// the operator's to make. A setting is read from <c>.devin/config.local.json</c>, then
/// <c>.devin/config.json</c>, then the user <c>config.json</c>, the first file that declares
/// it winning, which is the order in which Devin documents local configuration overriding
/// project configuration. Only project-scope trees are inspected: a global deployment's
/// overlap depends on every workspace it meets, which one working directory cannot answer.
/// </para>
/// </remarks>
public static class DevinImportOverlap
{
    private const string SkillKind = "skill";
    private const string AgentKind = "agent";

    /// <summary>The file names Devin accepts inside an agent directory, in its precedence order.</summary>
    private static readonly string[] AgentDefinitionFileNames = ["AGENT.md", "AGENTS.md", "agent.md", "agents.md"];

    private static readonly (string Root, string Kind, string? Setting)[] ImportedRoots =
    [
        (".agents/agents", AgentKind, null),
        (".agents/skills", SkillKind, null),
        (".claude/agents", AgentKind, "claude"),
        (".claude/skills", SkillKind, "claude"),
        (".github/skills", SkillKind, "copilot"),
        (".windsurf/skills", SkillKind, "windsurf"),
    ];

    /// <summary>
    /// Reports every tree Devin would load that repeats an identity <c>.devin/</c> defines,
    /// leaving out a tree whose import the effective configuration turns off.
    /// </summary>
    /// <param name="workingDirectory">The workspace a Devin session would open.</param>
    /// <param name="devinUserRoot">The Devin user configuration directory, or null when it cannot be resolved.</param>
    /// <param name="readFileText">Reads a file's text, or null when absent or unreadable.</param>
    public static DevinImportOverlapReport Inspect(
        string workingDirectory,
        string? devinUserRoot,
        Func<string, string?> readFileText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(readFileText);

        string root = Path.GetFullPath(workingDirectory);
        HashSet<string> devinSkills = ReadIdentities(Path.Combine(root, ".devin", "skills"), SkillKind);
        HashSet<string> devinAgents = ReadIdentities(Path.Combine(root, ".devin", "agents"), AgentKind);

        List<string> configPaths =
        [
            Path.Combine(root, ".devin", "config.local.json"),
            Path.Combine(root, ".devin", "config.json")
        ];
        if (devinUserRoot is not null)
        {
            configPaths.Add(Path.Combine(devinUserRoot, "config.json"));
        }

        List<DevinImportOverlapEntry> overlaps = [];
        foreach ((string relativeRoot, string kind, string? setting) in ImportedRoots)
        {
            if (setting is not null && IsImportDisabled(setting, configPaths, readFileText))
            {
                continue;
            }

            HashSet<string> native = kind == SkillKind ? devinSkills : devinAgents;
            string[] duplicated =
            [
                .. ReadIdentities(Path.Combine(root, relativeRoot.Replace('/', Path.DirectorySeparatorChar)), kind)
                    .Where(native.Contains)
                    .Order(StringComparer.Ordinal)
            ];

            if (duplicated.Length > 0)
            {
                overlaps.Add(new DevinImportOverlapEntry(relativeRoot, kind, setting, duplicated));
            }
        }

        return new DevinImportOverlapReport(overlaps);
    }

    /// <summary>
    /// A skill is a directory holding <c>SKILL.md</c>; an agent is a directory holding one of
    /// Devin's accepted definition names, or a flat <c>&lt;name&gt;.md</c>. Identities come
    /// from paths rather than frontmatter, which is how every Squad renderer names them.
    /// </summary>
    private static HashSet<string> ReadIdentities(string directory, string kind)
    {
        HashSet<string> identities = new(StringComparer.Ordinal);
        if (!Directory.Exists(directory))
        {
            return identities;
        }

        string[] definitionNames = kind == SkillKind ? ["SKILL.md"] : AgentDefinitionFileNames;
        foreach (string child in Directory.EnumerateDirectories(directory))
        {
            if (definitionNames.Any(name => File.Exists(Path.Combine(child, name))))
            {
                identities.Add(Path.GetFileName(child));
            }
        }

        if (kind == AgentKind)
        {
            foreach (string file in Directory.EnumerateFiles(directory, "*.md"))
            {
                identities.Add(Path.GetFileNameWithoutExtension(file));
            }
        }

        return identities;
    }

    private static bool IsImportDisabled(
        string setting,
        IReadOnlyList<string> configPaths,
        Func<string, string?> readFileText)
    {
        foreach (string path in configPaths)
        {
            bool? declared = ReadImportSetting(readFileText(path), setting);
            if (declared is not null)
            {
                return declared == false;
            }
        }

        return false;
    }

    /// <summary>
    /// Reads <c>read_config_from.&lt;setting&gt;</c> as a boolean. A malformed document, or a
    /// value that is not a boolean, declares nothing rather than throwing: a doctor check that
    /// crashes on a config it cannot parse is worse than one that reports what it can see.
    /// </summary>
    private static bool? ReadImportSetting(string? text, string setting)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(
                text,
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("read_config_from", out JsonElement imports) ||
                imports.ValueKind != JsonValueKind.Object ||
                !imports.TryGetProperty(setting, out JsonElement value))
            {
                return null;
            }

            return value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
