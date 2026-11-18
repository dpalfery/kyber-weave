using System.Text.Json;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.FileSystem;

namespace KyberWeave.Core.Squad.Deployment;

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
/// <param name="files">Lists the skill and agent trees and reads Devin's configuration files.</param>
/// <param name="paths">Turns the working directory into a full path.</param>
/// <param name="environment">Supplies the base directory a relative working directory resolves against.</param>
public sealed class DevinImportOverlap(
    IFileSystem files,
    IPathResolver paths,
    IProcessEnvironment environment) : IDevinImportOverlapInspector
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
    public DevinImportOverlapReport Inspect(string workingDirectory, string? devinUserRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        string root = paths.GetFullPath(workingDirectory, environment.CurrentDirectory);
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
            if (setting is not null && IsImportDisabled(setting, configPaths))
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
    /// <remarks>
    /// Enumeration is lazy, so an unreadable or vanishing directory throws while the loops
    /// advance rather than at the call. Those failures return what was read before them: a
    /// doctor warning built on part of a tree is still true, and a doctor that crashes on one
    /// unreadable folder reports nothing at all.
    /// </remarks>
    private HashSet<string> ReadIdentities(string directory, string kind)
    {
        HashSet<string> identities = new(StringComparer.Ordinal);
        if (!files.DirectoryExists(directory))
        {
            return identities;
        }

        string[] definitionNames = kind == SkillKind ? ["SKILL.md"] : AgentDefinitionFileNames;
        try
        {
            foreach (string child in files.EnumerateDirectories(directory))
            {
                if (definitionNames.Any(name => files.FileExists(Path.Combine(child, name))))
                {
                    identities.Add(Path.GetFileName(child));
                }
            }

            if (kind == AgentKind)
            {
                foreach (string file in files.EnumerateFiles(directory, "*.md", recursive: false))
                {
                    identities.Add(Path.GetFileNameWithoutExtension(file));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Keep the identities read before the failure; see remarks.
        }

        return identities;
    }

    private bool IsImportDisabled(string setting, IReadOnlyList<string> configPaths)
    {
        foreach (string path in configPaths)
        {
            bool? declared = ReadImportSetting(ReadFileTextOrNull(path), setting);
            if (declared is not null)
            {
                return declared == false;
            }
        }

        return false;
    }

    /// <summary>Reads a file's text, or null when it is absent or unreadable: an unreadable setting declares nothing.</summary>
    private string? ReadFileTextOrNull(string path)
    {
        try
        {
            return files.FileExists(path) ? files.ReadAllText(path) : null;
        }
        catch (Exception ex) when (FileFailure.Is(ex))
        {
            return null;
        }
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
