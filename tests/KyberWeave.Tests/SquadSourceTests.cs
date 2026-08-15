using System.Text;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using KyberWeave.Core.Squad.Validation;
using Xunit;
using Xunit.Sdk;

namespace KyberWeave.Tests;

public sealed class SquadSourceTests
{
    private const string NormalizedArchitectBody = "You are architect.\nPlan first.\n";
    private const string NormalizedArchitectBodySha256 =
        "856711e391c077cbee5211b2dfb76861163472c622bf414b5a52d6ce15e0b546";

    [Fact]
    public void LoadValidSourceLoadsDeterministically()
    {
        using SquadFixture fixture = SquadFixture.CreateValid();

        SquadSource first = SquadSourceLoader.Load(fixture.Path);
        SquadSource second = SquadSourceLoader.Load(fixture.Path);

        Assert.Equal("kyber-squad", first.Manifest.Name);
        Assert.Equal("full", first.Bundle.Name);
        Assert.Equal(
            ["architect", "dotnet-dev"],
            first.Agents.Select(agent => agent.Name));
        Assert.Equal(["test-dev"], first.Skills.Select(skill => skill.Name));
        Assert.Equal(
            first.Agents.Select(agent => (agent.Name, agent.SourcePath, agent.BodyDigest)),
            second.Agents.Select(agent => (agent.Name, agent.SourcePath, agent.BodyDigest)));
        Assert.Equal(
            first.Skills.Select(skill => (skill.Name, skill.SourcePath)),
            second.Skills.Select(skill => (skill.Name, skill.SourcePath)));
    }

