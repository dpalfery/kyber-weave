using System.Text;

namespace KyberWeave.Tests.Fixtures;

/// <summary>
/// Builds a minimal Squad corpus whose sole agent and skill deliberately share one
/// byte-identical instruction body and are declared as one fallback shared identity.
/// </summary>
internal sealed class SharedIdentitySquadFixture : IDisposable
{
    public const string Identity = "shared-worker";

    private const string InstructionBody = "Follow the shared instruction body exactly.\n";

    private readonly TempDirectory _tempDirectory = new();

    private SharedIdentitySquadFixture()
    {
    }

    public string Path => _tempDirectory.Path;

    public static SharedIdentitySquadFixture Create()
    {
        SharedIdentitySquadFixture fixture = new();
        fixture.Write("squad.yml", """
            schema: kyber-squad.squad/v1
            name: shared-identity-fixture
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
        fixture.Write("bundles/full.yml", $"""
            schema: kyber-squad.bundle/v1
            name: full
            agents:
              - {Identity}
            skills:
              - {Identity}
            """);
        fixture.Write("profiles/models.yml", """
            schema: kyber-squad.model-profiles/v1
            profiles:
              general:
                default: inherit
            """);
        fixture.Write("profiles/capabilities.yml", """
            schema: kyber-squad.capability-profiles/v1
            capabilities:
              - filesystem.read
            profiles:
              worker:
                permissions:
                  filesystem.read: allow
            """);
        fixture.Write("profiles/fallbacks.yml", $"""
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
        fixture.Write("toolchain.yml", """
            schema: kyber-squad.toolchain/v1
            required-features:
              - agent-ir/v1
            validated-release: null
            """);
        fixture.Write("mcp.json", """
            {
              "mcpServers": {}
            }
            """);
        fixture.Write($"agents/{Identity}.md", $"""
            ---
            schema: kyber-squad.agent/v1
            name: {Identity}
            description: Use when exercising shared identity projection.
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
        fixture.Write($"skills/{Identity}/SKILL.md", $"""
            ---
            name: {Identity}
            description: Use when exercising shared identity projection.
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
            fixture.Write($"schemas/{schema}.schema.json", """
                {
                  "$schema": "https://json-schema.org/draft/2020-12/schema",
                  "type": "object"
                }
                """);
        }
        fixture.Write("schemas/fallback-profiles.schema.json", """
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "$id": "https://kyber-weave.dev/schemas/kyber-squad/fallback-profiles/v1",
              "type": "object"
            }
            """);

        return fixture;
    }

    public void Dispose() => _tempDirectory.Dispose();

    private void Write(string relativePath, string content)
    {
        string fullPath = System.IO.Path.Combine(Path, relativePath);
        string? directory = System.IO.Path.GetDirectoryName(fullPath);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(fullPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
