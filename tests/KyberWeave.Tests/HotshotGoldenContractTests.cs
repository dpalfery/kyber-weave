using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using KyberWeave.Core.Skills.Model;
using KyberWeave.Core.Skills.Parsing;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Packaging;
using KyberWeave.Core.Squad.Parsing;
using KyberWeave.Core.Squad.Rendering;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the designated Hotshot Copilot tree independently of Kyber-Squad's canonical source.
/// </summary>
/// <remarks>
/// The checked-in manifest makes the ordinary suite deterministic and machine-portable. The
/// opt-in external check proves local provenance without embedding one developer's checkout path
/// or making CI depend on a second repository.
/// </remarks>
public sealed partial class HotshotGoldenContractTests
{
    private const int ExpectedAgentCount = 24;
    private const int ExpectedRenderedSkillCount = 24;
    private const int ExpectedCanonicalResourceCount = 64;
    private const int ExpectedCanonicalSkillTreeFileCount = 88;
    private const int ExpectedCopilotRenderFileCount = 48;
    private const string ExpectedSchema = "kyber-squad.hotshot-golden/v1";
    private const string ExpectedSourceCommit = "677c3a876ba9c62f1083608596b238c9deaff167";
    private const string ExternalGoldenRootVariable = "KYBER_SQUAD_HOTSHOT_GOLDEN_ROOT";
    private const string MaiCodeCopilotModel = "MAI-Code-1.1-Flash (copilot)";