    [Theory]
    [InlineData("squad.yml", "kyber-squad.squad/v1", "kyber-squad.squad/v2")]
    [InlineData("bundles/full.yml", "kyber-squad.bundle/v1", "kyber-squad.bundle/v2")]
    [InlineData("agents/architect.md", "kyber-squad.agent/v1", "kyber-squad.agent/v2")]
    [InlineData("profiles/models.yml", "kyber-squad.model-profiles/v1", "kyber-squad.model-profiles/v2")]
    [InlineData("profiles/capabilities.yml", "kyber-squad.capability-profiles/v1", "kyber-squad.capability-profiles/v2")]
    [InlineData("profiles/fallbacks.yml", "kyber-squad.fallback-profiles/v1", "kyber-squad.fallback-profiles/v2")]
    public void LoadUnsupportedSchemaReportsSourceRelativeLocationAndHint(
        string relativePath,
        string validSchema,
        string unsupportedSchema)
    {
        using SquadFixture fixture = SquadFixture.CreateValid();
        fixture.Replace(relativePath, validSchema, unsupportedSchema);

        Diagnostic diagnostic = AssertInvalid(fixture, relativePath, unsupportedSchema);

        Assert.Contains(validSchema, diagnostic.Hint!, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadAgentOmitsRequiredSchemaFieldReportsFieldAndSourceLocation()
    {
        using SquadFixture fixture = SquadFixture.CreateValid();
        fixture.Replace(
            "agents/architect.md",
            "description: Use when planning multi-domain work.\n",
            string.Empty);

        Diagnostic diagnostic = AssertInvalid(fixture, "agents/architect.md", "description");

        Assert.Contains("add", diagnostic.Hint!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoadAgentUsesUnknownFrontmatterFieldReportsClosedSchemaViolation()
    {
        using SquadFixture fixture = SquadFixture.CreateValid();
        fixture.Replace(
            "agents/architect.md",
            "aliases: []",
            "aliases: []\nharness-model: gpt-example");

        Diagnostic diagnostic = AssertInvalid(fixture, "agents/architect.md", "harness-model");

        Assert.Contains("remove", diagnostic.Hint!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoadVersionSourceIsNotAssemblyReportsRequiredLiteralAtManifestLocation()
    {
        using SquadFixture fixture = SquadFixture.CreateValid();
        fixture.Replace(
            "squad.yml",
            "version-source: kyber-weave-assembly",
            "version-source: release-tag");

        Diagnostic diagnostic = AssertInvalid(fixture, "squad.yml", "version-source");

        Assert.Contains("kyber-weave-assembly", diagnostic.Hint!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("model-profile", "missing-model", "agents/architect.md")]
    [InlineData("capability-profile", "missing-capabilities", "agents/architect.md")]
    [InlineData("fallback", "missing-fallback", "agents/architect.md")]
    public void LoadAgentReferencesUnknownProfileReportsAgentLocation(
        string key,
        string missingProfile,
        string relativePath)
    {
        using SquadFixture fixture = SquadFixture.CreateValid();
        string original = key switch
        {
            "model-profile" => "deep-planning",
            "capability-profile" => "architect",
            "fallback" => "role-skill",
            _ => throw new ArgumentOutOfRangeException(nameof(key))
        };
        fixture.Replace(relativePath, $"{key}: {original}", $"{key}: {missingProfile}");

        AssertInvalid(fixture, relativePath, missingProfile);
    }

    [Fact]
    public void LoadProfileUsesUnknownCapabilityReportsCapabilityProfileLocation()
    {
        using SquadFixture fixture = SquadFixture.CreateValid();
        fixture.Replace(
            "profiles/capabilities.yml",
            "      filesystem.read: allow",
            "      network.publish: allow");

        AssertInvalid(fixture, "profiles/capabilities.yml", "network.publish");
    }

    [Theory]
    [InlineData("grant")]
    [InlineData("true")]
    public void LoadProfileUsesUnknownPermissionDecisionReportsAllowedDecisions(string decision)
    {
        using SquadFixture fixture = SquadFixture.CreateValid();
        fixture.Replace(
            "profiles/capabilities.yml",
            "      filesystem.write: deny",
            $"      filesystem.write: {decision}");

        Diagnostic diagnostic = AssertInvalid(fixture, "profiles/capabilities.yml", decision);

        Assert.Contains("deny", diagnostic.Hint!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ask", diagnostic.Hint!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("allow", diagnostic.Hint!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoadProfileOmitsDeclaredCapabilityReportsMissingDecisionAtProfileLocation()
    {
        using SquadFixture fixture = SquadFixture.CreateValid();
        fixture.Replace(
            "profiles/capabilities.yml",
            "      delegate: ask\n",
            string.Empty);

        Diagnostic diagnostic = AssertInvalid(fixture, "profiles/capabilities.yml", "delegate");

        Assert.Contains("architect", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("permission", diagnostic.Hint!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoadFallbackProfileUsesUnknownFieldReportsClosedSchemaViolation()
    {
        using SquadFixture fixture = SquadFixture.CreateValid();
        fixture.Replace(
            "profiles/fallbacks.yml",
            "    no-agent-primitive: skill",
            "    no-agent-primitive: skill\n    harness-model: gpt-example");

        Diagnostic diagnostic = AssertInvalid(fixture, "profiles/fallbacks.yml", "harness-model");

        Assert.Contains("remove", diagnostic.Hint!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoadMissingFallbackProfilesSchemaReportsRequiredSchemaLocation()
    {
        using SquadFixture fixture = SquadFixture.CreateValid();
        File.Delete(fixture.AbsolutePath("schemas/fallback-profiles.schema.json"));

        Diagnostic diagnostic = AssertInvalid(
            fixture,
            "schemas/fallback-profiles.schema.json",
            "exists");

        Assert.Contains("schema", diagnostic.Hint!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoadMalformedFallbackProfilesSchemaReportsJsonAndSchemaLocation()
    {
        using SquadFixture fixture = SquadFixture.CreateValid();
        fixture.Write("schemas/fallback-profiles.schema.json", "{");

        Diagnostic diagnostic = AssertInvalid(
            fixture,
            "schemas/fallback-profiles.schema.json",
            "valid JSON");

        Assert.Contains("schema", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoadFallbackProfilesSchemaUsesUnsupportedIdentityVersionReportsExpectedIdentity()
    {
        using SquadFixture fixture = SquadFixture.CreateValid();
        fixture.Replace(
            "schemas/fallback-profiles.schema.json",
            "https://kyber-weave.dev/schemas/kyber-squad/fallback-profiles/v1",
            "https://kyber-weave.dev/schemas/kyber-squad/fallback-profiles/v2");

        Diagnostic diagnostic = AssertInvalid(
            fixture,
            "schemas/fallback-profiles.schema.json",
            "fallback-profiles/v2");

        Assert.Contains("fallback-profiles/v1", diagnostic.Hint!, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadMissingReferencedPathReportsDeclaringManifestLocation()
    {
        using SquadFixture fixture = SquadFixture.CreateValid();
        fixture.Replace("squad.yml", "profiles/models.yml", "profiles/missing.yml");

        Diagnostic diagnostic = AssertInvalid(fixture, "squad.yml", "profiles/missing.yml");

        Assert.Contains("exists", diagnostic.Hint!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("../outside.yml")]
    [InlineData("profiles/../../outside.yml")]
    [InlineData("/tmp/outside.yml")]
    public void LoadManifestPathEscapesProductRootRejectsBeforeReadingOutsidePath(string unsafePath)
    {
        using SquadFixture fixture = SquadFixture.CreateValid();
        fixture.Replace("squad.yml", "profiles/models.yml", unsafePath);

        Diagnostic diagnostic = AssertInvalid(fixture, "squad.yml", unsafePath);

        Assert.Contains("inside", diagnostic.Hint!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoadReferencedSymlinkEscapesProductRootRejectsResolvedTarget()
    {
        if (OperatingSystem.IsWindows())
        {
            throw SkipException.ForSkip(
                "Symbolic links require elevated privileges on Windows.");
        }

        using SquadFixture fixture = SquadFixture.CreateValid();
        using TempDirectory outside = new TempDirectory();
        string outsideAgent = System.IO.Path.Combine(outside.Path, "architect.md");
        File.WriteAllText(outsideAgent, SquadFixture.ArchitectAgentLf, new UTF8Encoding(false));
        string linkedAgent = fixture.AbsolutePath("agents/architect.md");
        File.Delete(linkedAgent);
        try
        {
            File.CreateSymbolicLink(linkedAgent, outsideAgent);
        }
        catch (UnauthorizedAccessException)
        {
            throw SkipException.ForSkip(
                "Creating symbolic links requires elevated privileges on this host.");
        }

        Diagnostic diagnostic = AssertInvalid(fixture, "agents/architect.md", "symbolic link");

        Assert.Contains("inside", diagnostic.Hint!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoadAgentIsNotValidUtf8ReportsEncodingAndSourceLocation()
    {
        using SquadFixture fixture = SquadFixture.CreateValid();
        File.WriteAllBytes(fixture.AbsolutePath("agents/architect.md"), [0xc3, 0x28]);

        Diagnostic diagnostic = AssertInvalid(fixture, "agents/architect.md", "UTF-8");

        Assert.Contains("UTF-8", diagnostic.Hint!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("agent", "missing-agent")]
    [InlineData("skill", "missing-skill")]
    public void LoadBundleReferencesUnknownIdentityReportsBundleLocation(
        string identityKind,
        string missingIdentity)
    {
        using SquadFixture fixture = SquadFixture.CreateValid();
        string anchor = identityKind == "agent" ? "  - dotnet-dev" : "  - test-dev";
        fixture.Replace("bundles/full.yml", anchor, $"{anchor}\n  - {missingIdentity}");

        Diagnostic diagnostic = AssertInvalid(fixture, "bundles/full.yml", missingIdentity);

        Assert.Contains(identityKind, diagnostic.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("agent", "agents/reviewer.md", "architect")]
    [InlineData("skill", "skills/reviewer/SKILL.md", "test-dev")]
    public void LoadDuplicateIdentityReportsBothSourceLocations(
        string identityKind,
        string duplicateRelativePath,
        string duplicateName)
    {
        using SquadFixture fixture = SquadFixture.CreateValid();
        if (identityKind == "agent")
        {
            fixture.Write(
                duplicateRelativePath,
                SquadFixture.Agent("reviewer", name: duplicateName, aliases: "[]"));
            fixture.Replace("bundles/full.yml", "  - dotnet-dev", "  - dotnet-dev\n  - reviewer");
        }
        else
        {
            fixture.Write(
                duplicateRelativePath,
                SquadFixture.Skill("test-dev", "A second path with the same identity."));
            fixture.Replace("bundles/full.yml", "  - test-dev", "  - test-dev\n  - reviewer");
        }

        SquadSourceValidationException exception = Assert.Throws<SquadSourceValidationException>(
            () => SquadSourceLoader.Load(fixture.Path));
        Diagnostic diagnostic = Assert.Single(
            exception.Diagnostics.Items,
            item => item.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) &&
                    item.Message.Contains(duplicateName, StringComparison.Ordinal));

        Assert.Equal(duplicateRelativePath, diagnostic.FilePath);
        Assert.NotEmpty(diagnostic.RelatedLocations);
        Assert.All(
            diagnostic.RelatedLocations,
            location => Assert.False(System.IO.Path.IsPathRooted(location.FilePath)));
        Assert.False(string.IsNullOrWhiteSpace(diagnostic.Hint));
    }

    [Theory]
    [InlineData("architect")]
    [InlineData("shared-alias")]
    public void LoadAliasCollidesWithCanonicalOrExistingAliasReportsDeclaringAgent(string alias)
    {
        using SquadFixture fixture = SquadFixture.CreateValid();
        fixture.Write(
            "agents/dotnet-dev.md",
            SquadFixture.Agent("dotnet-dev", aliases: $"[{alias}]"));
        if (alias == "shared-alias")
        {
            fixture.Write(
                "agents/architect.md",
                SquadFixture.Agent("architect", aliases: "[shared-alias]"));
        }

        Diagnostic diagnostic = AssertInvalid(fixture, "agents/dotnet-dev.md", alias);

        Assert.Contains("alias", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoadAliasReferenceInBundleRejectsNonCanonicalIdentity()
    {
        using SquadFixture fixture = SquadFixture.CreateValid();
        fixture.Write(
            "agents/architect.md",
            SquadFixture.Agent("architect", aliases: "[planner]"));
        fixture.Replace("bundles/full.yml", "  - architect", "  - planner");

        Diagnostic diagnostic = AssertInvalid(fixture, "bundles/full.yml", "planner");

        Assert.Contains("canonical", diagnostic.Hint!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("agent", "agents/architect.md", "role-architect")]
    [InlineData("alias", "agents/architect.md", "role-planner")]
    [InlineData("skill", "skills/test-dev/SKILL.md", "role-test-dev")]
    public void LoadIdentityUsesReservedFallbackPrefixReportsDeclaringSourceAndFallbackProfile(
        string identityKind,
        string expectedSourcePath,
        string reservedIdentity)
    {
        using SquadFixture fixture = SquadFixture.CreateValid();
        switch (identityKind)
        {
            case "agent":
                fixture.Write(
                    expectedSourcePath,
                    SquadFixture.Agent("architect", name: reservedIdentity));
                fixture.Replace("bundles/full.yml", "  - architect", $"  - {reservedIdentity}");
                break;
            case "alias":
                fixture.Write(
                    expectedSourcePath,
                    SquadFixture.Agent("architect", aliases: $"[{reservedIdentity}]"));
                break;
            case "skill":
                fixture.Write(
                    expectedSourcePath,
                    SquadFixture.Skill(reservedIdentity, "Use when writing tests."));
                fixture.Replace("bundles/full.yml", "  - test-dev", $"  - {reservedIdentity}");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(identityKind));
        }

        SquadSourceValidationException exception = Assert.Throws<SquadSourceValidationException>(
            () => SquadSourceLoader.Load(fixture.Path));
        Diagnostic diagnostic = Assert.Single(
            exception.Diagnostics.Items,
            item => string.Equals(item.FilePath, expectedSourcePath, StringComparison.Ordinal) &&
                    item.Message.Contains(reservedIdentity, StringComparison.Ordinal));

        Assert.Equal(SquadSourceValidator.InvalidSourceRule, diagnostic.Code);
        Assert.Contains("role-", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("reserved", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("rename", diagnostic.Hint!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            diagnostic.RelatedLocations,
            location => string.Equals(
                location.FilePath,
                "profiles/fallbacks.yml",
                StringComparison.Ordinal));
    }

    [Fact]
    public void LoadIdentitiesDifferingOnlyByReservedPrefixCaseRemainValid()
    {
        using SquadFixture fixture = SquadFixture.CreateValid();
        fixture.Write(
            "agents/architect.md",
            SquadFixture.Agent("architect", name: "Role-architect", aliases: "[Role-planner]"));
        fixture.Write(
            "skills/test-dev/SKILL.md",
            SquadFixture.Skill("Role-test-dev", "Use when writing tests."));
        fixture.Replace("bundles/full.yml", "  - architect", "  - Role-architect");
        fixture.Replace("bundles/full.yml", "  - test-dev", "  - Role-test-dev");

        SquadSource source = SquadSourceLoader.Load(fixture.Path);

        SquadAgent agent = Assert.Single(source.Agents, item => item.Name == "Role-architect");
        Assert.Equal(["Role-planner"], agent.Aliases);
        Assert.Contains(source.Skills, item => item.Name == "Role-test-dev");
    }

    [Fact]
    public void LoadLfAndUtf8BomVariantsProduceSameNormalizedBodyAndStableSha256()
    {
        using SquadFixture lfFixture = SquadFixture.CreateValid();
        using SquadFixture crlfFixture = SquadFixture.CreateValid();
        crlfFixture.Write(
            "agents/architect.md",
            SquadFixture.ArchitectAgentLf.Replace("\n", "\r\n", StringComparison.Ordinal),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        SquadAgent lfAgent = SquadSourceLoader.Load(lfFixture.Path).Agents.Single(agent => agent.Name == "architect");
        SquadAgent crlfAgent = SquadSourceLoader.Load(crlfFixture.Path).Agents.Single(agent => agent.Name == "architect");

        Assert.Equal(NormalizedArchitectBody, lfAgent.InstructionBody);
        Assert.Equal(NormalizedArchitectBody, crlfAgent.InstructionBody);
        Assert.Equal(NormalizedArchitectBodySha256, lfAgent.BodyDigest);
        Assert.Equal(lfAgent.BodyDigest, crlfAgent.BodyDigest);
    }

    [Fact]
    public void LoadAgentWithUnknownFrontmatterFieldReportsMatchingMarkdownLineNumber()
    {
        using SquadFixture fixture = SquadFixture.CreateValid();
        string invalidAgent =
            "---\n" +
            "schema: kyber-squad.agent/v1\n" +
            "name: architect\n" +
            "unknown-field: value\n" +
            "description: Use when planning.\n" +
            "invocation: subagent\n" +
            "model-profile: deep-planning\n" +
            "capability-profile: architect\n" +
            "delegates-to: [dotnet-dev]\n" +
            "fallback: role-skill\n" +
            "aliases: []\n" +
            "---\n" +
            "You are an architect.\n";

        fixture.Write("agents/architect.md", invalidAgent);

        Diagnostic diagnostic = AssertInvalid(fixture, "agents/architect.md", "unknown-field");
        Assert.Equal(4, diagnostic.StartLine);
    }

    [Fact]
    public void LoadSkillWithUnknownFrontmatterFieldReportsMatchingMarkdownLineNumber()
    {
        using SquadFixture fixture = SquadFixture.CreateValid();
        string invalidSkill =
            "---\n" +
            "name: test-dev\n" +
            "unexpected: field\n" +
            "description: Use when writing tests.\n" +
            "license: MIT\n" +
            "---\n" +
            "# test-dev\n";

        fixture.Write("skills/test-dev/SKILL.md", invalidSkill);

        Diagnostic diagnostic = AssertInvalid(fixture, "skills/test-dev/SKILL.md", "unexpected");
        Assert.Equal(3, diagnostic.StartLine);
    }

    [Fact]
    public void LoadAgentWithInvalidYamlSyntaxReportsMatchingMarkdownLineNumber()
    {
        using SquadFixture fixture = SquadFixture.CreateValid();
        string invalidAgent =
            "---\n" +
            "schema: kyber-squad.agent/v1\n" +
            "name: \"unclosed\n" +
            "description: Use when planning.\n" +
            "---\n" +
            "You are an architect.\n";

        fixture.Write("agents/architect.md", invalidAgent);

        Diagnostic diagnostic = AssertInvalid(fixture, "agents/architect.md", "YAML source is invalid");
        Assert.Equal(3, diagnostic.StartLine);
    }

    private static Diagnostic AssertInvalid(
        SquadFixture fixture,
        string expectedRelativePath,
        string expectedMessageFragment)
    {
        SquadSourceValidationException exception = Assert.Throws<SquadSourceValidationException>(
            () => SquadSourceLoader.Load(fixture.Path));
        Diagnostic diagnostic = Assert.Single(
            exception.Diagnostics.Items,
            item => string.Equals(item.FilePath, expectedRelativePath, StringComparison.Ordinal) &&
                    (item.Message.Contains(expectedMessageFragment, StringComparison.OrdinalIgnoreCase) ||
                     (item.Hint?.Contains(expectedMessageFragment, StringComparison.OrdinalIgnoreCase) ?? false)));

        Assert.Equal(Severity.Error, diagnostic.Severity);
        Assert.False(System.IO.Path.IsPathRooted(diagnostic.FilePath!));
        Assert.False(string.IsNullOrWhiteSpace(diagnostic.Hint));
        return diagnostic;
    }

    private sealed class SquadFixture : IDisposable
    {
        internal const string ArchitectAgentLf =
            "---\n" +
            "schema: kyber-squad.agent/v1\n" +
            "name: architect\n" +
            "description: Use when planning multi-domain work.\n" +
            "invocation: subagent\n" +
            "model-profile: deep-planning\n" +
            "capability-profile: architect\n" +
            "delegates-to: [dotnet-dev]\n" +
            "fallback: role-skill\n" +
            "aliases: []\n" +
            "---\n" +
            NormalizedArchitectBody;

        private readonly TempDirectory _temp = new();

        public string Path => _temp.Path;

        private SquadFixture()
        {
        }

        public static SquadFixture CreateValid()
        {
            SquadFixture fixture = new SquadFixture();
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
                  - dotnet-dev
                skills:
                  - test-dev
                """);
            fixture.Write("profiles/models.yml", """
                schema: kyber-squad.model-profiles/v1
                profiles:
                  deep-planning:
                    default: inherit
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
                  architect:
                    permissions:
                      filesystem.read: allow
                      filesystem.write: deny
                      delegate: ask
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
                  "mcpServers": {
                    "kyber-weave": {
                      "command": "kyber-weave-mcp",
                      "args": []
                    }
                  }
                }
                """);
            fixture.Write("agents/architect.md", ArchitectAgentLf);
            fixture.Write("agents/dotnet-dev.md", Agent("dotnet-dev"));
            fixture.Write("skills/test-dev/SKILL.md", Skill("test-dev", "Use when writing tests."));

            foreach (string? schema in new[]
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

        public static string Agent(
            string fileIdentity,
            string? name = null,
            string aliases = "[]") =>
            "---\n" +
            "schema: kyber-squad.agent/v1\n" +
            $"name: {name ?? fileIdentity}\n" +
            $"description: Use when acting as {fileIdentity}.\n" +
            "invocation: subagent\n" +
            "model-profile: general\n" +
            "capability-profile: worker\n" +
            "delegates-to: []\n" +
            "fallback: role-skill\n" +
            $"aliases: {aliases}\n" +
            "---\n" +
            $"You are {fileIdentity}.\n";

        public static string Skill(string name, string description) =>
            "---\n" +
            $"name: {name}\n" +
            $"description: {description}\n" +
            "license: MIT\n" +
            "metadata:\n" +
            "  author: Kyber-Weave\n" +
            "  version: 1.0.0\n" +
            "---\n" +
            $"# {name}\n";

        public string AbsolutePath(string relativePath) =>
            System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));

        public void Replace(string relativePath, string oldValue, string newValue)
        {
            string path = AbsolutePath(relativePath);
            string content = File.ReadAllText(path);
            Assert.Contains(oldValue, content, StringComparison.Ordinal);
            File.WriteAllText(
                path,
                content.Replace(oldValue, newValue, StringComparison.Ordinal),
                new UTF8Encoding(false));
        }

        public void Write(string relativePath, string content) =>
            Write(relativePath, content, new UTF8Encoding(false));

        public void Write(string relativePath, string content, Encoding encoding)
        {
            string path = AbsolutePath(relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content, encoding);
        }

        public void Dispose() => _temp.Dispose();
    }
}
