using System.Collections;
using System.Reflection;
using System.Text;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using KyberWeave.Core.Squad.Validation;
using Xunit;
using Xunit.Sdk;

namespace KyberWeave.Tests;

public sealed class SquadResourceClosureTests
{
    [Fact]
    public void PublicSourceArtifactsExposeOneCommonImmutableResourceModel()
    {
        PropertyInfo? agentResources = typeof(SquadAgent).GetProperty("Resources");
        PropertyInfo? skillResources = typeof(SquadSkill).GetProperty("Resources");

        Assert.NotNull(agentResources);
        Assert.NotNull(skillResources);
        Assert.Equal(agentResources.PropertyType, skillResources.PropertyType);
        Assert.True(agentResources.PropertyType.IsGenericType);
        Assert.Equal(typeof(IReadOnlyList<>), agentResources.PropertyType.GetGenericTypeDefinition());

        Type resourceType = Assert.Single(agentResources.PropertyType.GetGenericArguments());
        Assert.Equal("SquadResource", resourceType.Name);
        Assert.True(resourceType.IsPublic);
        Assert.True(resourceType.IsSealed);
        Assert.Equal(typeof(string), resourceType.GetProperty("RelativePath")?.PropertyType);
        Assert.Equal(typeof(string), resourceType.GetProperty("Content")?.PropertyType);
    }

    [Fact]
    public void LoadAgentResourcesResolvesRecursiveMarkdownAndLeafContentInOrdinalOrder()
    {
        using ResourceFixture fixture = ResourceFixture.CreateValid();
        fixture.WriteAgentBody(
            "[Start](references/start.md)\n" +
            "[Duplicate](references/start.md)\n" +
            "[Web](https://example.com/remote.md)\n" +
            "[Mail](mailto:maintainer@example.com)\n" +
            "[Fragment](#local-section)\n");
        fixture.Write(
            "agents/references/start.md",
            "# Start\r\n\r\n[Next](nested/next.md)\r\n[Leaf](shared.txt)\r\n");
        fixture.Write(
            "agents/references/nested/next.md",
            "# Next\n\n[Shared leaf](../shared.txt)\n");
        fixture.Write("agents/references/shared.txt", "shared leaf\r\n");

        SquadAgent agent = LoadAgent(fixture);
        IReadOnlyList<ResourceSnapshot> resources = ReadResources(agent);

        Assert.Equal(
            [
                "references/nested/next.md",
                "references/shared.txt",
                "references/start.md"
            ],
            resources.Select(resource => resource.RelativePath));
        Assert.Equal("shared leaf\n", resources[1].Content);
        Assert.Equal(
            "# Start\n\n[Next](nested/next.md)\n[Leaf](shared.txt)\n",
            resources[2].Content);
    }

    [Fact]
    public void LoadSkillResourcesResolvesMarkdownAndLeafContentRelativeToSkillDirectory()
    {
        using ResourceFixture fixture = ResourceFixture.CreateValid();
        fixture.WriteSkillBody("[Guide](references/guide.md)\n");
        fixture.Write(
            "skills/test-dev/references/guide.md",
            "# Guide\n\n[Checklist](assets/checklist.txt)\n");
        fixture.Write("skills/test-dev/references/assets/checklist.txt", "check one\n");

        SquadSkill skill = LoadSkill(fixture);
        IReadOnlyList<ResourceSnapshot> resources = ReadResources(skill);

        Assert.Equal(
            ["references/assets/checklist.txt", "references/guide.md"],
            resources.Select(resource => resource.RelativePath));
        Assert.Equal("check one\n", resources[0].Content);
    }

    [Fact]
    public void LoadResourceClosureDeduplicatesRepeatedLinksAndSharedDescendants()
    {
        using ResourceFixture fixture = ResourceFixture.CreateValid();
        fixture.WriteAgentBody(
            "[Alpha](references/alpha.md)\n" +
            "[Alpha again](references/alpha.md)\n" +
            "[Beta](references/beta.md)\n");
        fixture.Write("agents/references/alpha.md", "[Shared](shared.md)\n");
        fixture.Write("agents/references/beta.md", "[Shared](shared.md)\n");
        fixture.Write("agents/references/shared.md", "# Shared\n");

        SquadAgent agent = LoadAgent(fixture);

        Assert.Equal(
            ["references/alpha.md", "references/beta.md", "references/shared.md"],
            ReadResources(agent).Select(resource => resource.RelativePath));
    }

    [Fact]
    public void LoadRecursiveResourceWithMissingTargetReportsDeclaringResourceAndHint()
    {
        using ResourceFixture fixture = ResourceFixture.CreateValid();
        fixture.WriteAgentBody("[Start](references/start.md)\n");
        fixture.Write("agents/references/start.md", "[Missing](missing.md)\n");

        Diagnostic diagnostic = AssertInvalid(fixture, "missing.md");

        Assert.Equal("agents/references/start.md", diagnostic.FilePath);
    }

