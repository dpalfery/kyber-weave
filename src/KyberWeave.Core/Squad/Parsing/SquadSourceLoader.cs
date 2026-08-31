using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Validation;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace KyberWeave.Core.Squad.Parsing;

/// <summary>Loads and validates the target-neutral source maintained for Kyber-Squad.</summary>
public static class SquadSourceLoader
{
    private const string ManifestSchema = "kyber-squad.squad/v1";
    private const string BundleSchema = "kyber-squad.bundle/v1";
    private const string AgentSchema = "kyber-squad.agent/v1";
    private const string ModelProfilesSchema = "kyber-squad.model-profiles/v1";
    private const string CapabilityProfilesSchema = "kyber-squad.capability-profiles/v1";
    private const string FallbackProfilesSchema = "kyber-squad.fallback-profiles/v1";
    private const string FallbackProfilesSchemaDocument = "schemas/fallback-profiles.schema.json";
    private const string FallbackProfilesSchemaId =
        "https://kyber-weave.dev/schemas/kyber-squad/fallback-profiles/v1";
    private const string ToolchainSchema = "kyber-squad.toolchain/v1";
    private const string AssemblyVersionSource = "kyber-weave-assembly";

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly string[] RequiredSchemaFiles =
    [
        "schemas/squad.schema.json",
        "schemas/bundle.schema.json",
        "schemas/agent.schema.json",
        "schemas/model-profiles.schema.json",
        "schemas/capability-profiles.schema.json",
        FallbackProfilesSchemaDocument
    ];

    private static readonly HashSet<string> ModelProfileFields = new(StringComparer.Ordinal)
    {
        "default", "codex", "cursor", "claude", "copilot", "opencode", "kilo",
        "antigravity", "warp", "factory"
    };

    /// <summary>Loads the default bundle from a canonical product source root.</summary>
    public static SquadSource Load(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        string logicalRoot = Path.GetFullPath(root);
        if (!Directory.Exists(logicalRoot))
        {
            SquadSourceValidator.Throw(
                $"Squad source root '{root}' does not exist.",
                "kyber-squad",
                "squad.yml",
                "Provide a directory containing squad.yml.");
        }

        string resolvedRoot = ResolveExistingPath(logicalRoot).FullPath;
        SourceFile manifestFile = ReadSourceFile(
            logicalRoot,
            resolvedRoot,
            "squad.yml",
            "squad.yml",
            "kyber-squad");
        SquadManifest manifest = ParseManifest(manifestFile);

        ValidateSchemaDocuments(logicalRoot, resolvedRoot);

        if (!manifest.Bundles.TryGetValue(manifest.DefaultBundle, out string? bundlePath))
        {
            SquadSourceValidator.Throw(
                $"Default bundle '{manifest.DefaultBundle}' is not declared in bundles.",
                manifest.Name,
                manifest.SourcePath,
                "Add the default-bundle key to bundles or choose a declared bundle.");
        }

        SquadBundle bundle = ParseBundle(ReadSourceFile(
            logicalRoot,
            resolvedRoot,
            bundlePath,
            manifest.SourcePath,
            manifest.Name));
        if (!string.Equals(bundle.Name, manifest.DefaultBundle, StringComparison.Ordinal))
        {
            SquadSourceValidator.Throw(
                $"Bundle name '{bundle.Name}' does not match manifest key '{manifest.DefaultBundle}'.",
                bundle.Name,
                bundle.SourcePath,
                "Make the bundle name match its canonical manifest key.");
        }

        SquadModelProfiles models = ParseModelProfiles(ReadSourceFile(
            logicalRoot,
            resolvedRoot,
            manifest.Profiles.Models,
            manifest.SourcePath,
            manifest.Name));
        SquadCapabilityProfiles capabilities = ParseCapabilityProfiles(ReadSourceFile(
            logicalRoot,
            resolvedRoot,
            manifest.Profiles.Capabilities,
            manifest.SourcePath,
            manifest.Name));
        SquadFallbackProfiles fallbacks = ParseFallbackProfiles(ReadSourceFile(
            logicalRoot,
            resolvedRoot,
            manifest.Profiles.Fallbacks,
            manifest.SourcePath,
            manifest.Name));
        SquadToolchain toolchain = ParseToolchain(ReadSourceFile(
            logicalRoot,
            resolvedRoot,
            manifest.ToolchainPath,
            manifest.SourcePath,
            manifest.Name));
        JsonElement mcp = ParseMcp(ReadSourceFile(
            logicalRoot,
            resolvedRoot,
            manifest.McpPath,
            manifest.SourcePath,
            manifest.Name));

        IReadOnlyList<SquadAgent> agents = LoadAgents(logicalRoot, resolvedRoot);
        IReadOnlyList<SquadSkill> skills = LoadSkills(logicalRoot, resolvedRoot);
        SquadSourceValidator.Validate(bundle, agents, skills, models, capabilities, fallbacks);

        return new SquadSource(
            resolvedRoot,
            manifest,
            bundle,
            agents,
            skills,
            models,
            capabilities,
            fallbacks,
            toolchain,
            mcp);
    }

    private static SquadManifest ParseManifest(SourceFile file)
    {
        YamlMappingNode root = ParseYamlMapping(file);
        EnsureOnlyFields(
            root,
            ["schema", "name", "version-source", "default-bundle", "bundles", "profiles", "toolchain", "mcp"],
            file.RelativePath);
        string schema = RequireSchema(root, "schema", ManifestSchema, file.RelativePath);
        string name = RequireScalar(root, "name", file.RelativePath);
        YamlNode versionSourceNode = RequireNode(root, "version-source", file.RelativePath);
        string versionSource = RequireScalar(
            versionSourceNode,
            "version-source",
            file.RelativePath,
            nodeIsValue: true);
        if (!string.Equals(versionSource, AssemblyVersionSource, StringComparison.Ordinal))
        {
            SquadSourceValidator.Throw(
                $"Manifest version-source '{versionSource}' is not supported.",
                "version-source",
                file.RelativePath,
                $"Set version-source to the required literal '{AssemblyVersionSource}'.",
                StartLine(versionSourceNode));
        }

        string defaultBundle = RequireScalar(root, "default-bundle", file.RelativePath);
        IReadOnlyDictionary<string, string> bundles = RequireStringMap(root, "bundles", file.RelativePath);
        YamlMappingNode profileNode = RequireMapping(root, "profiles", file.RelativePath);
        EnsureOnlyFields(profileNode, ["models", "capabilities", "fallbacks"], file.RelativePath);
        SquadProfilePaths profiles = new SquadProfilePaths(
            RequireScalar(profileNode, "models", file.RelativePath),
            RequireScalar(profileNode, "capabilities", file.RelativePath),
            RequireScalar(profileNode, "fallbacks", file.RelativePath));

        return new SquadManifest(
            schema,
            name,
            versionSource,
            defaultBundle,
            bundles,
            profiles,
            RequireScalar(root, "toolchain", file.RelativePath),
            RequireScalar(root, "mcp", file.RelativePath),
            file.RelativePath);
    }