    private static readonly string[] ApprovedToolOrder =
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
        "vscodeGeneral/rename"
    ];

    private static readonly string[] ExpectedCollisionIdentities =
    [
        "csharp-dev",
        "dal-dev",
        "github-devops",
        "maui-dev",
        "product-owner",
        "python-dev",
        "test-dev"
    ];

    private static string ProductRoot =>
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");

    private static string ManifestPath =>
        Path.Combine(
            KyberWeaveTestPaths.ToolRoot,
            "tests",
            "KyberWeave.Tests",
            "Fixtures",
            "kyber-squad-hotshot-golden.json");

    [Fact]
    public void CheckedInManifestPinsExactHotshotGoldenInventory()
    {
        HotshotGoldenManifest manifest = ReadManifest();
        List<string> mismatches = [];

        AddMismatch(mismatches, "schema", ExpectedSchema, manifest.Schema);
        AddMismatch(mismatches, "source commit", ExpectedSourceCommit, manifest.SourceCommit);
        AddSequenceMismatch(mismatches, "approved tool order", ApprovedToolOrder, manifest.ApprovedToolOrder);
        AddSequenceMismatch(
            mismatches,
            "collision identities",
            ExpectedCollisionIdentities,
            manifest.ExpectedCollisionIdentities);

        string[] agentNames = manifest.Agents
            .Select(entry => AgentName(entry.Path))
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] skillNames = manifest.Skills
            .Select(entry => SkillName(entry.Path))
            .Order(StringComparer.Ordinal)
            .ToArray();
        AddMismatch(mismatches, "agent count", ExpectedAgentCount, manifest.Agents.Count);
        AddMismatch(mismatches, "rendered skill count", ExpectedRenderedSkillCount, manifest.Skills.Count);
        AddMismatch(
            mismatches,
            "canonical resource count",
            ExpectedCanonicalResourceCount,
            manifest.CanonicalResources.Count);
        AddMismatch(
            mismatches,
            "canonical skill tree file count",
            ExpectedCanonicalSkillTreeFileCount,
            manifest.Skills.Count + manifest.CanonicalResources.Count);
        AddMismatch(
            mismatches,
            "rendered file count",
            ExpectedCopilotRenderFileCount,
            manifest.Agents.Select(entry => entry.Path).Concat(manifest.Skills.Select(entry => entry.Path)).Distinct(StringComparer.Ordinal).Count());

        string[] duplicateResources = manifest.CanonicalResources
            .GroupBy(path => path, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicateResources.Length > 0)
        {
            mismatches.Add($"canonical resources contain duplicates: {string.Join(", ", duplicateResources)}");
        }

        foreach (string resource in manifest.CanonicalResources)
        {
            if (!resource.StartsWith("skills/", StringComparison.Ordinal) ||
                resource.EndsWith("/SKILL.md", StringComparison.Ordinal))
            {
                mismatches.Add($"invalid supplemental canonical resource path '{resource}'");
            }
        }

        string[] collisions = agentNames.Intersect(skillNames, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        AddSequenceMismatch(mismatches, "agent/skill collisions", ExpectedCollisionIdentities, collisions);
        if (skillNames.Contains("conductor", StringComparer.Ordinal) ||
            skillNames.Contains("conductor-v3", StringComparer.Ordinal))
        {
            mismatches.Add("golden skills unexpectedly contain conductor or conductor-v3");
        }

        ValidateTaskReviewer(manifest, "task-reviewer", mismatches);
        ValidateTaskReviewer(manifest, "task-reviewer-v3", mismatches);
        ValidateConductorDelegation(manifest, "conductor", "task-reviewer", "task-reviewer-v3", mismatches);
        ValidateConductorDelegation(manifest, "conductor-v3", "task-reviewer-v3", "task-reviewer", mismatches);

        foreach (GoldenAgentEntry entry in manifest.Agents)
        {
            string name = AgentName(entry.Path);
            AddMismatch(mismatches, $"{entry.Path} name", name, RequireJsonString(entry.Frontmatter, "name"));
            RequireJsonString(entry.Frontmatter, "description");
            string[] tools = RequireJsonStrings(entry.Frontmatter, "tools");
            string[] unknownTools = tools.Except(ApprovedToolOrder, StringComparer.Ordinal).ToArray();
            if (unknownTools.Length > 0)
            {
                mismatches.Add($"{entry.Path} contains unknown tools: {string.Join(", ", unknownTools)}");
            }

            ValidateSha256(mismatches, $"{entry.Path} body", entry.BodySha256);
            ValidateSha256(mismatches, $"{entry.Path} file", entry.FileSha256);
        }

        foreach (GoldenSkillEntry entry in manifest.Skills)
        {
            ValidateSha256(mismatches, entry.Path, entry.FileSha256);
        }

        Assert.Empty(mismatches);
    }

    [Fact]
    public void CanonicalSourceMatchesCheckedInHotshotGoldenContract()
    {
        HotshotGoldenManifest manifest = ReadManifest();
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        List<string> mismatches = [];

        string[] expectedAgents = manifest.Agents.Select(entry => AgentName(entry.Path)).Order(StringComparer.Ordinal).ToArray();
        string[] expectedSkills = manifest.Skills.Select(entry => SkillName(entry.Path)).Order(StringComparer.Ordinal).ToArray();
        AddSequenceMismatch(mismatches, "canonical agents", expectedAgents, source.Agents.Select(agent => agent.Name));
        AddSequenceMismatch(mismatches, "bundle agents", expectedAgents, source.Bundle.AgentNames.Order(StringComparer.Ordinal));
        AddSequenceMismatch(mismatches, "canonical skills", expectedSkills, source.Skills.Select(skill => skill.Name));
        AddSequenceMismatch(mismatches, "bundle skills", expectedSkills, source.Bundle.SkillNames.Order(StringComparer.Ordinal));

        Dictionary<string, SquadAgent> actualAgents = source.Agents.ToDictionary(agent => agent.Name, StringComparer.Ordinal);
        foreach (GoldenAgentEntry expected in manifest.Agents)
        {
            string name = AgentName(expected.Path);
            if (!actualAgents.TryGetValue(name, out SquadAgent? actual))
            {
                mismatches.Add($"missing canonical agent '{name}'");
                continue;
            }

            AddMismatch(mismatches, $"{name} description", RequireJsonString(expected.Frontmatter, "description"), actual.Description);
            AddMismatch(mismatches, $"{name} body SHA-256", expected.BodySha256, actual.BodyDigest);
            AddMismatch(mismatches, $"{name} capability profile", RequireMetadata(expected.Frontmatter, "capability-profile"), actual.CapabilityProfile);
            AddMismatch(mismatches, $"{name} fallback", RequireMetadata(expected.Frontmatter, "fallback"), actual.Fallback);
            AddSequenceMismatch(mismatches, $"{name} delegation", ExpectedDelegates(expected.Frontmatter), actual.DelegatesTo);
            AddSequenceMismatch(mismatches, $"{name} aliases", ExpectedAliases(expected.Frontmatter), actual.Aliases);

            SquadInvocation expectedInvocation = expected.Frontmatter.TryGetProperty("user-invocable", out JsonElement userInvocable) &&
                userInvocable.ValueKind == JsonValueKind.False
                ? SquadInvocation.Subagent
                : SquadInvocation.Primary;
            AddMismatch(mismatches, $"{name} invocation", expectedInvocation.ToString(), actual.Invocation.ToString());

            string? expectedModel = OptionalJsonString(expected.Frontmatter, "model");
            string? actualModel = ResolveCopilotModel(source, actual);
            AddMismatch(mismatches, $"{name} model", expectedModel ?? "<omitted>", actualModel ?? "<omitted>");
        }

        string skillRoot = Path.Combine(ProductRoot, "skills");
        string[] actualSkillFiles = Directory.EnumerateFiles(skillRoot, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(ProductRoot, path).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] expectedSkillFiles = manifest.Skills
            .Select(entry => entry.Path.Replace(".github/", string.Empty, StringComparison.Ordinal))
            .Concat(manifest.CanonicalResources)
            .Order(StringComparer.Ordinal)
            .ToArray();
        AddSequenceMismatch(mismatches, "recursive canonical skill files", expectedSkillFiles, actualSkillFiles);

        foreach (GoldenSkillEntry expected in manifest.Skills)
        {
            string canonicalPath = Path.Combine(
                ProductRoot,
                expected.Path.Replace(".github/", string.Empty, StringComparison.Ordinal).Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(canonicalPath))
            {
                mismatches.Add($"missing canonical skill file '{expected.Path}'");
                continue;
            }

            AddMismatch(
                mismatches,
                $"{expected.Path} bytes",
                expected.FileSha256,
                Sha256(File.ReadAllBytes(canonicalPath)));
        }

        string[] actualCollisions = source.Agents.Select(agent => agent.Name)
            .Intersect(source.Skills.Select(skill => skill.Name), StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        AddSequenceMismatch(mismatches, "canonical collision identities", ExpectedCollisionIdentities, actualCollisions);

        Assert.True(
            mismatches.Count == 0,
            "Canonical source does not match the Hotshot golden contract:\n" + string.Join("\n", mismatches));
    }

    [Fact]
    public void RecursivePackagesRetainEveryCanonicalSkillResourceAndResolveLocalReferences()
    {
        HotshotGoldenManifest manifest = ReadManifest();
        using TempDirectory temp = new();
        string apmArchivePath = SquadPacker.PackApm(ProductRoot, temp.Path, "retention-contract");
        string pluginArchivePath = SquadPacker.PackPlugins(ProductRoot, temp.Path, "retention-contract");
        List<string> mismatches = [];

        VerifyArchiveSkillResources(apmArchivePath, manifest, mismatches);
        VerifyArchiveSkillResources(pluginArchivePath, manifest, mismatches);
        VerifyLocalSkillReferences(ProductRoot, manifest, "canonical source", mismatches);

        string extractedPluginRoot = Path.Combine(temp.Path, "plugin");
        ZipFile.ExtractToDirectory(pluginArchivePath, extractedPluginRoot);
        VerifyLocalSkillReferences(extractedPluginRoot, manifest, "Agent Plugins package", mismatches);

        Assert.True(
            mismatches.Count == 0,
            "Canonical skill resource retention failed:\n" + string.Join("\n", mismatches));
    }

    [Fact]
    public async Task CopilotRenderMatchesCheckedInHotshotGoldenContract()
    {
        HotshotGoldenManifest manifest = ReadManifest();

        List<string> mismatches = await RenderMismatchesAsync(manifest);

        Assert.True(
            mismatches.Count == 0,
            "Copilot render does not match the Hotshot golden contract:\n" + string.Join("\n", mismatches));
    }

    [Fact]
    public async Task ExternalHotshotGoldenTreeMatchesManifestAndCopilotRender()
    {
        string? configuredRoot = Environment.GetEnvironmentVariable(ExternalGoldenRootVariable);
        if (string.IsNullOrWhiteSpace(configuredRoot))
        {
            return;
        }

        string goldenRoot = Path.GetFullPath(configuredRoot);
        HotshotGoldenManifest manifest = ReadManifest();
        List<string> mismatches = [];

        string[] expectedPaths = manifest.Agents.Select(entry => entry.Path)
            .Concat(manifest.Skills.Select(entry => entry.Path))
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] actualPaths = EnumerateExternalGoldenFiles(goldenRoot);
        AddSequenceMismatch(mismatches, "external golden paths", expectedPaths, actualPaths);

        foreach (GoldenAgentEntry expected in manifest.Agents)
        {
            string path = ResolveExternalPath(goldenRoot, expected.Path);
            if (!File.Exists(path))
            {
                continue;
            }

            string normalized = NormalizeLf(await File.ReadAllTextAsync(path));
            (YamlMappingNode frontmatter, string body) = SplitFrontmatter(normalized, expected.Path);
            CompareFrontmatter(mismatches, expected.Path, expected.Frontmatter, frontmatter, normalizeToolOrder: false, manifest.ApprovedToolOrder);
            AddMismatch(mismatches, $"{expected.Path} body SHA-256", expected.BodySha256, Sha256(body));
            AddMismatch(mismatches, $"{expected.Path} file SHA-256", expected.FileSha256, Sha256(await File.ReadAllBytesAsync(path)));
        }

        foreach (GoldenSkillEntry expected in manifest.Skills)
        {
            string path = ResolveExternalPath(goldenRoot, expected.Path);
            if (File.Exists(path))
            {
                AddMismatch(
                    mismatches,
                    $"{expected.Path} file SHA-256",
                    expected.FileSha256,
                    Sha256(await File.ReadAllBytesAsync(path)));
            }
        }

        mismatches.AddRange(await RenderMismatchesAsync(manifest));

        Assert.True(
            mismatches.Count == 0,
            "External Hotshot parity failed:\n" + string.Join("\n", mismatches));
    }

    private static async Task<List<string>> RenderMismatchesAsync(HotshotGoldenManifest manifest)
    {
        SquadRendererRegistry registry = new([new CopilotRenderer()]);
        SquadRenderResult result = await registry.RenderAsync(new SquadRenderRequest(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Copilot],
            Scope: SquadDeploymentScope.Project));
        List<string> mismatches = [];
        if (!result.Success)
        {
            mismatches.AddRange(result.Errors.Select(error => $"render error: {error}"));
            return mismatches;
        }

        Dictionary<string, SquadDeploymentFile> files = result.Files.ToDictionary(file => file.RelativePath, StringComparer.Ordinal);
        string[] expectedPaths = manifest.Agents.Select(entry => entry.Path)
            .Concat(manifest.Skills.Select(entry => entry.Path))
            .Order(StringComparer.Ordinal)
            .ToArray();
        AddSequenceMismatch(mismatches, "rendered paths", expectedPaths, files.Keys.Order(StringComparer.Ordinal));

        foreach (GoldenAgentEntry expected in manifest.Agents)
        {
            if (!files.TryGetValue(expected.Path, out SquadDeploymentFile? file))
            {
                continue;
            }

            string normalized = NormalizeLf(Encoding.UTF8.GetString(file.Content.Span));
            (YamlMappingNode frontmatter, string body) = SplitFrontmatter(normalized, expected.Path);
            CompareFrontmatter(mismatches, expected.Path, expected.Frontmatter, frontmatter, normalizeToolOrder: true, manifest.ApprovedToolOrder);
            AddMismatch(mismatches, $"{expected.Path} body SHA-256", expected.BodySha256, Sha256(body));
        }

        foreach (GoldenSkillEntry expected in manifest.Skills)
        {
            if (files.TryGetValue(expected.Path, out SquadDeploymentFile? file))
            {
                AddMismatch(
                    mismatches,
                    $"{expected.Path} bytes",
                    expected.FileSha256,
                    Sha256(file.Content.Span));
            }
        }

        return mismatches;
    }

    private static void VerifyArchiveSkillResources(
        string archivePath,
        HotshotGoldenManifest manifest,
        List<string> mismatches)
    {
        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        HashSet<string> entryNames = archive.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.Ordinal);
        foreach (string resource in manifest.CanonicalResources)
        {
            if (!entryNames.Contains(resource))
            {
                mismatches.Add($"{Path.GetFileName(archivePath)} omits '{resource}'");
            }
        }
    }

    private static void VerifyLocalSkillReferences(
        string root,
        HotshotGoldenManifest manifest,
        string subject,
        List<string> mismatches)
    {
        foreach (GoldenSkillEntry skillEntry in manifest.Skills)
        {
            string relativeSkillPath = skillEntry.Path.Replace(".github/", string.Empty, StringComparison.Ordinal);
            string skillPath = Path.Combine(root, relativeSkillPath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(skillPath))
            {
                mismatches.Add($"{subject} omits '{relativeSkillPath}'");
                continue;
            }

            Skill skill = SkillParser.ParseFile(skillPath);
            HashSet<string> references = skill.ReferenceLinks.Select(link => link.Target).ToHashSet(StringComparer.Ordinal);
            foreach (Match match in LocalResourcePathRegex().Matches(skill.InstructionsBody))
            {
                references.Add(match.Groups["path"].Value);
            }

            foreach (string reference in references.Order(StringComparer.Ordinal))
            {
                string resolvedPath = Path.Combine(skill.DirectoryPath, reference.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(resolvedPath) && !Directory.Exists(resolvedPath))
                {
                    mismatches.Add($"{subject} skill '{skill.Frontmatter.Name}' has unresolved local resource '{reference}'");
                }
            }
        }
    }

    private static void CompareFrontmatter(
        List<string> mismatches,
        string path,
        JsonElement expected,
        YamlMappingNode actual,
        bool normalizeToolOrder,
        IReadOnlyList<string> approvedToolOrder)
    {
        string[] expectedKeys = expected.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray();
        string[] actualKeys = actual.Children.Keys.Select(RequireYamlScalar).Order(StringComparer.Ordinal).ToArray();
        AddSequenceMismatch(mismatches, $"{path} frontmatter keys", expectedKeys, actualKeys);

        foreach (JsonProperty property in expected.EnumerateObject())
        {
            if (!actual.Children.TryGetValue(new YamlScalarNode(property.Name), out YamlNode? actualValue))
            {
                continue;
            }

            if (normalizeToolOrder && property.Name == "tools")
            {
                HashSet<string> membership = RequireJsonStrings(expected, "tools").ToHashSet(StringComparer.Ordinal);
                string[] normalizedTools = approvedToolOrder.Where(membership.Contains).ToArray();
                AddSequenceMismatch(
                    mismatches,
                    $"{path} tools",
                    normalizedTools,
                    RequireYamlSequence(actualValue, "tools").Children.Select(RequireYamlScalar));
                continue;
            }

            string expectedCanonical = CanonicalJson(property.Value);
            string actualCanonical = CanonicalYaml(actualValue);
            AddMismatch(mismatches, $"{path} frontmatter.{property.Name}", expectedCanonical, actualCanonical);
        }
    }

    private static string CanonicalJson(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", element.EnumerateObject()
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .Select(property => JsonSerializer.Serialize(property.Name) + ":" + CanonicalJson(property.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", element.EnumerateArray().Select(CanonicalJson)) + "]",
        JsonValueKind.String => JsonSerializer.Serialize(element.GetString()),
        JsonValueKind.True => JsonSerializer.Serialize("true"),
        JsonValueKind.False => JsonSerializer.Serialize("false"),
        JsonValueKind.Null => "null",
        _ => JsonSerializer.Serialize(element.GetRawText())
    };

    private static string CanonicalYaml(YamlNode node) => node switch
    {
        YamlMappingNode mapping => "{" + string.Join(",", mapping.Children
            .OrderBy(pair => RequireYamlScalar(pair.Key), StringComparer.Ordinal)
            .Select(pair => JsonSerializer.Serialize(RequireYamlScalar(pair.Key)) + ":" + CanonicalYaml(pair.Value))) + "}",
        YamlSequenceNode sequence => "[" + string.Join(",", sequence.Children.Select(CanonicalYaml)) + "]",
        YamlScalarNode scalar when scalar.Value is null => "null",
        YamlScalarNode scalar => JsonSerializer.Serialize(scalar.Value),
        _ => throw new InvalidDataException($"Unsupported YAML node type '{node.GetType().Name}'.")
    };

    private static string[] EnumerateExternalGoldenFiles(string root)
    {
        string agentsRoot = Path.Combine(root, ".github", "agents");
        string skillsRoot = Path.Combine(root, ".github", "skills");
        return Directory.EnumerateFiles(agentsRoot, "*", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(skillsRoot, "*", SearchOption.AllDirectories))
            .Select(path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static string ResolveExternalPath(string root, string relativePath)
    {
        string fullPath = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        string normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        Assert.StartsWith(normalizedRoot, fullPath, StringComparison.Ordinal);
        return fullPath;
    }

    private static (YamlMappingNode Frontmatter, string Body) SplitFrontmatter(string text, string path)
    {
        const string delimiter = "---\n";
        if (!text.StartsWith(delimiter, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"'{path}' has no opening YAML frontmatter delimiter.");
        }

        int end = text.IndexOf("\n---\n", delimiter.Length, StringComparison.Ordinal);
        if (end < 0)
        {
            throw new InvalidDataException($"'{path}' has no closing YAML frontmatter delimiter.");
        }

        string yaml = text[delimiter.Length..(end + 1)];
        string body = text[(end + 5)..];
        YamlStream stream = new();
        stream.Load(new StringReader(yaml));
        return (Assert.IsType<YamlMappingNode>(stream.Documents.Single().RootNode), body);
    }

    private static string? ResolveCopilotModel(SquadSource source, SquadAgent agent)
    {
        SquadModelProfile profile = source.ModelProfiles.Profiles[agent.ModelProfile];
        if (profile.HarnessModels.TryGetValue("copilot", out string? model))
        {
            return model;
        }

        return string.Equals(profile.Default, "inherit", StringComparison.Ordinal) ? null : profile.Default;
    }

    private static void ValidateTaskReviewer(HotshotGoldenManifest manifest, string name, List<string> mismatches)
    {
        GoldenAgentEntry[] entries = manifest.Agents.Where(candidate => AgentName(candidate.Path) == name).ToArray();
        if (entries.Length != 1)
        {
            mismatches.Add($"expected one golden agent '{name}', found {entries.Length}");
            return;
        }

        AddMismatch(mismatches, $"{name} model", MaiCodeCopilotModel, RequireJsonString(entries[0].Frontmatter, "model"));
    }

    private static void ValidateConductorDelegation(
        HotshotGoldenManifest manifest,
        string conductor,
        string requiredReviewer,
        string forbiddenReviewer,
        List<string> mismatches)
    {
        GoldenAgentEntry entry = manifest.Agents.Single(candidate => AgentName(candidate.Path) == conductor);
        string[] delegates = ExpectedDelegates(entry.Frontmatter);
        if (!delegates.Contains(requiredReviewer, StringComparer.Ordinal))
        {
            mismatches.Add($"{conductor} does not delegate to {requiredReviewer}");
        }

        if (delegates.Contains(forbiddenReviewer, StringComparer.Ordinal))
        {
            mismatches.Add($"{conductor} unexpectedly delegates to {forbiddenReviewer}");
        }
    }

    private static HotshotGoldenManifest ReadManifest()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(ManifestPath));
        JsonElement root = document.RootElement;
        GoldenAgentEntry[] agents = root.GetProperty("agents").EnumerateArray()
            .Select(entry => new GoldenAgentEntry(
                entry.GetProperty("path").GetString() ?? throw new InvalidDataException("Golden agent path is not a string."),
                entry.GetProperty("frontmatter").Clone(),
                entry.GetProperty("body_sha256").GetString() ?? throw new InvalidDataException("Golden body digest is not a string."),
                entry.GetProperty("file_sha256").GetString() ?? throw new InvalidDataException("Golden agent file digest is not a string.")))
            .ToArray();
        GoldenSkillEntry[] skills = root.GetProperty("skills").EnumerateArray()
            .Select(entry => new GoldenSkillEntry(
                entry.GetProperty("path").GetString() ?? throw new InvalidDataException("Golden skill path is not a string."),
                entry.GetProperty("file_sha256").GetString() ?? throw new InvalidDataException("Golden skill file digest is not a string.")))
            .ToArray();

        return new HotshotGoldenManifest(
            root.GetProperty("schema").GetString() ?? throw new InvalidDataException("Golden schema is not a string."),
            root.GetProperty("source_commit").GetString() ?? throw new InvalidDataException("Golden source commit is not a string."),
            RequireJsonStrings(root, "approved_tool_order"),
            RequireJsonStrings(root, "expected_collision_identities"),
            RequireJsonStrings(root, "canonical_resources"),
            agents,
            skills);
    }

    private static string[] ExpectedDelegates(JsonElement frontmatter)
    {
        string? value = OptionalMetadata(frontmatter, "delegates-to");
        return value is null
            ? []
            : value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    private static string[] ExpectedAliases(JsonElement frontmatter)
    {
        string? value = OptionalMetadata(frontmatter, "aliases");
        return value is null
            ? []
            : value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    private static string RequireMetadata(JsonElement frontmatter, string key) =>
        OptionalMetadata(frontmatter, key) ?? throw new InvalidDataException($"Golden frontmatter metadata omits '{key}'.");

    private static string? OptionalMetadata(JsonElement frontmatter, string key)
    {
        if (!frontmatter.TryGetProperty("metadata", out JsonElement metadata) ||
            !metadata.TryGetProperty(key, out JsonElement value))
        {
            return null;
        }

        return value.GetString();
    }

    private static string RequireJsonString(JsonElement element, string key) =>
        OptionalJsonString(element, key) ?? throw new InvalidDataException($"Golden object omits string '{key}'.");

    private static string? OptionalJsonString(JsonElement element, string key) =>
        element.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string[] RequireJsonStrings(JsonElement element, string key)
    {
        if (!element.TryGetProperty(key, out JsonElement value) || value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"Golden object omits array '{key}'.");
        }

        return value.EnumerateArray()
            .Select(item => item.GetString() ?? throw new InvalidDataException($"Golden array '{key}' contains a non-string."))
            .ToArray();
    }

    private static YamlSequenceNode RequireYamlSequence(YamlNode node, string key) =>
        node as YamlSequenceNode ?? throw new InvalidDataException($"Rendered frontmatter '{key}' is not a sequence.");

    private static string RequireYamlScalar(YamlNode node) =>
        (node as YamlScalarNode)?.Value ?? throw new InvalidDataException("Rendered YAML contains a non-scalar key or value.");

    private static string AgentName(string path) =>
        Path.GetFileName(path)[..^".agent.md".Length];

    private static string SkillName(string path) =>
        path.Split('/', StringSplitOptions.RemoveEmptyEntries)[2];

    private static string NormalizeLf(string text) =>
        text.TrimStart('\uFEFF').Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static string Sha256(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void ValidateSha256(List<string> mismatches, string subject, string digest)
    {
        if (digest.Length != 64 || digest.Any(character => !Uri.IsHexDigit(character)))
        {
            mismatches.Add($"{subject} has invalid SHA-256 '{digest}'");
        }
    }

    private static void AddMismatch(List<string> mismatches, string subject, string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            mismatches.Add($"{subject}: expected '{expected}', actual '{actual}'");
        }
    }

    private static void AddMismatch(List<string> mismatches, string subject, int expected, int actual)
    {
        if (expected != actual)
        {
            mismatches.Add($"{subject}: expected '{expected}', actual '{actual}'");
        }
    }

    private static void AddSequenceMismatch(
        List<string> mismatches,
        string subject,
        IEnumerable<string> expected,
        IEnumerable<string> actual)
    {
        string[] expectedArray = expected.ToArray();
        string[] actualArray = actual.ToArray();
        if (!expectedArray.SequenceEqual(actualArray, StringComparer.Ordinal))
        {
            mismatches.Add(
                $"{subject}: expected [{string.Join(", ", expectedArray)}], actual [{string.Join(", ", actualArray)}]");
        }
    }

    private readonly record struct GoldenAgentEntry(
        string Path,
        JsonElement Frontmatter,
        string BodySha256,
        string FileSha256);

    private readonly record struct GoldenSkillEntry(string Path, string FileSha256);

    private readonly record struct HotshotGoldenManifest(
        string Schema,
        string SourceCommit,
        IReadOnlyList<string> ApprovedToolOrder,
        IReadOnlyList<string> ExpectedCollisionIdentities,
        IReadOnlyList<string> CanonicalResources,
        IReadOnlyList<GoldenAgentEntry> Agents,
        IReadOnlyList<GoldenSkillEntry> Skills);

    [GeneratedRegex(@"(?<![A-Za-z0-9._\-/])(?:\./)?(?<path>(?:scripts|references|assets|providers|agents)/[A-Za-z0-9._\-/]+)", RegexOptions.CultureInvariant)]
    private static partial Regex LocalResourcePathRegex();
}
