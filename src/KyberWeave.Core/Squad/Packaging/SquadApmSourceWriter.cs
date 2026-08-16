using System.Text;
using System.Text.Json;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;

namespace KyberWeave.Core.Squad.Packaging;

/// <summary>
/// Defines the contract for writing a canonical Squad source tree into a target-neutral staging directory.
/// </summary>
public interface ISquadApmSourceWriter
{
    /// <summary>
    /// Writes the given Squad source to the specified output staging directory.
    /// </summary>
    /// <param name="source">The validated Squad source.</param>
    /// <param name="outputDirectory">The destination directory to populate.</param>
    void Write(SquadSource source, string outputDirectory);
}

/// <summary>
/// Writes canonical Kyber-Squad source into a temporary, target-neutral APM source tree
/// for compilation and packing by the upstream APM runner.
/// </summary>
public sealed class SquadApmSourceWriter : ISquadApmSourceWriter
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly JsonSerializerOptions IndentedJsonOptions = new()
    {
        WriteIndented = true
    };

    /// <summary>
    /// Writes the given Squad source to a temporary target-neutral staging directory.
    /// </summary>
    /// <param name="source">The validated Squad source.</param>
    /// <param name="outputDirectory">The destination directory to populate.</param>
    public void Write(SquadSource source, string outputDirectory)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        string root = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(root);

        WriteManifest(source, root);
        WriteBundle(source, root);
        WriteModelProfiles(source, root);
        WriteCapabilityProfiles(source, root);
        WriteFallbackProfiles(source, root);
        WriteToolchain(source, root);
        WriteMcp(source, root);
        WriteAgents(source, root);
        WriteSkills(source, root);
        WriteSchemas(source, root);
    }

    private static void WriteManifest(SquadSource source, string root)
    {
        StringBuilder builder = new();
        builder.Append("schema: ").Append(source.Manifest.Schema).Append('\n');
        builder.Append("name: ").Append(source.Manifest.Name).Append('\n');
        builder.Append("version-source: ").Append(source.Manifest.VersionSource).Append('\n');
        builder.Append("default-bundle: ").Append(source.Manifest.DefaultBundle).Append('\n');

        builder.Append("bundles:\n");
        foreach ((string name, string path) in source.Manifest.Bundles)
        {
            builder.Append("  ").Append(name).Append(": ").Append(path.Replace('\\', '/')).Append('\n');
        }

        builder.Append("profiles:\n");
        builder.Append("  models: ").Append(source.Manifest.Profiles.Models.Replace('\\', '/')).Append('\n');
        builder.Append("  capabilities: ").Append(source.Manifest.Profiles.Capabilities.Replace('\\', '/')).Append('\n');
        builder.Append("  fallbacks: ").Append(source.Manifest.Profiles.Fallbacks.Replace('\\', '/')).Append('\n');

        builder.Append("toolchain: ").Append(source.Manifest.ToolchainPath.Replace('\\', '/')).Append('\n');
        builder.Append("mcp: ").Append(source.Manifest.McpPath.Replace('\\', '/')).Append('\n');

        WriteFile(root, source.Manifest.SourcePath, builder.ToString());
    }

    private static void WriteBundle(SquadSource source, string root)
    {
        StringBuilder builder = new();
        builder.Append("schema: ").Append(source.Bundle.Schema).Append('\n');
        builder.Append("name: ").Append(source.Bundle.Name).Append('\n');

        builder.Append("agents:\n");
        foreach (string agent in source.Bundle.AgentNames)
        {
            builder.Append("  - ").Append(agent).Append('\n');
        }

        builder.Append("skills:\n");
        foreach (string skill in source.Bundle.SkillNames)
        {
            builder.Append("  - ").Append(skill).Append('\n');
        }

        string bundlePath = !string.IsNullOrWhiteSpace(source.Bundle.SourcePath)
            ? source.Bundle.SourcePath
            : $"bundles/{source.Bundle.Name}.yml";

        WriteFile(root, bundlePath, builder.ToString());
    }

    private static void WriteModelProfiles(SquadSource source, string root)
    {
        StringBuilder builder = new();
        builder.Append("schema: ").Append(source.ModelProfiles.Schema).Append('\n');
        builder.Append("profiles:\n");

        foreach ((string profileName, SquadModelProfile profile) in source.ModelProfiles.Profiles)
        {
            builder.Append("  ").Append(profileName).Append(":\n");
            builder.Append("    default: ").Append(profile.Default).Append('\n');
            foreach ((string harness, string model) in profile.HarnessModels)
            {
                builder.Append("    ").Append(harness).Append(": ").Append(model).Append('\n');
            }
        }

        string modelsPath = !string.IsNullOrWhiteSpace(source.ModelProfiles.SourcePath)
            ? source.ModelProfiles.SourcePath
            : source.Manifest.Profiles.Models;

        WriteFile(root, modelsPath, builder.ToString());
    }

    private static void WriteCapabilityProfiles(SquadSource source, string root)
    {
        StringBuilder builder = new();
        builder.Append("schema: ").Append(source.CapabilityProfiles.Schema).Append('\n');
        builder.Append("capabilities:\n");
        foreach (string capability in source.CapabilityProfiles.Capabilities)
        {
            builder.Append("  - ").Append(capability).Append('\n');
        }

        builder.Append("profiles:\n");
        foreach ((string profileName, SquadCapabilityProfile profile) in source.CapabilityProfiles.Profiles)
        {
            builder.Append("  ").Append(profileName).Append(":\n");
            builder.Append("    permissions:\n");
            foreach ((string capability, SquadPermissionDecision decision) in profile.Permissions)
            {
                string decisionStr = decision switch
                {
                    SquadPermissionDecision.Deny => "deny",
                    SquadPermissionDecision.Ask => "ask",
                    SquadPermissionDecision.Allow => "allow",
                    _ => "deny"
                };
                builder.Append("      ").Append(capability).Append(": ").Append(decisionStr).Append('\n');
            }
        }

        string capabilitiesPath = !string.IsNullOrWhiteSpace(source.CapabilityProfiles.SourcePath)
            ? source.CapabilityProfiles.SourcePath
            : source.Manifest.Profiles.Capabilities;

        WriteFile(root, capabilitiesPath, builder.ToString());
    }

    private static void WriteFallbackProfiles(SquadSource source, string root)
    {
        StringBuilder builder = new();
        builder.Append("schema: ").Append(source.FallbackProfiles.Schema).Append('\n');
        builder.Append("profiles:\n");

        foreach ((string profileName, SquadFallbackProfile profile) in source.FallbackProfiles.Profiles)
        {
            builder.Append("  ").Append(profileName).Append(":\n");
            builder.Append("    no-primary-agent: ").Append(profile.NoPrimaryAgent).Append('\n');
            builder.Append("    no-agent-primitive: ").Append(profile.NoAgentPrimitive).Append('\n');
            builder.Append("    body-source: ").Append(profile.BodySource).Append('\n');
            builder.Append("    output-identity:\n");
            builder.Append("      unoccupied: ").Append(profile.OutputIdentity.Unoccupied).Append('\n');
            builder.Append("      shared: ").Append(profile.OutputIdentity.Shared).Append('\n');
            builder.Append("      collision: ").Append(profile.OutputIdentity.Collision).Append('\n');
            builder.Append("      prefix: ").Append(profile.OutputIdentity.Prefix).Append('\n');

            if (profile.SharedIdentities.Count > 0)
            {
                builder.Append("    shared-identities:\n");
                foreach (string id in profile.SharedIdentities)
                {
                    builder.Append("      - ").Append(id).Append('\n');
                }
            }
            else
            {
                builder.Append("    shared-identities: []\n");
            }
        }

        string fallbacksPath = !string.IsNullOrWhiteSpace(source.FallbackProfiles.SourcePath)
            ? source.FallbackProfiles.SourcePath
            : source.Manifest.Profiles.Fallbacks;

        WriteFile(root, fallbacksPath, builder.ToString());
    }

    private static void WriteToolchain(SquadSource source, string root)
    {
        StringBuilder builder = new();
        builder.Append("schema: ").Append(source.Toolchain.Schema).Append('\n');
        builder.Append("required-features:\n");
        foreach (string feature in source.Toolchain.RequiredFeatures)
        {
            builder.Append("  - ").Append(feature).Append('\n');
        }

        if (source.Toolchain.ValidatedRelease.HasValue)
        {
            builder.Append("validated-release: ")
                .Append(source.Toolchain.ValidatedRelease.Value.GetRawText())
                .Append('\n');
        }
        else
        {
            builder.Append("validated-release: null\n");
        }

        string toolchainPath = !string.IsNullOrWhiteSpace(source.Toolchain.SourcePath)
            ? source.Toolchain.SourcePath
            : source.Manifest.ToolchainPath;

        WriteFile(root, toolchainPath, builder.ToString());
    }

    private static void WriteMcp(SquadSource source, string root)
    {
        string json = JsonSerializer.Serialize(source.McpConfiguration, IndentedJsonOptions);
        string mcpPath = !string.IsNullOrWhiteSpace(source.Manifest.McpPath)
            ? source.Manifest.McpPath
            : "mcp.json";

        WriteFile(root, mcpPath, json);
    }

    private static void WriteAgents(SquadSource source, string root)
    {
        foreach (SquadAgent agent in source.Agents)
        {
            StringBuilder builder = new();
            builder.Append("---\n");
            builder.Append("schema: ").Append(agent.Schema).Append('\n');
            builder.Append("name: ").Append(agent.Name).Append('\n');
            builder.Append("description: ").Append(EscapeYamlString(agent.Description)).Append('\n');
            builder.Append("invocation: ")
                .Append(agent.Invocation == SquadInvocation.Primary ? "primary" : "subagent")
                .Append('\n');
            builder.Append("model-profile: ").Append(agent.ModelProfile).Append('\n');
            builder.Append("capability-profile: ").Append(agent.CapabilityProfile).Append('\n');

            if (agent.DelegatesTo.Count > 0)
            {
                builder.Append("delegates-to:\n");
                foreach (string del in agent.DelegatesTo)
                {
                    builder.Append("  - ").Append(del).Append('\n');
                }
            }
            else
            {
                builder.Append("delegates-to: []\n");
            }

            builder.Append("fallback: ").Append(agent.Fallback).Append('\n');

            if (agent.Aliases.Count > 0)
            {
                builder.Append("aliases:\n");
                foreach (string alias in agent.Aliases)
                {
                    builder.Append("  - ").Append(alias).Append('\n');
                }
            }
            else
            {
                builder.Append("aliases: []\n");
            }

            builder.Append("---\n");

            string normalizedBody = agent.InstructionBody.Replace("\r\n", "\n");
            builder.Append(normalizedBody);
            if (!normalizedBody.EndsWith('\n'))
            {
                builder.Append('\n');
            }

            string agentPath = !string.IsNullOrWhiteSpace(agent.SourcePath)
                ? agent.SourcePath
                : $"agents/{agent.Name}.md";

            WriteFile(root, agentPath, builder.ToString());
        }
    }

    private static void WriteSkills(SquadSource source, string root)
    {
        foreach (SquadSkill skill in source.Skills)
        {
            StringBuilder builder = new();
            builder.Append("---\n");
            builder.Append("name: ").Append(skill.Name).Append('\n');
            builder.Append("description: ").Append(EscapeYamlString(skill.Description)).Append('\n');
            builder.Append("---\n");

            string normalizedBody = skill.InstructionBody.Replace("\r\n", "\n");
            builder.Append(normalizedBody);
            if (!normalizedBody.EndsWith('\n'))
            {
                builder.Append('\n');
            }

            string skillPath = !string.IsNullOrWhiteSpace(skill.SourcePath)
                ? skill.SourcePath
                : $"skills/{skill.Name}/SKILL.md";

            WriteFile(root, skillPath, builder.ToString());
        }
    }

    private static void WriteSchemas(SquadSource source, string root)
    {
        string sourceSchemasDir = Path.Combine(source.RootPath, "schemas");
        if (Directory.Exists(sourceSchemasDir))
        {
            foreach (string schemaFile in Directory.EnumerateFiles(sourceSchemasDir, "*.schema.json"))
            {
                string relativePath = $"schemas/{Path.GetFileName(schemaFile)}";
                string content = File.ReadAllText(schemaFile);
                WriteFile(root, relativePath, content);
            }
        }
        else
        {
            // Write standard schemas if source.RootPath does not contain schemas on disk
            WriteStandardSchemas(root);
        }
    }

    private static void WriteStandardSchemas(string root)
    {
        WriteFile(root, "schemas/squad.schema.json", """
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "$id": "https://kyber-weave.dev/schemas/kyber-squad/squad/v1",
              "type": "object"
            }
            """);

        WriteFile(root, "schemas/bundle.schema.json", """
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "$id": "https://kyber-weave.dev/schemas/kyber-squad/bundle/v1",
              "type": "object"
            }
            """);

        WriteFile(root, "schemas/agent.schema.json", """
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "$id": "https://kyber-weave.dev/schemas/kyber-squad/agent/v1",
              "type": "object"
            }
            """);

        WriteFile(root, "schemas/model-profiles.schema.json", """
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "$id": "https://kyber-weave.dev/schemas/kyber-squad/model-profiles/v1",
              "type": "object"
            }
            """);

        WriteFile(root, "schemas/capability-profiles.schema.json", """
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "$id": "https://kyber-weave.dev/schemas/kyber-squad/capability-profiles/v1",
              "type": "object"
            }
            """);

        WriteFile(root, "schemas/fallback-profiles.schema.json", """
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "$id": "https://kyber-weave.dev/schemas/kyber-squad/fallback-profiles/v1",
              "type": "object"
            }
            """);
    }

    private static void WriteFile(string root, string relativePath, string content)
    {
        string normalizedRelPath = SquadPathPolicy.NormalizeRelativePath(relativePath.Replace('\\', '/'));
        string fullPath = Path.Combine(root, normalizedRelPath.Replace('/', Path.DirectorySeparatorChar));
        string? dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string normalizedContent = content.Replace("\r\n", "\n");
        File.WriteAllText(fullPath, normalizedContent, Utf8WithoutBom);
    }

    private static string EscapeYamlString(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "\"\"";
        }

        if (value.Contains('\n') || value.Contains('\r') || value.Contains(':') || value.Contains('#') ||
            value.Contains('\"') || value.Contains('\'') || value.StartsWith(' ') || value.EndsWith(' '))
        {
            return "\"" + value
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal)
                .Replace("\r", "", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal) + "\"";
        }

        return value;
    }
}