    private static SquadBundle ParseBundle(SourceFile file)
    {
        YamlMappingNode root = ParseYamlMapping(file);
        EnsureOnlyFields(root, ["schema", "name", "agents", "skills"], file.RelativePath);
        return new SquadBundle(
            RequireSchema(root, "schema", BundleSchema, file.RelativePath),
            RequireScalar(root, "name", file.RelativePath),
            RequireStringSequence(root, "agents", file.RelativePath),
            RequireStringSequence(root, "skills", file.RelativePath),
            file.RelativePath);
    }

    private static SquadModelProfiles ParseModelProfiles(SourceFile file)
    {
        YamlMappingNode root = ParseYamlMapping(file);
        EnsureOnlyFields(root, ["schema", "profiles"], file.RelativePath);
        string schema = RequireSchema(root, "schema", ModelProfilesSchema, file.RelativePath);
        YamlMappingNode profilesNode = RequireMapping(root, "profiles", file.RelativePath);
        SortedDictionary<string, SquadModelProfile> profiles = new SortedDictionary<string, SquadModelProfile>(StringComparer.Ordinal);

        foreach ((string? name, YamlNode? node) in MappingEntries(profilesNode, file.RelativePath))
        {
            if (profiles.ContainsKey(name))
            {
                ThrowDuplicateKey(name, file.RelativePath);
            }

            YamlMappingNode profile = RequireMapping(node, name, file.RelativePath, nodeIsValue: true);
            EnsureOnlyFields(profile, ModelProfileFields, file.RelativePath);
            string defaultModel = RequireScalar(profile, "default", file.RelativePath);
            SortedDictionary<string, string> harnessModels = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach ((string? key, YamlNode? value) in MappingEntries(profile, file.RelativePath))
            {
                if (!string.Equals(key, "default", StringComparison.Ordinal))
                {
                    harnessModels.Add(key, RequireScalar(value, key, file.RelativePath, nodeIsValue: true));
                }
            }

            profiles.Add(name, new SquadModelProfile(defaultModel, harnessModels));
        }

        return new SquadModelProfiles(schema, profiles, file.RelativePath);
    }

    private static SquadCapabilityProfiles ParseCapabilityProfiles(SourceFile file)
    {
        YamlMappingNode root = ParseYamlMapping(file);
        EnsureOnlyFields(root, ["schema", "capabilities", "profiles"], file.RelativePath);
        string schema = RequireSchema(root, "schema", CapabilityProfilesSchema, file.RelativePath);
        IReadOnlyList<string> vocabulary = RequireStringSequence(root, "capabilities", file.RelativePath);
        EnsureDistinct(vocabulary, "capability", file.RelativePath);
        HashSet<string> known = vocabulary.ToHashSet(StringComparer.Ordinal);
        YamlMappingNode profilesNode = RequireMapping(root, "profiles", file.RelativePath);
        SortedDictionary<string, SquadCapabilityProfile> profiles = new SortedDictionary<string, SquadCapabilityProfile>(StringComparer.Ordinal);

        foreach ((string? name, YamlNode? node) in MappingEntries(profilesNode, file.RelativePath))
        {
            YamlMappingNode profile = RequireMapping(node, name, file.RelativePath, nodeIsValue: true);
            EnsureOnlyFields(profile, ["target", "permissions"], file.RelativePath);
            string? target = TryGetScalar(profile, "target", file.RelativePath);
            if (target is not null)
            {
                RequireLiteral(
                    target,
                    $"profiles.{name}.target",
                    "copilot",
                    file.RelativePath,
                    "Use 'copilot' for a target-specific capability profile or omit target for a shared profile.");
            }

            YamlMappingNode permissionsNode = RequireMapping(profile, "permissions", file.RelativePath);
            SortedDictionary<string, SquadPermissionDecision> permissions = new SortedDictionary<string, SquadPermissionDecision>(StringComparer.Ordinal);

            foreach ((string? capability, YamlNode? decisionNode) in MappingEntries(permissionsNode, file.RelativePath))
            {
                if (!known.Contains(capability))
                {
                    SquadSourceValidator.Throw(
                        $"Capability profile '{name}' uses unknown capability '{capability}'.",
                        name,
                        file.RelativePath,
                        "Declare the capability in the top-level capabilities list or remove the permission.",
                        StartLine(decisionNode));
                }

                string decisionText = RequireScalar(
                    decisionNode,
                    capability,
                    file.RelativePath,
                    nodeIsValue: true);
                SquadPermissionDecision decision = decisionText switch
                {
                    "deny" => SquadPermissionDecision.Deny,
                    "ask" => SquadPermissionDecision.Ask,
                    "allow" => SquadPermissionDecision.Allow,
                    _ => ThrowInvalidDecision(decisionText, name, file.RelativePath, decisionNode)
                };
                permissions.Add(capability, decision);
            }

            string? missingCapability = vocabulary.FirstOrDefault(capability => !permissions.ContainsKey(capability));
            if (missingCapability is not null)
            {
                SquadSourceValidator.Throw(
                    $"Capability profile '{name}' omits permission for declared capability '{missingCapability}'.",
                    name,
                    file.RelativePath,
                    $"Add an explicit permission decision for '{missingCapability}': deny, ask, or allow.",
                    StartLine(permissionsNode));
            }

            profiles.Add(name, new SquadCapabilityProfile(target, permissions));
        }

        return new SquadCapabilityProfiles(schema, vocabulary, profiles, file.RelativePath);
    }