    [Fact]
    public void LoadResourceClosureWithActiveCycleReportsCycleAndSourceLocation()
    {
        using ResourceFixture fixture = ResourceFixture.CreateValid();
        fixture.WriteAgentBody("[A](references/a.md)\n");
        fixture.Write("agents/references/a.md", "[B](b.md)\n");
        fixture.Write("agents/references/b.md", "[A](a.md)\n");

        Diagnostic diagnostic = AssertInvalid(fixture, "cycle");

        Assert.StartsWith("agents/references/", diagnostic.FilePath, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadResourceThatTraversesOutsideAgentDirectoryFailsBeforeReadingIt()
    {
        using ResourceFixture fixture = ResourceFixture.CreateValid();
        fixture.Write("outside.md", "must not be read\n");
        fixture.WriteAgentBody("[Outside](../outside.md)\n");

        Diagnostic diagnostic = AssertInvalid(fixture, "../outside.md");

        Assert.Equal("agents/architect.md", diagnostic.FilePath);
        Assert.Contains("inside", diagnostic.Hint!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoadResourceSymlinkThatEscapesAgentDirectoryReportsResolvedEscape()
    {
        if (OperatingSystem.IsWindows())
        {
            throw SkipException.ForSkip("Symbolic links require elevated privileges on Windows.");
        }

        using ResourceFixture fixture = ResourceFixture.CreateValid();
        using TempDirectory outside = new TempDirectory();
        string outsideResource = Path.Combine(outside.Path, "outside.md");
        File.WriteAllText(outsideResource, "outside\n", new UTF8Encoding(false));
        string linkedResource = fixture.AbsolutePath("agents/references/escape.md");
        Directory.CreateDirectory(Path.GetDirectoryName(linkedResource)!);
        try
        {
            File.CreateSymbolicLink(linkedResource, outsideResource);
        }
        catch (UnauthorizedAccessException)
        {
            throw SkipException.ForSkip("Creating symbolic links requires elevated privileges on this host.");
        }

        fixture.WriteAgentBody("[Escape](references/escape.md)\n");

        Diagnostic diagnostic = AssertInvalid(fixture, "symbolic link");

        Assert.StartsWith("agents/", diagnostic.FilePath, StringComparison.Ordinal);
        Assert.Contains("inside", diagnostic.Hint!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoadLeafResourceThatIsNotValidUtf8ReportsResourceLocation()
    {
        using ResourceFixture fixture = ResourceFixture.CreateValid();
        fixture.WriteAgentBody("[Leaf](references/bad.txt)\n");
        fixture.WriteBytes("agents/references/bad.txt", [0xc3, 0x28]);

        Diagnostic diagnostic = AssertInvalid(fixture, "UTF-8");

        Assert.Equal("agents/references/bad.txt", diagnostic.FilePath);
        Assert.Contains("UTF-8", diagnostic.Hint!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoadResourcePathsWithPortableAliasesReportsCollision()
    {
        using ResourceFixture fixture = ResourceFixture.CreateValid();
        fixture.WriteAgentBody(
            "[Canonical](references/guide.md)\n" +
            "[Trailing-dot alias](references/guide.md.)\n");
        fixture.Write("agents/references/guide.md", "canonical\n");
        fixture.Write("agents/references/guide.md.", "alias\n");

        Diagnostic diagnostic = AssertInvalid(fixture, "alias collision");

        Assert.StartsWith("agents/", diagnostic.FilePath, StringComparison.Ordinal);
        Assert.Contains("portable", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static SquadAgent LoadAgent(ResourceFixture fixture) =>
        Assert.Single(
            SquadSourceLoader.Load(fixture.Path).Agents,
            agent => agent.Name == "architect");

    private static SquadSkill LoadSkill(ResourceFixture fixture) =>
        Assert.Single(
            SquadSourceLoader.Load(fixture.Path).Skills,
            skill => skill.Name == "test-dev");

    private static IReadOnlyList<ResourceSnapshot> ReadResources(object artifact)
    {
        PropertyInfo? property = artifact.GetType().GetProperty("Resources");
        Assert.NotNull(property);
        IEnumerable? resources = property.GetValue(artifact) as IEnumerable;
        Assert.NotNull(resources);

        List<ResourceSnapshot> snapshots = new List<ResourceSnapshot>();
        foreach (object resource in resources)
        {
            PropertyInfo? pathProperty = resource.GetType().GetProperty("RelativePath");
            PropertyInfo? contentProperty = resource.GetType().GetProperty("Content");
            Assert.NotNull(pathProperty);
            Assert.NotNull(contentProperty);
            string? relativePath = pathProperty.GetValue(resource) as string;
            string? content = contentProperty.GetValue(resource) as string;
            Assert.NotNull(relativePath);
            Assert.NotNull(content);
            snapshots.Add(new ResourceSnapshot(relativePath, content));
        }

        return snapshots;
    }

    private static Diagnostic AssertInvalid(ResourceFixture fixture, string expectedFragment)
    {
        SquadSourceValidationException exception = Assert.Throws<SquadSourceValidationException>(
            () => SquadSourceLoader.Load(fixture.Path));
        Diagnostic diagnostic = Assert.Single(
            exception.Diagnostics.Items,
            item => item.Message.Contains(expectedFragment, StringComparison.OrdinalIgnoreCase) ||
                    (item.Hint?.Contains(expectedFragment, StringComparison.OrdinalIgnoreCase) ?? false));

        Assert.Equal(Severity.Error, diagnostic.Severity);
        Assert.False(Path.IsPathRooted(diagnostic.FilePath!));
        Assert.False(string.IsNullOrWhiteSpace(diagnostic.Hint));
        return diagnostic;
    }

    private sealed record ResourceSnapshot(string RelativePath, string Content);

    private sealed class ResourceFixture : IDisposable
    {
        private readonly TempDirectory _temp = new TempDirectory();

        public string Path => _temp.Path;

        private ResourceFixture()
        {
        }

        public static ResourceFixture CreateValid()
        {
            ResourceFixture fixture = new ResourceFixture();
            fixture.Write("squad.yml", """
                schema: kyber-squad.squad/v1
                name: kyber-squad
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
            fixture.Write("bundles/full.yml", """
                schema: kyber-squad.bundle/v1
                name: full
                agents:
                  - architect
                skills:
                  - test-dev
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
                  - filesystem.write
                  - delegate
                profiles:
                  worker:
                    permissions:
                      filesystem.read: allow
                      filesystem.write: ask
                      delegate: deny
                """);
            fixture.Write("profiles/fallbacks.yml", """
                schema: kyber-squad.fallback-profiles/v1
                profiles:
                  role-skill:
                    no-primary-agent: skill
                    no-agent-primitive: skill
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
            fixture.WriteAgentBody("You are architect.\n");
            fixture.WriteSkillBody("# test-dev\n");

            foreach (string schema in new[]
                     {
                         "squad", "bundle", "agent", "model-profiles", "capability-profiles"
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
                  "type": "object",
                  "additionalProperties": false,
                  "required": ["schema", "profiles"],
                  "properties": {
                    "schema": { "const": "kyber-squad.fallback-profiles/v1" },
                    "profiles": {
                      "type": "object",
                      "additionalProperties": {
                        "type": "object",
                        "additionalProperties": false,
                        "required": ["no-primary-agent", "no-agent-primitive"],
                        "properties": {
                          "no-primary-agent": { "enum": ["skill", "omit"] },
                          "no-agent-primitive": { "enum": ["skill", "omit"] },
                          "body-source": { "const": "agent" },
                          "output-identity": {
                            "type": "object",
                            "additionalProperties": false,
                            "required": ["unoccupied", "shared", "collision", "prefix"],
                            "properties": {
                              "unoccupied": { "const": "agent-name" },
                              "shared": { "const": "reuse-skill" },
                              "collision": { "const": "role-prefixed-agent-name" },
                              "prefix": { "const": "role-" }
                            }
                          },
                          "shared-identities": {
                            "type": "array",
                            "uniqueItems": true,
                            "items": { "type": "string", "minLength": 1 }
                          }
                        }
                      }
                    }
                  }
                }
                """);

            return fixture;
        }

        public string AbsolutePath(string relativePath) =>
            System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));

        public void WriteAgentBody(string body) =>
            Write(
                "agents/architect.md",
                "---\n" +
                "schema: kyber-squad.agent/v1\n" +
                "name: architect\n" +
                "description: Use when planning.\n" +
                "invocation: subagent\n" +
                "model-profile: general\n" +
                "capability-profile: worker\n" +
                "copilot-tools: [vscode, read]\n" +
                "delegates-to: []\n" +
                "fallback: role-skill\n" +
                "aliases: []\n" +
                "---\n" +
                body);

        public void WriteSkillBody(string body) =>
            Write(
                "skills/test-dev/SKILL.md",
                "---\n" +
                "name: test-dev\n" +
                "description: Use when writing tests.\n" +
                "license: MIT\n" +
                "metadata:\n" +
                "  author: Kyber-Weave\n" +
                "  version: 1.0.0\n" +
                "---\n" +
                body);

        public void Write(string relativePath, string content)
        {
            string path = AbsolutePath(relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content, new UTF8Encoding(false));
        }

        public void WriteBytes(string relativePath, byte[] content)
        {
            string path = AbsolutePath(relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
        }

        public void Dispose() => _temp.Dispose();
    }
}
