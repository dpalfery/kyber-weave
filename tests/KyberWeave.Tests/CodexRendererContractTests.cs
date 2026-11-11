using System.Text;
using KyberWeave.Tests.Fakes;
using System.Text.RegularExpressions;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using KyberWeave.Core.Squad.Rendering;
using Xunit;
using YamlDotNet.RepresentationModel;
using static KyberWeave.Tests.Fakes.SquadRenderedOutput;

namespace KyberWeave.Tests;

/// <summary>
/// Renders the real canonical Squad source (<c>products/kyber-squad</c>) through
/// <see cref="SquadRendererRegistry"/> with <see cref="CodexRenderer"/> and validates the result
/// against Codex's native agent TOML and skill contracts.
/// </summary>
/// <remarks>
/// Validates that canonical agents render as Codex native agent TOML primitives at
/// <c>.codex/agents/&lt;name&gt;.toml</c> and skills render at <c>.codex/skills/&lt;name&gt;/SKILL.md</c>.
/// Verifies profile-declared shared-identity suppression,
/// model resolution from <c>models.yml</c>, TOML structure and instruction encoding,
/// and structured degradation accounting (<c>permission-not-expressible</c> with matching <c>InstructionDigest</c>).
/// </remarks>
public sealed class CodexRendererContractTests : IDisposable
{
    private static readonly string ProductRoot =
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");

    private readonly TempDirectory _temp = new();

    public void Dispose()
    {
        _temp.Dispose();
    }

