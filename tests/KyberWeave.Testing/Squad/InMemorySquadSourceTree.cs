using KyberWeave.Tests.FileSystem;

namespace KyberWeave.Testing.Squad;

/// <summary>
/// Seeds an in-memory file system with a minimal Squad source tree that the source loader accepts, so a test
/// of loading, link containment or encoding needs neither the checkout nor a temporary directory.
/// </summary>
/// <remarks>
/// The tree carries one agent and one skill that share an instruction body and one fallback shared identity.
/// The schema documents are the smallest JSON object each loader check accepts; this fixture exists to prove
/// the loader's behaviour, not to reproduce the shipped schemas.
/// </remarks>
public static class InMemorySquadSourceTree
{
    /// <summary>The identity the tree's agent and skill share.</summary>
    public const string Identity = "shared-worker";

    private const string InstructionBody = "Follow the shared instruction body exactly.\n";

    /// <summary>Writes the whole tree under <paramref name="root"/>.</summary>
    /// <param name="files">The in-memory file system to write into.</param>
    /// <param name="root">The Squad source root the tree lives under.</param>
    public static void Write(InMemoryFileSystem files, string root)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        files.CreateDirectory(root);

        WriteFile(files, root, "squad.yml", """
            schema: kyber-squad.squad/v1
            name: in-memory-fixture
            version-source: kyber-weave-assembly
            default-bundle: full
            bundles:
              full: bundles/full.yml
            profiles:
              models: profiles/models.yml
              capabilities: profiles/capabilities.yml
              fallbacks: profiles/fallbacks.yml
            toolchain: toolchain.yml
            mcp: mcp.json
            """);
        WriteFile(files, root, "bundles/full.yml", $"""
            schema: kyber-squad.bundle/v1
            name: full
            agents:
              - {Identity}
            skills:
              - {Identity}
            """);
        WriteFile(files, root, "profiles/models.yml", """
            schema: kyber-squad.model-profiles/v1
            profiles:
              general:
                default: inherit
            """);
        WriteFile(files, root, "profiles/capabilities.yml", """
            schema: kyber-squad.capability-profiles/v1
            capabilities:
              - filesystem.read
            profiles:
              worker:
                permissions:
                  filesystem.read: allow
            """);
        WriteFile(files, root, "profiles/fallbacks.yml", $"""
            schema: kyber-squad.fallback-profiles/v1
            profiles:
              role-skill:
                no-primary-agent: skill
                no-agent-primitive: skill
                body-source: agent
                output-identity:
                  unoccupied: agent-name
                  shared: reuse-skill
                  collision: role-prefixed-agent-name
                  prefix: role-
                shared-identities:
                  - {Identity}
            """);
        WriteFile(files, root, "toolchain.yml", """
            schema: kyber-squad.toolchain/v1
            required-features:
              - agent-ir/v1
            validated-release: null
            """);
        WriteFile(files, root, "mcp.json", """
            {
              "mcpServers": {}
            }
            """);
        WriteFile(files, root, $"agents/{Identity}.md", $"""
            ---
            schema: kyber-squad.agent/v1
            name: {Identity}
            description: Use when exercising the in-memory squad source tree.
            invocation: subagent
            model-profile: general
            capability-profile: worker
            copilot-tools: [vscode, read]
            delegates-to: []
            fallback: role-skill
            aliases: []
            ---
            {InstructionBody}
            """);
        WriteFile(files, root, $"skills/{Identity}/SKILL.md", $"""
            ---
            name: {Identity}
            description: Use when exercising the in-memory squad source tree.
            license: MIT
            ---
            {InstructionBody}
            """);

        foreach (string schema in new[]
                 {
                     "squad",
                     "bundle",
                     "agent",
                     "model-profiles",
                     "capability-profiles"
                 })
        {
            WriteFile(files, root, $"schemas/{schema}.schema.json", """
                {
                  "$schema": "https://json-schema.org/draft/2020-12/schema",
                  "type": "object"
                }
                """);
        }

        WriteFile(files, root, "schemas/fallback-profiles.schema.json", """
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "$id": "https://kyber-weave.dev/schemas/kyber-squad/fallback-profiles/v1",
              "type": "object"
            }
            """);
    }

    private static void WriteFile(InMemoryFileSystem files, string root, string relativePath, string content)
    {
        string fullPath = Path.Combine(root, relativePath);
        string? directory = Path.GetDirectoryName(fullPath);
        if (directory is not null)
        {
            files.CreateDirectory(directory);
        }

        files.WriteAllText(fullPath, content);
    }
}