    private static SquadFallbackProfiles ParseFallbackProfiles(SourceFile file)
    {
        YamlMappingNode root = ParseYamlMapping(file);
        EnsureOnlyFields(root, ["schema", "profiles"], file.RelativePath);
        string schema = RequireSchema(root, "schema", FallbackProfilesSchema, file.RelativePath);
        YamlMappingNode profilesNode = RequireMapping(root, "profiles", file.RelativePath);
        SortedDictionary<string, SquadFallbackProfile> profiles = new SortedDictionary<string, SquadFallbackProfile>(StringComparer.Ordinal);

        foreach ((string? name, YamlNode? node) in MappingEntries(profilesNode, file.RelativePath))
        {
            YamlMappingNode profile = RequireMapping(node, name, file.RelativePath, nodeIsValue: true);
            EnsureOnlyFields(
                profile,
                ["no-primary-agent", "no-agent-primitive", "body-source", "output-identity", "shared-identities"],
                file.RelativePath);
            YamlMappingNode? outputIdentity = TryGetMapping(profile, "output-identity", file.RelativePath);
            string bodySource = TryGetScalar(profile, "body-source", file.RelativePath) ?? "agent";
            RequireLiteral(
                bodySource,
                "body-source",
                "agent",
                file.RelativePath,
                "Use the canonical agent instruction body for every role-skill projection.");
            profiles.Add(name, new SquadFallbackProfile(
                RequireFallbackDecision(profile, "no-primary-agent", file.RelativePath),
                RequireFallbackDecision(profile, "no-agent-primitive", file.RelativePath),
                bodySource,
                outputIdentity is null
                    ? new SquadFallbackOutputIdentity(
                        "agent-name",
                        "reuse-skill",
                        "role-prefixed-agent-name",
                        "role-")
                    : ParseFallbackOutputIdentity(outputIdentity, file.RelativePath),
                TryGetStringSequence(profile, "shared-identities", file.RelativePath) ?? []));
        }

        return new SquadFallbackProfiles(schema, profiles, file.RelativePath);
    }

    private static SquadFallbackOutputIdentity ParseFallbackOutputIdentity(
        YamlMappingNode mapping,
        string sourcePath)
    {
        EnsureOnlyFields(mapping, ["unoccupied", "shared", "collision", "prefix"], sourcePath);
        SquadFallbackOutputIdentity identity = new SquadFallbackOutputIdentity(
            RequireScalar(mapping, "unoccupied", sourcePath),
            RequireScalar(mapping, "shared", sourcePath),
            RequireScalar(mapping, "collision", sourcePath),
            RequireScalar(mapping, "prefix", sourcePath));
        RequireLiteral(
            identity.Unoccupied,
            "output-identity.unoccupied",
            "agent-name",
            sourcePath,
            "Use the agent name when no canonical skill occupies the identity.");
        RequireLiteral(
            identity.Shared,
            "output-identity.shared",
            "reuse-skill",
            sourcePath,
            "Reuse the same-name skill only for an explicitly shared identity.");
        RequireLiteral(
            identity.Collision,
            "output-identity.collision",
            "role-prefixed-agent-name",
            sourcePath,
            "Prefix role skills whose canonical skill has a distinct instruction body.");
        RequireLiteral(
            identity.Prefix,
            "output-identity.prefix",
            "role-",
            sourcePath,
            "Use the stable role- prefix for distinct-body collisions.");
        return identity;
    }

    private static SquadToolchain ParseToolchain(SourceFile file)
    {
        YamlMappingNode root = ParseYamlMapping(file);
        EnsureOnlyFields(root, ["schema", "required-features", "validated-release"], file.RelativePath);
        string schema = RequireSchema(root, "schema", ToolchainSchema, file.RelativePath);
        IReadOnlyList<string> requiredFeatures = RequireStringSequence(root, "required-features", file.RelativePath);
        EnsureDistinct(requiredFeatures, "required feature", file.RelativePath);
        YamlNode releaseNode = RequireNode(root, "validated-release", file.RelativePath);
        JsonElement? release = IsYamlNull(releaseNode)
            ? null
            : JsonSerializer.SerializeToElement(ToPlainValue(releaseNode));
        return new SquadToolchain(schema, requiredFeatures, release, file.RelativePath);
    }