    /// <summary>
    ///     Codex's own decisions on the real corpus: every agent is a TOML agent file whose model
    ///     follows the <c>codex</c> column verbatim and whose body is the developer instructions;
    ///     the primary agent also ships an entry-point skill carrying its body; and every non-deny
    ///     decision is recorded as inexpressible. The skill documents and record identity are the
    ///     shared pipeline's, checked in <see cref="Squad.SquadRendererSharedContractTests"/>.
    /// </summary>
    [Fact]
    public async Task RenderAsync_Codex_RendersTheRealCanonicalCorpus()
    {
        SquadSource source = TestSquadCorpus.CanonicalSource;
        SquadRenderResult result = await new SquadRendererRegistry([new CodexRenderer()]).RenderAsync(
            new SquadRenderRequest(ProductRoot, [SquadTarget.Codex], SquadDeploymentScope.Project));

        Assert.True(result.Success, string.Join("; ", result.Errors));
        foreach (SquadAgent agent in source.Agents)
        {
            SquadDeploymentFile file = Assert.Single(result.Files, f => f.RelativePath == $".codex/agents/{agent.Name}.toml");
            ParsedTomlAgent parsed = ParseCodexAgentToml(Encoding.UTF8.GetString(file.Content.Span), agent.Name);
            Assert.Equal(agent.Name, parsed.Name);
            Assert.Equal(TestSquadCorpus.ExpectedRenderText(SquadTarget.Codex, agent.Description), parsed.Description);

            SquadModelProfile modelProfile = source.ModelProfiles.Profiles[agent.ModelProfile];
            string? expectedModel = modelProfile.HarnessModels.TryGetValue("codex", out string? codexModel)
                ? codexModel
                : modelProfile.Default == "inherit" ? null : modelProfile.Default;
            Assert.Equal(expectedModel, parsed.Model);

            Assert.Equal(
                Normalized(TestSquadCorpus.ExpectedBodyText(SquadTarget.Codex, agent)),
                parsed.DeveloperInstructions);
        }

        // The other primary agents are shared identities whose canonical skill is the entry point.
        SquadAgent conductor = Assert.Single(source.Agents, agent => agent.Name == "conductor");
        SquadDeploymentFile entryPoint = Assert.Single(result.Files, f => f.RelativePath == $".codex/skills/{conductor.Name}/SKILL.md");
        (_, string entryPointBody) = SplitFrontmatter(Encoding.UTF8.GetString(entryPoint.Content.Span), conductor.Name);
        Assert.Equal(
            Normalized(TestSquadCorpus.ExpectedRenderText(SquadTarget.Codex, conductor.InstructionBody)),
            entryPointBody);
        Assert.All(conductor.Resources, resource => Assert.Contains(
            result.Files,
            f => f.RelativePath == $".codex/skills/{conductor.Name}/{resource.RelativePath}"));

        Assert.Equal(
            source.Agents
                .Where(a => source.CapabilityProfiles.Profiles[a.CapabilityProfile].Permissions.Any(
                    pair => source.CapabilityProfiles.Capabilities.Contains(pair.Key) && pair.Value != SquadPermissionDecision.Deny))
                .Select(a => a.Name)
                .Order(StringComparer.Ordinal),
            result.Degradations.Select(d => d.CanonicalIdentity).Order(StringComparer.Ordinal));
        Assert.All(result.Degradations, d =>
        {
            Assert.Equal("permission-not-expressible", d.Code);
            Assert.Contains(source.Agents.Single(a => a.Name == d.CanonicalIdentity).CapabilityProfile, d.Details, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task RenderAsync_Codex_GlobalScopePlacesThePrimaryAgentEntryPointSkillUnderSkills()
    {
        SquadSource source = TestSquadCorpus.CanonicalSource;
        SquadAgent primaryAgent = Assert.Single(source.Agents, agent => agent.Name == "conductor");
        SquadRenderRequest request = new(
            ProductRoot,
            [SquadTarget.Codex],
            SquadDeploymentScope.Global);

        SquadRenderResult result = await new CodexRenderer().RenderAsync(request);

        Assert.True(result.Success, string.Join("; ", result.Errors));

        string expectedAgentBody = TestSquadCorpus
            .ExpectedRenderText(SquadTarget.Codex, primaryAgent.InstructionBody)
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!expectedAgentBody.EndsWith('\n'))
        {
            expectedAgentBody += "\n";
        }

        SquadDeploymentFile agentFile = Assert.Single(
            result.Files,
            file => file.RelativePath == $"agents/{primaryAgent.Name}.toml");
        ParsedTomlAgent parsedAgent = ParseCodexAgentToml(
            Encoding.UTF8.GetString(agentFile.Content.Span),
            primaryAgent.Name);
        Assert.Equal(primaryAgent.Name, parsedAgent.Name);
        Assert.Equal(expectedAgentBody, parsedAgent.DeveloperInstructions);

        SquadDeploymentFile skillFile = Assert.Single(
            result.Files,
            file => file.RelativePath == $"skills/{primaryAgent.Name}/SKILL.md");
        (YamlMappingNode frontmatter, string skillBody) = SplitFrontmatter(
            Encoding.UTF8.GetString(skillFile.Content.Span),
            primaryAgent.Name);
        Assert.Equal(primaryAgent.Name, RequireScalar(frontmatter, "name", primaryAgent.Name));
        Assert.Equal("MIT", RequireScalar(frontmatter, "license", primaryAgent.Name));
        Assert.Equal(expectedAgentBody, skillBody);

        foreach (SquadResource resource in primaryAgent.Resources)
        {
            Assert.Single(
                result.Files,
                file => file.RelativePath == $"skills/{primaryAgent.Name}/{resource.RelativePath}");
        }

        Assert.DoesNotContain(
            result.Files,
            file => file.RelativePath.StartsWith(".codex/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RenderSkill_FrontmatterKeyOrderIsStable()
    {
        SquadSource source = TestSquadCorpus.CanonicalSource;
        HashSet<string> sharedIdentities = SharedIdentities(source);
        string probe = source.Skills
                           .Select(skill => skill.Name)
                           .Where(name => !sharedIdentities.Contains(name))
                           .OrderBy(name => name, StringComparer.Ordinal)
                           .FirstOrDefault()
                       ?? throw new InvalidOperationException(
                           $"Corpus at '{ProductRoot}' declares no non-suppressed skills.");

        SquadRenderResult result = await new CodexRenderer().RenderAsync(new SquadRenderRequest(
            ProductRoot,
            [SquadTarget.Codex],
            SquadDeploymentScope.Project));

        SquadDeploymentFile rendered = Assert.Single(
            result.Files,
            f => f.RelativePath == $".codex/skills/{probe}/SKILL.md");

        (YamlMappingNode frontmatter, _) = SplitFrontmatter(
            Encoding.UTF8.GetString(rendered.Content.Span),
            probe);

        string[] keys = frontmatter.Children.Keys
            .OfType<YamlScalarNode>()
            .Select(key => key.Value ?? string.Empty)
            .ToArray();
        Assert.Equal(["name", "description", "license"], keys);
    }

    /// <summary>
    /// Bodies with backslashes and triple quotes must round-trip through rendered TOML:
    /// EscapeTomlMultiline escapes backslashes first, and an independent parser (not a
    /// reverse of the renderer) recovers the original body.
    /// </summary>
    [Fact]
    public async Task RenderAgent_EscapesBackslashesAndTripleQuotes_RoundTripsThroughTomlParse()
    {
        const string fixtureBody =
            "Path C:\\Users\\agent\\project and a fence:\n" +
            "\"\"\"quoted block\"\"\"\n" +
            "trailing slash \\\n";

        string sourceRoot = WriteMinimalSquadSource(_temp.Path, fixtureBody);

        SquadRenderResult result = await new CodexRenderer().RenderAsync(new SquadRenderRequest(
            sourceRoot,
            [SquadTarget.Codex],
            SquadDeploymentScope.Project));

        Assert.True(result.Success, string.Join("; ", result.Errors));
        SquadDeploymentFile agentFile = Assert.Single(
            result.Files,
            f => f.RelativePath == ".codex/agents/escape-probe.toml");

        string toml = Encoding.UTF8.GetString(agentFile.Content.Span);
        ParsedTomlAgent parsed = ParseCodexAgentToml(toml, "escape-probe");

        string expectedBody = fixtureBody.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!expectedBody.EndsWith('\n'))
        {
            expectedBody += "\n";
        }

        Assert.Equal(expectedBody, parsed.DeveloperInstructions);
        Assert.Contains("developer_instructions = \"\"\"", toml, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"(?m)^\s*instructions\s*=", toml);
    }

    private sealed record ParsedTomlAgent(
        string Name,
        string Description,
        string? Model,
        string DeveloperInstructions);

    /// <summary>
    /// Strict minimal TOML reader for Codex agent files: basic strings plus one multiline
    /// <c>developer_instructions</c> field. Escape handling walks the input so <c>\\</c>
    /// and <c>\"""</c> decode correctly without mirroring renderer replace order.
    /// </summary>
    private static string Normalized(string body)
    {
        string normalized = body.Replace("\r\n", "\n", StringComparison.Ordinal);
        return normalized.EndsWith('\n') ? normalized : normalized + "\n";
    }

    private static ParsedTomlAgent ParseCodexAgentToml(string toml, string identity)
    {
        const string multilineOpen = "developer_instructions = \"\"\"\n";
        int multilineIdx = toml.IndexOf(multilineOpen, StringComparison.Ordinal);
        Assert.True(
            multilineIdx >= 0,
            $"Agent '{identity}' is missing required field 'developer_instructions' as a multiline string.");

        string header = toml[..multilineIdx];
        string afterOpen = toml[(multilineIdx + multilineOpen.Length)..];
        int closeIdx = FindMultilineClose(afterOpen);
        Assert.True(closeIdx >= 0, $"Agent '{identity}' developer_instructions block is missing closing quotes.");

        string developerInstructions = UnescapeTomlStringContent(afterOpen[..closeIdx]);

        string? name = null;
        string? description = null;
        string? model = null;

        foreach (string line in header.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            string trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
            {
                continue;
            }

            Match match = Regex.Match(trimmed, @"^(?<key>[A-Za-z0-9_\-]+)\s*=\s*""(?<val>.*)""$");
            Assert.True(match.Success, $"Agent '{identity}' has an unparseable header line: '{trimmed}'.");

            string key = match.Groups["key"].Value;
            string val = UnescapeTomlStringContent(match.Groups["val"].Value);

            switch (key)
            {
                case "name":
                    name = val;
                    break;
                case "description":
                    description = val;
                    break;
                case "model":
                    model = val;
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Agent '{identity}' header contains unexpected key '{key}'.");
            }
        }

        Assert.NotNull(name);
        Assert.NotNull(description);

        return new ParsedTomlAgent(name, description, model, developerInstructions);
    }

    /// <summary>
    /// Locates the closing <c>"""</c> that is not part of a <c>\"""</c> escape sequence.
    /// </summary>
    private static int FindMultilineClose(string afterOpen)
    {
        for (int i = 0; i <= afterOpen.Length - 3; i++)
        {
            if (afterOpen[i] != '"' || afterOpen[i + 1] != '"' || afterOpen[i + 2] != '"')
            {
                continue;
            }

            int backslashCount = 0;
            for (int j = i - 1; j >= 0 && afterOpen[j] == '\\'; j--)
            {
                backslashCount++;
            }

            // An odd number of preceding backslashes means the first quote is escaped.
            if (backslashCount % 2 == 1)
            {
                continue;
            }

            return i;
        }

        return -1;
    }

    /// <summary>
    /// Decodes TOML basic/multiline escape sequences by scanning rather than ordered replaces.
    /// </summary>
    private static string UnescapeTomlStringContent(string value)
    {
        StringBuilder builder = new(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c != '\\')
            {
                builder.Append(c);
                continue;
            }

            Assert.True(i + 1 < value.Length, "TOML string ends with a dangling backslash escape.");
            char next = value[++i];
            builder.Append(next switch
            {
                '\\' => '\\',
                '"' => '"',
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                _ => throw new InvalidOperationException($"Unsupported TOML escape '\\{next}'.")
            });
        }

        return builder.ToString();
    }

    private static string WriteMinimalSquadSource(string root, string agentBody)
    {
        Write(root, "squad.yml", """
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
        Write(root, "bundles/full.yml", """
                                        schema: kyber-squad.bundle/v1
                                        name: full
                                        agents:
                                          - escape-probe
                                        skills: []
                                        """);
        Write(root, "profiles/models.yml", """
                                           schema: kyber-squad.model-profiles/v1
                                           profiles:
                                             general:
                                               default: inherit
                                           """);
        Write(root, "profiles/capabilities.yml", """
                                                 schema: kyber-squad.capability-profiles/v1
                                                 capabilities:
                                                   - filesystem.read
                                                 profiles:
                                                   worker:
                                                     permissions:
                                                       filesystem.read: deny
                                                 """);
        Write(root, "profiles/fallbacks.yml", """
                                              schema: kyber-squad.fallback-profiles/v1
                                              profiles:
                                                role-skill:
                                                  no-primary-agent: skill
                                                  no-agent-primitive: skill
                                              """);
        Write(root, "toolchain.yml", """
                                     schema: kyber-squad.toolchain/v1
                                     required-features:
                                       - agent-ir/v1
                                     validated-release: null
                                     """);
        Write(root, "mcp.json", """
                                {
                                  "mcpServers": {
                                    "kw-docs": {
                                      "command": "kyber-weave-mcp",
                                      "args": ["--repo-root", "."]
                                    }
                                  }
                                }
                                """);
        Write(root, "agents/escape-probe.md",
            "---\n" +
            "schema: kyber-squad.agent/v1\n" +
            "name: escape-probe\n" +
            "description: Probe agent for TOML escape round-trip.\n" +
            "invocation: subagent\n" +
            "model-profile: general\n" +
            "capability-profile: worker\n" +
            "copilot-tools: [vscode]\n" +
            "delegates-to: []\n" +
            "fallback: role-skill\n" +
            "aliases: []\n" +
            "---\n" +
            agentBody);
        Write(root, "schemas/squad.schema.json", MinimalSchema);
        Write(root, "schemas/bundle.schema.json", MinimalSchema);
        Write(root, "schemas/agent.schema.json", MinimalSchema);
        Write(root, "schemas/model-profiles.schema.json", MinimalSchema);
        Write(root, "schemas/capability-profiles.schema.json", MinimalSchema);
        Write(root, "schemas/fallback-profiles.schema.json", """
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

        return root;
    }

    private const string MinimalSchema = """
                                         {
                                           "$schema": "https://json-schema.org/draft/2020-12/schema",
                                           "type": "object"
                                         }
                                         """;

    private static void Write(string root, string relativePath, string content)
    {
        string path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }
}
