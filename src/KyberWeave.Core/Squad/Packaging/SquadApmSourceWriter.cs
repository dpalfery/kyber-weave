using System.Text;
using System.Text.Json;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using YamlDotNet.Serialization;

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

    private static readonly ISerializer YamlSerializer = new SerializerBuilder().Build();

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
        Dictionary<string, string> bundles = new(StringComparer.Ordinal);
        foreach ((string name, string path) in source.Manifest.Bundles)
        {
            bundles[name] = path.Replace('\\', '/');
        }

        Dictionary<string, object?> manifestDict = new(StringComparer.Ordinal)
        {
            ["schema"] = source.Manifest.Schema,
            ["name"] = source.Manifest.Name,
            ["version-source"] = source.Manifest.VersionSource,
            ["default-bundle"] = source.Manifest.DefaultBundle,
            ["bundles"] = bundles,
            ["profiles"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["models"] = source.Manifest.Profiles.Models.Replace('\\', '/'),
                ["capabilities"] = source.Manifest.Profiles.Capabilities.Replace('\\', '/'),
                ["fallbacks"] = source.Manifest.Profiles.Fallbacks.Replace('\\', '/')
            },
            ["toolchain"] = source.Manifest.ToolchainPath.Replace('\\', '/'),
            ["mcp"] = source.Manifest.McpPath.Replace('\\', '/')
        };

        string yaml = YamlSerializer.Serialize(manifestDict);
        WriteFile(root, source.Manifest.SourcePath, yaml);
    }

    private static void WriteBundle(SquadSource source, string root)
    {
        Dictionary<string, object?> bundleDict = new(StringComparer.Ordinal)
        {
            ["schema"] = source.Bundle.Schema,
            ["name"] = source.Bundle.Name,
            ["agents"] = source.Bundle.AgentNames.ToList(),
            ["skills"] = source.Bundle.SkillNames.ToList()
        };

        string bundlePath = !string.IsNullOrWhiteSpace(source.Bundle.SourcePath)
            ? source.Bundle.SourcePath
            : $"bundles/{source.Bundle.Name}.yml";

        WriteFile(root, bundlePath, YamlSerializer.Serialize(bundleDict));
    }

    private static void WriteModelProfiles(SquadSource source, string root)
    {
        Dictionary<string, object?> profilesDict = new(StringComparer.Ordinal);
        foreach ((string profileName, SquadModelProfile profile) in source.ModelProfiles.Profiles)
        {
            Dictionary<string, object?> profileEntry = new(StringComparer.Ordinal)
            {
                ["default"] = profile.Default
            };
            foreach ((string harness, string model) in profile.HarnessModels)
            {
                profileEntry[harness] = model;
            }

            profilesDict[profileName] = profileEntry;
        }

        Dictionary<string, object?> modelProfilesDict = new(StringComparer.Ordinal)
        {
            ["schema"] = source.ModelProfiles.Schema,
            ["profiles"] = profilesDict
        };

        string modelsPath = !string.IsNullOrWhiteSpace(source.ModelProfiles.SourcePath)
            ? source.ModelProfiles.SourcePath
            : source.Manifest.Profiles.Models;

        WriteFile(root, modelsPath, YamlSerializer.Serialize(modelProfilesDict));
    }

    private static void WriteCapabilityProfiles(SquadSource source, string root)
    {
        Dictionary<string, object?> profilesDict = new(StringComparer.Ordinal);
        foreach ((string profileName, SquadCapabilityProfile profile) in source.CapabilityProfiles.Profiles)
        {
            Dictionary<string, string> permissionsDict = new(StringComparer.Ordinal);
            foreach ((string capability, SquadPermissionDecision decision) in profile.Permissions)
            {
                string decisionStr = decision switch
                {
                    SquadPermissionDecision.Deny => "deny",
                    SquadPermissionDecision.Ask => "ask",
                    SquadPermissionDecision.Allow => "allow",
                    _ => "deny"
                };
                permissionsDict[capability] = decisionStr;
            }

            profilesDict[profileName] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["permissions"] = permissionsDict
            };
        }

        Dictionary<string, object?> capabilityProfilesDict = new(StringComparer.Ordinal)
        {
            ["schema"] = source.CapabilityProfiles.Schema,
            ["capabilities"] = source.CapabilityProfiles.Capabilities.ToList(),
            ["profiles"] = profilesDict
        };

        string capabilitiesPath = !string.IsNullOrWhiteSpace(source.CapabilityProfiles.SourcePath)
            ? source.CapabilityProfiles.SourcePath
            : source.Manifest.Profiles.Capabilities;

        WriteFile(root, capabilitiesPath, YamlSerializer.Serialize(capabilityProfilesDict));
    }

    private static void WriteFallbackProfiles(SquadSource source, string root)
    {
        Dictionary<string, object?> profilesDict = new(StringComparer.Ordinal);
        foreach ((string profileName, SquadFallbackProfile profile) in source.FallbackProfiles.Profiles)
        {
            Dictionary<string, object?> profileEntry = new(StringComparer.Ordinal)
            {
                ["no-primary-agent"] = profile.NoPrimaryAgent,
                ["no-agent-primitive"] = profile.NoAgentPrimitive,
                ["body-source"] = profile.BodySource,
                ["output-identity"] = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["unoccupied"] = profile.OutputIdentity.Unoccupied,
                    ["shared"] = profile.OutputIdentity.Shared,
                    ["collision"] = profile.OutputIdentity.Collision,
                    ["prefix"] = profile.OutputIdentity.Prefix
                },
                ["shared-identities"] = profile.SharedIdentities.ToList()
            };

            profilesDict[profileName] = profileEntry;
        }

        Dictionary<string, object?> fallbackProfilesDict = new(StringComparer.Ordinal)
        {
            ["schema"] = source.FallbackProfiles.Schema,
            ["profiles"] = profilesDict
        };

        string fallbacksPath = !string.IsNullOrWhiteSpace(source.FallbackProfiles.SourcePath)
            ? source.FallbackProfiles.SourcePath
            : source.Manifest.Profiles.Fallbacks;

        WriteFile(root, fallbacksPath, YamlSerializer.Serialize(fallbackProfilesDict));
    }

    private static void WriteToolchain(SquadSource source, string root)
    {
        Dictionary<string, object?> toolchainDict = new(StringComparer.Ordinal)
        {
            ["schema"] = source.Toolchain.Schema,
            ["required-features"] = source.Toolchain.RequiredFeatures.ToList(),
            ["validated-release"] = source.Toolchain.ValidatedRelease.HasValue
                ? JsonElementToYamlObject(source.Toolchain.ValidatedRelease.Value)
                : null
        };

        string yaml = YamlSerializer.Serialize(toolchainDict);
        string toolchainPath = !string.IsNullOrWhiteSpace(source.Toolchain.SourcePath)
            ? source.Toolchain.SourcePath
            : source.Manifest.ToolchainPath;

        WriteFile(root, toolchainPath, yaml);
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
            Dictionary<string, object?> frontmatter = new(StringComparer.Ordinal)
            {
                ["schema"] = agent.Schema,
                ["name"] = agent.Name,
                ["description"] = agent.Description,
                ["invocation"] = agent.Invocation == SquadInvocation.Primary ? "primary" : "subagent",
                ["model-profile"] = agent.ModelProfile,
                ["capability-profile"] = agent.CapabilityProfile,
                ["delegates-to"] = agent.DelegatesTo.ToList(),
                ["fallback"] = agent.Fallback,
                ["aliases"] = agent.Aliases.ToList()
            };

            string yaml = YamlSerializer.Serialize(frontmatter);
            StringBuilder builder = new();
            builder.Append("---\n");
            builder.Append(yaml);
            if (!yaml.EndsWith('\n'))
            {
                builder.Append('\n');
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
            Dictionary<string, object?> frontmatter = new(StringComparer.Ordinal)
            {
                ["name"] = skill.Name,
                ["description"] = skill.Description
            };

            string yaml = YamlSerializer.Serialize(frontmatter);
            StringBuilder builder = new();
            builder.Append("---\n");
            builder.Append(yaml);
            if (!yaml.EndsWith('\n'))
            {
                builder.Append('\n');
            }
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

    private static object? JsonElementToYamlObject(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                Dictionary<string, object?> dict = new(StringComparer.Ordinal);
                foreach (JsonProperty prop in element.EnumerateObject())
                {
                    dict[prop.Name] = JsonElementToYamlObject(prop.Value);
                }
                return dict;

            case JsonValueKind.Array:
                List<object?> list = [];
                foreach (JsonElement item in element.EnumerateArray())
                {
                    list.Add(JsonElementToYamlObject(item));
                }
                return list;

            case JsonValueKind.String:
                return element.GetString();

            case JsonValueKind.Number:
                if (element.TryGetInt64(out long l)) return l;
                if (element.TryGetDouble(out double d)) return d;
                return element.GetRawText();

            case JsonValueKind.True:
                return true;

            case JsonValueKind.False:
                return false;

            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
            default:
                return null;
        }
    }
}