    private static JsonElement ParseMcp(SourceFile file)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(file.Content);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                ThrowJson(file.RelativePath, "MCP configuration must be a JSON object.");
            }

            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (!string.Equals(property.Name, "mcpServers", StringComparison.Ordinal))
                {
                    ThrowJson(
                        file.RelativePath,
                        $"MCP configuration contains unknown field '{property.Name}'.");
                }
            }

            if (!root.TryGetProperty("mcpServers", out JsonElement servers) ||
                servers.ValueKind != JsonValueKind.Object)
            {
                ThrowJson(file.RelativePath, "MCP configuration requires an object field 'mcpServers'.");
            }

            foreach (JsonProperty server in servers.EnumerateObject())
            {
                ValidateMcpServer(server, file.RelativePath);
            }

            return root.Clone();
        }
        catch (JsonException exception)
        {
            SquadSourceValidator.Throw(
                $"MCP configuration is not valid JSON: {exception.Message}",
                "mcp",
                file.RelativePath,
                "Correct the JSON syntax and keep the file UTF-8 encoded.",
                exception.LineNumber is null ? null : checked((int)exception.LineNumber.Value + 1));
            throw;
        }
    }

    private static IReadOnlyList<SquadAgent> LoadAgents(string logicalRoot, string resolvedRoot)
    {
        const string directory = "agents";
        string logicalDirectory = Path.Combine(logicalRoot, directory);
        if (!Directory.Exists(logicalDirectory))
        {
            return [];
        }

        EnsureDiscoveredPath(logicalRoot, resolvedRoot, directory, isDirectory: true);
        return Directory.EnumerateFiles(logicalDirectory, "*.md", SearchOption.TopDirectoryOnly)
            .Select(path => ToRelativePath(logicalRoot, path))
            .Order(StringComparer.Ordinal)
            .Select(relativePath => ParseAgent(
                logicalRoot,
                resolvedRoot,
                ReadSourceFile(
                    logicalRoot,
                    resolvedRoot,
                    relativePath,
                    relativePath,
                    relativePath)))
            .OrderBy(agent => agent.Name, StringComparer.Ordinal)
            .ThenByDescending(agent => SourceIdentityMatches(agent.Name, agent.SourcePath, isSkill: false))
            .ThenBy(agent => agent.SourcePath, StringComparer.Ordinal)
            .ToArray();
    }

    private static SquadAgent ParseAgent(string logicalRoot, string resolvedRoot, SourceFile file)
    {
        Frontmatter frontmatter = ParseFrontmatter(file);
        YamlMappingNode root = ParseYamlMapping(file with { Content = frontmatter.Yaml }, frontmatter.LineOffset);
        EnsureOnlyFields(
            root,
            ["schema", "name", "description", "invocation", "model-profile", "capability-profile", "copilot-capability-profile", "copilot-tools", "delegates-to", "fallback", "aliases"],
            file.RelativePath,
            frontmatter.LineOffset);

        string invocationText = RequireScalar(root, "invocation", file.RelativePath, frontmatter.LineOffset);
        SquadInvocation invocation = invocationText switch
        {
            "primary" => SquadInvocation.Primary,
            "subagent" => SquadInvocation.Subagent,
            _ => ThrowInvalidInvocation(invocationText, file.RelativePath)
        };
        IReadOnlyList<string> delegates = RequireStringSequence(root, "delegates-to", file.RelativePath, frontmatter.LineOffset);
        IReadOnlyList<string> aliases = RequireStringSequence(root, "aliases", file.RelativePath, frontmatter.LineOffset);
        IReadOnlyList<string> copilotTools = RequireStringSequence(root, "copilot-tools", file.RelativePath, frontmatter.LineOffset);
        EnsureDistinct(delegates, "delegated agent", file.RelativePath);
        EnsureDistinct(aliases, "alias", file.RelativePath);
        EnsureDistinct(copilotTools, "Copilot tool", file.RelativePath);

        if (copilotTools.Count == 0)
        {
            SquadSourceValidator.Throw(
                "Field 'copilot-tools' must declare at least one known tool.",
                "copilot-tools",
                file.RelativePath,
                $"Add one or more tools from: {string.Join(", ", CopilotToolCatalog.OrderedTools)}.");
        }

        foreach (string tool in copilotTools)
        {
            if (!CopilotToolCatalog.RequiredCapabilities.ContainsKey(tool))
            {
                SquadSourceValidator.Throw(
                    $"Copilot tool '{tool}' is not in the known tool catalog.",
                    tool,
                    file.RelativePath,
                    $"Use one of: {string.Join(", ", CopilotToolCatalog.OrderedTools)}.");
            }
        }

        if (string.IsNullOrWhiteSpace(frontmatter.Body))
        {
            SquadSourceValidator.Throw(
                "Agent instruction body must not be empty.",
                file.RelativePath,
                file.RelativePath,
                "Add the canonical, target-neutral agent instructions after the frontmatter.");
        }

        return new SquadAgent(
            RequireSchema(root, "schema", AgentSchema, file.RelativePath, frontmatter.LineOffset),
            RequireScalar(root, "name", file.RelativePath, frontmatter.LineOffset),
            RequireScalar(root, "description", file.RelativePath, frontmatter.LineOffset),
            invocation,
            RequireScalar(root, "model-profile", file.RelativePath, frontmatter.LineOffset),
            RequireScalar(root, "capability-profile", file.RelativePath, frontmatter.LineOffset),
            TryGetScalar(root, "copilot-capability-profile", file.RelativePath, frontmatter.LineOffset),
            copilotTools,
            delegates,
            RequireScalar(root, "fallback", file.RelativePath, frontmatter.LineOffset),
            aliases,
            frontmatter.Body,
            Sha256(frontmatter.Body),
            file.RelativePath,
            LoadResources(logicalRoot, resolvedRoot, file.RelativePath, frontmatter.Body));
    }

    private static IReadOnlyList<SquadSkill> LoadSkills(string logicalRoot, string resolvedRoot)
    {
        const string directory = "skills";
        string logicalDirectory = Path.Combine(logicalRoot, directory);
        if (!Directory.Exists(logicalDirectory))
        {
            return [];
        }

        EnsureDiscoveredPath(logicalRoot, resolvedRoot, directory, isDirectory: true);
        List<SquadSkill> skills = new List<SquadSkill>();
        foreach (string skillDirectory in Directory.EnumerateDirectories(logicalDirectory).Order(StringComparer.Ordinal))
        {
            string relativeDirectory = ToRelativePath(logicalRoot, skillDirectory);
            EnsureDiscoveredPath(logicalRoot, resolvedRoot, relativeDirectory, isDirectory: true);
            string skillPath = Path.Combine(skillDirectory, "SKILL.md");
            if (!File.Exists(skillPath))
            {
                continue;
            }

            string relativePath = ToRelativePath(logicalRoot, skillPath);
            skills.Add(ParseSkill(
                logicalRoot,
                resolvedRoot,
                ReadSourceFile(
                    logicalRoot,
                    resolvedRoot,
                    relativePath,
                    relativePath,
                    relativePath)));
        }

        return skills
            .OrderBy(skill => skill.Name, StringComparer.Ordinal)
            .ThenByDescending(skill => SourceIdentityMatches(skill.Name, skill.SourcePath, isSkill: true))
            .ThenBy(skill => skill.SourcePath, StringComparer.Ordinal)
            .ToArray();
    }

    private static SquadSkill ParseSkill(string logicalRoot, string resolvedRoot, SourceFile file)
    {
        Frontmatter frontmatter = ParseFrontmatter(file);
        YamlMappingNode root = ParseYamlMapping(file with { Content = frontmatter.Yaml }, frontmatter.LineOffset);
        EnsureOnlyFields(
            root,
            ["name", "description", "license", "compatibility", "metadata", "allowed-tools"],
            file.RelativePath,
            frontmatter.LineOffset);
        return new SquadSkill(
            RequireScalar(root, "name", file.RelativePath, frontmatter.LineOffset),
            RequireScalar(root, "description", file.RelativePath, frontmatter.LineOffset),
            frontmatter.Body,
            file.RelativePath,
            LoadResources(logicalRoot, resolvedRoot, file.RelativePath, frontmatter.Body));
    }

    private static IReadOnlyList<SquadResource> LoadResources(
        string logicalRoot,
        string resolvedRoot,
        string principalPath,
        string instructionBody)
    {
        string platformPrincipalPath = principalPath.Replace('/', Path.DirectorySeparatorChar);
        string ownerRelativePath = Path.GetDirectoryName(platformPrincipalPath) ?? string.Empty;
        string logicalOwnerRoot = Path.GetFullPath(Path.Combine(logicalRoot, ownerRelativePath));
        string resolvedOwnerRoot = ResolveExistingPath(logicalOwnerRoot).FullPath;
        ResourceClosureBuilder builder = new ResourceClosureBuilder(
            logicalRoot,
            resolvedRoot,
            logicalOwnerRoot,
            resolvedOwnerRoot);
        return builder.Build(principalPath, instructionBody);
    }

    private static void ValidateSchemaDocuments(string logicalRoot, string resolvedRoot)
    {
        foreach (string relativePath in RequiredSchemaFiles)
        {
            SourceFile file = ReadSourceFile(
                logicalRoot,
                resolvedRoot,
                relativePath,
                relativePath,
                relativePath);
            try
            {
                using JsonDocument document = JsonDocument.Parse(file.Content);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    SquadSourceValidator.Throw(
                        "Schema document must contain a JSON object.",
                        relativePath,
                        relativePath,
                        "Replace the schema with a valid JSON Schema object.");
                }

                if (string.Equals(relativePath, FallbackProfilesSchemaDocument, StringComparison.Ordinal))
                {
                    ValidateFallbackProfilesSchemaIdentity(document.RootElement, relativePath);
                }
            }
            catch (JsonException exception)
            {
                SquadSourceValidator.Throw(
                    $"Schema document is not valid JSON: {exception.Message}",
                    relativePath,
                    relativePath,
                    "Correct the JSON syntax and keep the schema UTF-8 encoded.",
                    exception.LineNumber is null ? null : checked((int)exception.LineNumber.Value + 1));
            }
        }
    }

    private static void ValidateFallbackProfilesSchemaIdentity(JsonElement root, string relativePath)
    {
        if (!root.TryGetProperty("$id", out JsonElement idElement) ||
            idElement.ValueKind != JsonValueKind.String ||
            !string.Equals(idElement.GetString(), FallbackProfilesSchemaId, StringComparison.Ordinal))
        {
            string actual = idElement.ValueKind == JsonValueKind.String
                ? idElement.GetString() ?? "null"
                : "missing or non-string";
            SquadSourceValidator.Throw(
                $"Schema document identity '{actual}' is not supported.",
                relativePath,
                relativePath,
                $"Set $id to the supported identity '{FallbackProfilesSchemaId}'.");
        }
    }

    private static SourceFile ReadSourceFile(
        string logicalRoot,
        string resolvedRoot,
        string relativePath,
        string declaringPath,
        string subject)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) || HasTraversal(relativePath))
        {
            SquadSourceValidator.Throw(
                $"Referenced path '{relativePath}' escapes or traverses the Squad product root.",
                subject,
                declaringPath,
                "Use a relative path that stays inside the Squad product root.");
        }

        string platformPath = relativePath
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
        string logicalPath = Path.GetFullPath(Path.Combine(logicalRoot, platformPath));
        if (!IsWithin(logicalRoot, logicalPath))
        {
            SquadSourceValidator.Throw(
                $"Referenced path '{relativePath}' resolves outside the Squad product root.",
                subject,
                declaringPath,
                "Use a relative path that resolves inside the Squad product root.");
        }

        if (!File.Exists(logicalPath))
        {
            string hint = relativePath.StartsWith("schemas/", StringComparison.Ordinal)
                ? "Ensure the required schema file exists inside the Squad product root."
                : "Ensure the referenced file exists inside the Squad product root.";
            SquadSourceValidator.Throw(
                $"Referenced path '{relativePath}' does not exist.",
                subject,
                declaringPath,
                hint);
        }

        ResolvedPath resolved = ResolveExistingPath(logicalPath);
        if (!IsWithin(resolvedRoot, resolved.FullPath))
        {
            string mechanism = resolved.EncounteredSymbolicLink ? "symbolic link" : "resolved path";
            SquadSourceValidator.Throw(
                $"Source path '{relativePath}' uses a {mechanism} whose target is outside the Squad product root.",
                subject,
                ToPortablePath(relativePath),
                "Keep every source and symbolic-link target inside the Squad product root.");
        }

        string portablePath = ToPortablePath(Path.GetRelativePath(logicalRoot, logicalPath));
        try
        {
            byte[] bytes = File.ReadAllBytes(logicalPath);
            string content = StrictUtf8.GetString(bytes);
            if (content.Length > 0 && content[0] == '\uFEFF')
            {
                content = content[1..];
            }

            return new SourceFile(portablePath, NormalizeNewlines(content));
        }
        catch (DecoderFallbackException)
        {
            SquadSourceValidator.Throw(
                $"Source file '{portablePath}' is not valid UTF-8.",
                subject,
                portablePath,
                "Save the file as valid UTF-8 without replacing invalid bytes.");
            throw;
        }
    }

    private static void EnsureDiscoveredPath(
        string logicalRoot,
        string resolvedRoot,
        string relativePath,
        bool isDirectory)
    {
        string logicalPath = Path.Combine(
            logicalRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        bool exists = isDirectory ? Directory.Exists(logicalPath) : File.Exists(logicalPath);
        if (!exists)
        {
            return;
        }

        ResolvedPath resolved = ResolveExistingPath(logicalPath);
        if (!IsWithin(resolvedRoot, resolved.FullPath))
        {
            SquadSourceValidator.Throw(
                $"Source path '{relativePath}' uses a symbolic link whose target is outside the Squad product root.",
                relativePath,
                ToPortablePath(relativePath),
                "Keep every source and symbolic-link target inside the Squad product root.");
        }
    }

    private static ResolvedPath ResolveExistingPath(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string root = Path.GetPathRoot(fullPath)
                   ?? throw new InvalidOperationException($"Path '{path}' has no filesystem root.");
        string current = root;
        bool encounteredLink = false;
        string[] segments = fullPath[root.Length..]
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);

        foreach (string segment in segments)
        {
            current = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : new FileInfo(current);
            if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) == 0)
            {
                continue;
            }

            FileSystemInfo? target = info.ResolveLinkTarget(returnFinalTarget: true);
            if (target is not null)
            {
                encounteredLink = true;
                current = Path.GetFullPath(target.FullName);
            }
        }

        return new ResolvedPath(Path.GetFullPath(current), encounteredLink);
    }

    private static Frontmatter ParseFrontmatter(SourceFile file)
    {
        if (!file.Content.StartsWith("---\n", StringComparison.Ordinal))
        {
            SquadSourceValidator.Throw(
                "Markdown source is missing YAML frontmatter.",
                file.RelativePath,
                file.RelativePath,
                "Add a YAML frontmatter block delimited by '---' lines.");
        }

        int closingDelimiter = file.Content.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (closingDelimiter < 0)
        {
            SquadSourceValidator.Throw(
                "Markdown source has an unterminated YAML frontmatter block.",
                file.RelativePath,
                file.RelativePath,
                "Add a closing '---' line followed by a newline.");
        }

        return new Frontmatter(
            file.Content[4..closingDelimiter],
            file.Content[(closingDelimiter + 5)..],
            LineOffset: 1);
    }

    private static YamlMappingNode ParseYamlMapping(SourceFile file, int lineOffset = 0)
    {
        try
        {
            YamlStream stream = new YamlStream();
            stream.Load(new StringReader(file.Content));
            if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode)
            {
                SquadSourceValidator.Throw(
                    "YAML source must contain exactly one mapping document.",
                    file.RelativePath,
                    file.RelativePath,
                    "Replace the content with one YAML mapping document.");
            }

            return (YamlMappingNode)stream.Documents[0].RootNode;
        }
        catch (YamlException exception)
        {
            SquadSourceValidator.Throw(
                $"YAML source is invalid: {exception.Message}",
                file.RelativePath,
                file.RelativePath,
                "Correct the YAML syntax and remove duplicate mapping keys.",
                checked((int)exception.Start.Line + lineOffset));
            throw;
        }
    }

    private static string RequireSchema(
        YamlMappingNode mapping,
        string field,
        string expected,
        string sourcePath,
        int lineOffset = 0)
    {
        string actual = RequireScalar(mapping, field, sourcePath, lineOffset);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            SquadSourceValidator.Throw(
                $"Unsupported schema '{actual}'.",
                field,
                sourcePath,
                $"Use the supported schema '{expected}'.");
        }

        return actual;
    }

    private static string RequireScalar(
        YamlMappingNode mapping,
        string field,
        string sourcePath,
        int lineOffset = 0) =>
        RequireScalar(RequireNode(mapping, field, sourcePath), field, sourcePath, nodeIsValue: true, lineOffset);

    private static string RequireScalar(
        YamlNode node,
        string field,
        string sourcePath,
        bool nodeIsValue,
        int lineOffset = 0)
    {
        _ = nodeIsValue;
        if (node is not YamlScalarNode scalarNode || string.IsNullOrWhiteSpace(scalarNode.Value))
        {
            SquadSourceValidator.Throw(
                $"Field '{field}' must be a non-empty string.",
                field,
                sourcePath,
                $"Add a non-empty string value for '{field}'.",
                StartLine(node, lineOffset));
        }

        return ((YamlScalarNode)node).Value!;
    }

    private static YamlMappingNode RequireMapping(
        YamlMappingNode mapping,
        string field,
        string sourcePath,
        int lineOffset = 0) =>
        RequireMapping(RequireNode(mapping, field, sourcePath), field, sourcePath, nodeIsValue: true, lineOffset);

    private static YamlMappingNode RequireMapping(
        YamlNode node,
        string field,
        string sourcePath,
        bool nodeIsValue,
        int lineOffset = 0)
    {
        _ = nodeIsValue;
        if (node is not YamlMappingNode)
        {
            SquadSourceValidator.Throw(
                $"Field '{field}' must be a mapping.",
                field,
                sourcePath,
                $"Add a YAML mapping for '{field}'.",
                StartLine(node, lineOffset));
        }

        return (YamlMappingNode)node;
    }

    private static YamlNode RequireNode(YamlMappingNode mapping, string field, string sourcePath)
    {
        YamlScalarNode key = new YamlScalarNode(field);
        if (!mapping.Children.TryGetValue(key, out YamlNode? node))
        {
            SquadSourceValidator.Throw(
                $"Required field '{field}' is missing.",
                field,
                sourcePath,
                $"Add the required '{field}' field.");
        }

        return node;
    }

    private static string? TryGetScalar(
        YamlMappingNode mapping,
        string field,
        string sourcePath,
        int lineOffset = 0) =>
        mapping.Children.TryGetValue(new YamlScalarNode(field), out YamlNode? node)
            ? RequireScalar(node, field, sourcePath, nodeIsValue: true, lineOffset)
            : null;

    private static YamlMappingNode? TryGetMapping(
        YamlMappingNode mapping,
        string field,
        string sourcePath,
        int lineOffset = 0) =>
        mapping.Children.TryGetValue(new YamlScalarNode(field), out YamlNode? node)
            ? RequireMapping(node, field, sourcePath, nodeIsValue: true, lineOffset)
            : null;

    private static IReadOnlyList<string>? TryGetStringSequence(
        YamlMappingNode mapping,
        string field,
        string sourcePath,
        int lineOffset = 0)
    {
        if (!mapping.Children.TryGetValue(new YamlScalarNode(field), out YamlNode? node))
        {
            return null;
        }

        if (node is not YamlSequenceNode)
        {
            SquadSourceValidator.Throw(
                $"Field '{field}' must be a sequence.",
                field,
                sourcePath,
                $"Add a YAML sequence for '{field}'.",
                StartLine(node, lineOffset));
        }

        string[] values = ((YamlSequenceNode)node).Children
            .Select(item => RequireScalar(item, field, sourcePath, nodeIsValue: true, lineOffset))
            .ToArray();
        EnsureDistinct(values, field, sourcePath);
        return values;
    }

    private static IReadOnlyList<string> RequireStringSequence(
        YamlMappingNode mapping,
        string field,
        string sourcePath,
        int lineOffset = 0)
    {
        YamlNode node = RequireNode(mapping, field, sourcePath);
        if (node is not YamlSequenceNode)
        {
            SquadSourceValidator.Throw(
                $"Field '{field}' must be a sequence.",
                field,
                sourcePath,
                $"Add a YAML sequence for '{field}'.",
                StartLine(node, lineOffset));
        }

        return ((YamlSequenceNode)node).Children
            .Select(item => RequireScalar(item, field, sourcePath, nodeIsValue: true, lineOffset))
            .ToArray();
    }

    private static IReadOnlyDictionary<string, string> RequireStringMap(
        YamlMappingNode mapping,
        string field,
        string sourcePath,
        int lineOffset = 0)
    {
        YamlMappingNode child = RequireMapping(mapping, field, sourcePath, lineOffset);
        SortedDictionary<string, string> result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach ((string key, YamlNode node) in MappingEntries(child, sourcePath, lineOffset))
        {
            result.Add(key, RequireScalar(node, key, sourcePath, nodeIsValue: true, lineOffset));
        }

        return result;
    }

    private static IEnumerable<KeyValuePair<string, YamlNode>> MappingEntries(
        YamlMappingNode mapping,
        string sourcePath,
        int lineOffset = 0)
    {
        foreach (KeyValuePair<YamlNode, YamlNode> pair in mapping.Children)
        {
            if (pair.Key is not YamlScalarNode keyNode || string.IsNullOrWhiteSpace(keyNode.Value))
            {
                SquadSourceValidator.Throw(
                    "YAML mapping keys must be non-empty strings.",
                    sourcePath,
                    sourcePath,
                    "Replace the mapping key with a non-empty string.",
                    StartLine(pair.Key, lineOffset));
            }

            yield return new KeyValuePair<string, YamlNode>(((YamlScalarNode)pair.Key).Value!, pair.Value);
        }
    }

    private static void EnsureOnlyFields(
        YamlMappingNode mapping,
        IEnumerable<string> allowed,
        string sourcePath,
        int lineOffset = 0)
    {
        HashSet<string> allowedSet = allowed.ToHashSet(StringComparer.Ordinal);
        foreach ((string field, YamlNode node) in MappingEntries(mapping, sourcePath, lineOffset))
        {
            if (!allowedSet.Contains(field))
            {
                SquadSourceValidator.Throw(
                    $"Unknown field '{field}' violates the closed source schema.",
                    field,
                    sourcePath,
                    $"Remove the unsupported field '{field}'.",
                    StartLine(node, lineOffset));
            }
        }
    }

    private static void EnsureDistinct(
        IReadOnlyList<string> values,
        string kind,
        string sourcePath)
    {
        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string value in values)
        {
            if (!seen.Add(value))
            {
                SquadSourceValidator.Throw(
                    $"Duplicate {kind} '{value}' is not allowed.",
                    value,
                    sourcePath,
                    $"Remove the duplicate {kind}.");
            }
        }
    }

    private static string RequireFallbackDecision(
        YamlMappingNode mapping,
        string field,
        string sourcePath)
    {
        string decision = RequireScalar(mapping, field, sourcePath);
        if (decision is not ("skill" or "omit"))
        {
            SquadSourceValidator.Throw(
                $"Fallback field '{field}' uses unsupported decision '{decision}'.",
                field,
                sourcePath,
                "Use one of the supported fallback decisions: skill or omit.");
        }

        return decision;
    }

    private static void RequireLiteral(
        string value,
        string field,
        string expected,
        string sourcePath,
        string hint)
    {
        if (!string.Equals(value, expected, StringComparison.Ordinal))
        {
            SquadSourceValidator.Throw(
                $"Fallback field '{field}' uses unsupported value '{value}'.",
                field,
                sourcePath,
                hint);
        }
    }

    private static SquadPermissionDecision ThrowInvalidDecision(
        string decision,
        string subject,
        string sourcePath,
        YamlNode node)
    {
        SquadSourceValidator.Throw(
            $"Permission decision '{decision}' is not supported.",
            subject,
            sourcePath,
            "Use one of the allowed permission decisions: deny, ask, or allow.",
            StartLine(node));
        throw new InvalidOperationException("Unreachable after validation failure.");
    }

    private static SquadInvocation ThrowInvalidInvocation(string invocation, string sourcePath)
    {
        SquadSourceValidator.Throw(
            $"Agent invocation '{invocation}' is not supported.",
            invocation,
            sourcePath,
            "Use one of the supported invocation modes: primary or subagent.");
        throw new InvalidOperationException("Unreachable after validation failure.");
    }

    private static void ThrowDuplicateKey(string key, string sourcePath) =>
        SquadSourceValidator.Throw(
            $"Duplicate mapping key '{key}' is not allowed.",
            key,
            sourcePath,
            "Remove the duplicate mapping key.");

    private static void ThrowJson(string sourcePath, string message) =>
        SquadSourceValidator.Throw(
            message,
            "mcp",
            sourcePath,
            "Use the documented MCP JSON fields and value types.");

    private static void ValidateMcpServer(JsonProperty server, string sourcePath)
    {
        if (server.Value.ValueKind != JsonValueKind.Object)
        {
            ThrowJson(sourcePath, $"MCP server '{server.Name}' must be a JSON object.");
        }

        foreach (JsonProperty property in server.Value.EnumerateObject())
        {
            if (property.Name is not ("command" or "args"))
            {
                ThrowJson(sourcePath, $"MCP server '{server.Name}' contains unknown field '{property.Name}'.");
            }
        }

        if (!server.Value.TryGetProperty("command", out JsonElement command) ||
            command.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(command.GetString()))
        {
            ThrowJson(sourcePath, $"MCP server '{server.Name}' requires a non-empty string command.");
        }

        if (!server.Value.TryGetProperty("args", out JsonElement args) ||
            args.ValueKind != JsonValueKind.Array ||
            args.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
        {
            ThrowJson(sourcePath, $"MCP server '{server.Name}' requires a string array args field.");
        }
    }

    private static object? ToPlainValue(YamlNode node) => node switch
    {
        YamlScalarNode scalar when IsYamlNull(scalar) => null,
        YamlScalarNode scalar => scalar.Value,
        YamlSequenceNode sequence => sequence.Children.Select(ToPlainValue).ToArray(),
        YamlMappingNode mapping => MappingEntries(mapping, "toolchain.yml")
            .ToDictionary(pair => pair.Key, pair => ToPlainValue(pair.Value), StringComparer.Ordinal),
        _ => throw new InvalidOperationException($"Unsupported YAML node type {node.GetType().Name}.")
    };

    private static bool IsYamlNull(YamlNode node) =>
        node is YamlScalarNode scalar &&
        (scalar.Value is null || scalar.Value is "~" ||
         string.Equals(scalar.Value, "null", StringComparison.OrdinalIgnoreCase));

    private static int? StartLine(YamlNode node, int lineOffset = 0) =>
        node.Start.Line > 0 ? checked((int)node.Start.Line + lineOffset) : null;

    private static bool HasTraversal(string relativePath) =>
        relativePath.Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => string.Equals(segment, "..", StringComparison.Ordinal));

    private static bool IsWithin(string root, string candidate)
    {
        string relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(candidate));
        return !Path.IsPathRooted(relative) &&
               !string.Equals(relative, "..", StringComparison.Ordinal) &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
               !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static string NormalizeNewlines(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

    private static string Sha256(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string ToRelativePath(string root, string path) =>
        ToPortablePath(Path.GetRelativePath(root, path));

    private static string ToPortablePath(string path) =>
        path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');

    private static bool SourceIdentityMatches(string identity, string sourcePath, bool isSkill)
    {
        string platformPath = sourcePath.Replace('/', Path.DirectorySeparatorChar);
        string? sourceIdentity = isSkill
            ? Path.GetFileName(Path.GetDirectoryName(platformPath))
            : Path.GetFileNameWithoutExtension(platformPath);
        return string.Equals(identity, sourceIdentity, StringComparison.Ordinal);
    }

    private sealed class ResourceClosureBuilder
    {
        private readonly string _logicalRoot;
        private readonly string _resolvedRoot;
        private readonly string _logicalOwnerRoot;
        private readonly string _resolvedOwnerRoot;
        private readonly SortedDictionary<string, SquadResource> _resources =
            new SortedDictionary<string, SquadResource>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _portablePaths =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _active = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _complete = new HashSet<string>(StringComparer.Ordinal);

        public ResourceClosureBuilder(
            string logicalRoot,
            string resolvedRoot,
            string logicalOwnerRoot,
            string resolvedOwnerRoot)
        {
            _logicalRoot = logicalRoot;
            _resolvedRoot = resolvedRoot;
            _logicalOwnerRoot = logicalOwnerRoot;
            _resolvedOwnerRoot = resolvedOwnerRoot;
        }

        public IReadOnlyList<SquadResource> Build(string principalPath, string instructionBody)
        {
            VisitLinks(instructionBody, principalPath, string.Empty);
            return _resources.Values.ToArray();
        }

        private void VisitLinks(string markdown, string declaringPath, string declaringDirectory)
        {
            MarkdownDocument document = Markdown.Parse(markdown);
            foreach (LinkInline link in document.Descendants<LinkInline>())
            {
                string? target = GetLocalTarget(link.Url);
                if (target is not null)
                {
                    Visit(target, declaringPath, declaringDirectory);
                }
            }
        }

        private void Visit(string target, string declaringPath, string declaringDirectory)
        {
            if (IsRootedResourcePath(target))
            {
                ThrowOutsideOwner(target, declaringPath);
            }

            string platformTarget = target.Replace('/', Path.DirectorySeparatorChar);
            string logicalPath = Path.GetFullPath(Path.Combine(
                _logicalOwnerRoot,
                declaringDirectory.Replace('/', Path.DirectorySeparatorChar),
                platformTarget));
            if (!IsWithin(_logicalOwnerRoot, logicalPath) ||
                string.Equals(_logicalOwnerRoot, logicalPath, StringComparison.Ordinal))
            {
                ThrowOutsideOwner(target, declaringPath);
            }

            if (!File.Exists(logicalPath))
            {
                SquadSourceValidator.Throw(
                    $"Referenced resource '{target}' does not exist inside its owning artifact directory.",
                    target,
                    declaringPath,
                    "Add the file or update the link to an existing resource inside the owning agent or skill directory.");
            }

            ResolvedPath resolved = ResolveExistingPath(logicalPath);
            if (!IsWithin(_resolvedOwnerRoot, resolved.FullPath))
            {
                string mechanism = resolved.EncounteredSymbolicLink ? "symbolic link" : "resolved path";
                SquadSourceValidator.Throw(
                    $"Resource '{target}' uses a {mechanism} whose target is outside its owning artifact directory.",
                    target,
                    declaringPath,
                    "Keep every resource and symbolic-link target inside the owning agent or skill directory.");
            }

            string relativePath = ToPortablePath(Path.GetRelativePath(_logicalOwnerRoot, logicalPath));
            EnsurePortableIdentity(relativePath, target, declaringPath);

            if (_active.Contains(relativePath))
            {
                SquadSourceValidator.Throw(
                    $"Resource link cycle detected at '{relativePath}'.",
                    target,
                    declaringPath,
                    "Break the Markdown resource cycle so every reference chain terminates.");
            }

            if (_complete.Contains(relativePath))
            {
                return;
            }

            string productRelativePath = ToPortablePath(Path.GetRelativePath(_logicalRoot, logicalPath));
            SourceFile resource = ReadSourceFile(
                _logicalRoot,
                _resolvedRoot,
                productRelativePath,
                declaringPath,
                target);
            _resources.Add(relativePath, new SquadResource(relativePath, resource.Content));

            _active.Add(relativePath);
            if (IsMarkdown(relativePath))
            {
                string resourceDirectory = ToPortablePath(Path.GetDirectoryName(
                    relativePath.Replace('/', Path.DirectorySeparatorChar)) ?? string.Empty);
                VisitLinks(resource.Content, resource.RelativePath, resourceDirectory);
            }

            _active.Remove(relativePath);
            _complete.Add(relativePath);
        }

        private void EnsurePortableIdentity(string relativePath, string target, string declaringPath)
        {
            string portableIdentity = GetPortableAliasIdentity(relativePath);
            if (_portablePaths.TryGetValue(portableIdentity, out string? existingPath) &&
                !string.Equals(existingPath, relativePath, StringComparison.Ordinal))
            {
                SquadSourceValidator.Throw(
                    $"Resource paths '{existingPath}' and '{relativePath}' have a portable alias collision.",
                    target,
                    declaringPath,
                    "Rename one resource so its case-insensitive path remains distinct after trailing dots and spaces are removed.");
            }

            _portablePaths.TryAdd(portableIdentity, relativePath);
            try
            {
                _ = SquadPathPolicy.NormalizeRelativePath(relativePath);
            }
            catch (SquadPathContainmentException)
            {
                ThrowNonPortable(relativePath, target, declaringPath);
            }
            catch (SquadDeploymentConflictException)
            {
                ThrowNonPortable(relativePath, target, declaringPath);
            }
        }

        private static string? GetLocalTarget(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            string target = url.Trim();
            if (target.StartsWith('#') || target.StartsWith("//", StringComparison.Ordinal) ||
                (HasUriScheme(target) && !IsWindowsDrivePath(target)))
            {
                return null;
            }

            int fragmentIndex = target.IndexOf('#', StringComparison.Ordinal);
            int queryIndex = target.IndexOf('?', StringComparison.Ordinal);
            int suffixIndex = fragmentIndex < 0
                ? queryIndex
                : queryIndex < 0 ? fragmentIndex : Math.Min(fragmentIndex, queryIndex);
            if (suffixIndex >= 0)
            {
                target = target[..suffixIndex];
            }

            return target.Length == 0 ? null : Uri.UnescapeDataString(target);
        }

        private static bool HasUriScheme(string target)
        {
            int colonIndex = target.IndexOf(':', StringComparison.Ordinal);
            if (colonIndex <= 0 || !char.IsAsciiLetter(target[0]))
            {
                return false;
            }

            return target[1..colonIndex].All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '+' or '-' or '.');
        }

        private static bool IsWindowsDrivePath(string target) =>
            target.Length >= 2 && char.IsAsciiLetter(target[0]) && target[1] == ':';

        private static bool IsRootedResourcePath(string target) =>
            Path.IsPathRooted(target) || target[0] is '/' or '\\' || IsWindowsDrivePath(target);

        private static bool IsMarkdown(string relativePath)
        {
            string extension = Path.GetExtension(relativePath);
            return string.Equals(extension, ".md", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(extension, ".markdown", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetPortableAliasIdentity(string relativePath) =>
            string.Join(
                '/',
                relativePath.Split('/').Select(segment => segment.TrimEnd(' ', '.')))
                .Normalize(NormalizationForm.FormC);

        private static void ThrowOutsideOwner(string target, string declaringPath) =>
            SquadSourceValidator.Throw(
                $"Referenced resource '{target}' escapes its owning artifact directory.",
                target,
                declaringPath,
                "Use a relative link whose resolved file stays inside the owning agent or skill directory.");

        private static void ThrowNonPortable(string relativePath, string target, string declaringPath) =>
            SquadSourceValidator.Throw(
                $"Resource path '{relativePath}' is not a normalized portable path.",
                target,
                declaringPath,
                "Use NFC Unicode, forward slashes, and names valid on Windows filesystems.");
    }

    private sealed record SourceFile(string RelativePath, string Content);
    private sealed record Frontmatter(string Yaml, string Body, int LineOffset = 1);
    private readonly record struct ResolvedPath(string FullPath, bool EncounteredSymbolicLink);
}
