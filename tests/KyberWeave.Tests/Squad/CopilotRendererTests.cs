using System.Text;
using System.Text.RegularExpressions;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using KyberWeave.Core.Squad.Rendering;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Unit tests pinning GitHub Copilot agent rendering in <see cref="CopilotRenderer"/>,
/// specifically verifying YAML flow sequence formatting for the <c>tools</c> frontmatter field,
/// explicit single-quoting of MCP server wildcards, capability gating for MCP tools, inclusion
/// of standard environment tools (<c>vscode</c>, <c>todo</c>), and deterministic tool ordering.
/// </summary>
public sealed class CopilotRendererTests
{
    private static readonly string ProductRoot =
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");

    private static readonly Regex ToolsFlowSequenceRegex =
        new(@"^tools:\s*\[(?<items>.*)\]$", RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly string[] McpWildcardTrio =
        ["'codegraph/*'", "'kyber-weave/*'", "'context7/*'"];

    [Fact]
    public async Task RenderAsync_EmitsToolsAsYamlFlowSequence_AcrossAllRenderedAgents()
    {
        SquadRendererRegistry registry = new([new CopilotRenderer()]);
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Copilot],
            Scope: SquadDeploymentScope.Project);

        SquadRenderResult result = await registry.RenderAsync(request);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        IEnumerable<SquadDeploymentFile> agentFiles = result.Files
            .Where(f => f.RelativePath.StartsWith(".github/agents/", StringComparison.Ordinal) &&
                        f.RelativePath.EndsWith(".agent.md", StringComparison.Ordinal));

        Assert.NotEmpty(agentFiles);

        foreach (SquadDeploymentFile file in agentFiles)
        {
            string content = Encoding.UTF8.GetString(file.Content.Span);
            string frontmatter = ExtractFrontmatterText(content);

            Match match = ToolsFlowSequenceRegex.Match(frontmatter);
            Assert.True(
                match.Success,
                $"Agent file '{file.RelativePath}' frontmatter does not contain a flow-style 'tools: [...]' line.\nFrontmatter:\n{frontmatter}");

            // Verify tools is not rendered as a YAML block list (e.g. "tools:\n  - " or "tools:\n- ")
            Assert.DoesNotMatch(@"tools:\s*\n\s*-", frontmatter);
        }
    }

    [Fact]
    public async Task RenderAsync_SingleQuotesAllMcpServerWildcardsInRawYamlText()
    {
        SquadRendererRegistry registry = new([new CopilotRenderer()]);
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Copilot],
            Scope: SquadDeploymentScope.Project);

        SquadRenderResult result = await registry.RenderAsync(request);

        Assert.True(result.Success, string.Join("; ", result.Errors));

        // Find worker / analytical agents that have filesystem.read enabled (e.g. architect, csharp-dev)
        SquadDeploymentFile architectFile = Assert.Single(
            result.Files,
            f => f.RelativePath == ".github/agents/architect.agent.md");

        string architectContent = Encoding.UTF8.GetString(architectFile.Content.Span);
        string architectFrontmatter = ExtractFrontmatterText(architectContent);
        string architectToolsLine = ExtractToolsLine(architectFrontmatter);

        foreach (string wildcard in McpWildcardTrio)
        {
            Assert.Contains(wildcard, architectToolsLine, StringComparison.Ordinal);
        }

        // Verify wildcards are never unquoted (e.g. "codegraph/*" without quotes) or double-quoted
        Assert.DoesNotContain("codegraph/*,", architectToolsLine.Replace("'codegraph/*'", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.DoesNotContain("\"codegraph/*\"", architectToolsLine, StringComparison.Ordinal);
        Assert.DoesNotContain("\"kyber-weave/*\"", architectToolsLine, StringComparison.Ordinal);
        Assert.DoesNotContain("\"context7/*\"", architectToolsLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RenderAsync_AlwaysIncludesVscodeAndTodo_AcrossAllAgents()
    {
        SquadRendererRegistry registry = new([new CopilotRenderer()]);
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Copilot],
            Scope: SquadDeploymentScope.Project);

        SquadRenderResult result = await registry.RenderAsync(request);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        IEnumerable<SquadDeploymentFile> agentFiles = result.Files
            .Where(f => f.RelativePath.StartsWith(".github/agents/", StringComparison.Ordinal) &&
                        f.RelativePath.EndsWith(".agent.md", StringComparison.Ordinal));

        foreach (SquadDeploymentFile file in agentFiles)
        {
            string content = Encoding.UTF8.GetString(file.Content.Span);
            string toolsLine = ExtractToolsLine(ExtractFrontmatterText(content));

            Assert.Contains("vscode", toolsLine, StringComparison.Ordinal);
            Assert.Contains("todo", toolsLine, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task RenderAsync_PureOrchestratorAgents_WithholdMcpWildcardsDespiteFilesystemRead()
    {
        SquadRendererRegistry registry = new([new CopilotRenderer()]);
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Copilot],
            Scope: SquadDeploymentScope.Project);

        SquadRenderResult result = await registry.RenderAsync(request);

        Assert.True(result.Success, string.Join("; ", result.Errors));

        foreach (string orchestrator in new[] { "conductor", "conductor-v3" })
        {
            SquadDeploymentFile file = Assert.Single(
                result.Files,
                f => f.RelativePath == $".github/agents/{orchestrator}.agent.md");

            string content = Encoding.UTF8.GetString(file.Content.Span);
            string toolsLine = ExtractToolsLine(ExtractFrontmatterText(content));

            // Conductor agents have filesystem.read: allow, but must strictly withhold MCP wildcards
            Assert.DoesNotContain("codegraph", toolsLine, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("kyber-weave", toolsLine, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("context7", toolsLine, StringComparison.OrdinalIgnoreCase);

            Assert.Equal("tools: [vscode, read, agent, todo]", toolsLine);
        }
    }

    [Fact]
    public async Task RenderAsync_AgentsWithFilesystemRead_IncludeMcpWildcards()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadRendererRegistry registry = new([new CopilotRenderer()]);
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Copilot],
            Scope: SquadDeploymentScope.Project);

        SquadRenderResult result = await registry.RenderAsync(request);

        Assert.True(result.Success, string.Join("; ", result.Errors));

        foreach (SquadAgent agent in source.Agents)
        {
            bool isOrchestrator = agent.Name is "conductor" or "conductor-v3";
            SquadCapabilityProfile profile = source.CapabilityProfiles.Profiles[agent.CapabilityProfile];
            bool hasRead = profile.Permissions["filesystem.read"] == SquadPermissionDecision.Allow;

            SquadDeploymentFile file = Assert.Single(
                result.Files,
                f => f.RelativePath == $".github/agents/{agent.Name}.agent.md");

            string content = Encoding.UTF8.GetString(file.Content.Span);
            string toolsLine = ExtractToolsLine(ExtractFrontmatterText(content));

            if (hasRead && !isOrchestrator)
            {
                foreach (string wildcard in McpWildcardTrio)
                {
                    Assert.Contains(wildcard, toolsLine, StringComparison.Ordinal);
                }
            }
            else
            {
                Assert.DoesNotContain("codegraph", toolsLine, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("kyber-weave", toolsLine, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("context7", toolsLine, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Theory]
    [InlineData("csharp-dev", "tools: [vscode, execute, read, 'codegraph/*', 'kyber-weave/*', 'context7/*', edit, search, todo]")]
    [InlineData("python-dev", "tools: [vscode, execute, read, 'codegraph/*', 'kyber-weave/*', 'context7/*', edit, search, todo]")]
    [InlineData("react-dev", "tools: [vscode, execute, read, 'codegraph/*', 'kyber-weave/*', 'context7/*', edit, search, todo]")]
    [InlineData("test-dev", "tools: [vscode, execute, read, 'codegraph/*', 'kyber-weave/*', 'context7/*', edit, search, todo]")]
    [InlineData("maui-dev", "tools: [vscode, execute, read, 'codegraph/*', 'kyber-weave/*', 'context7/*', edit, search, todo]")]
    [InlineData("tauri-dev", "tools: [vscode, execute, read, 'codegraph/*', 'kyber-weave/*', 'context7/*', edit, search, todo]")]
    [InlineData("dal-dev", "tools: [vscode, execute, read, 'codegraph/*', 'kyber-weave/*', 'context7/*', edit, search, todo]")]
    [InlineData("conductor", "tools: [vscode, read, agent, todo]")]
    [InlineData("conductor-v3", "tools: [vscode, read, agent, todo]")]
    [InlineData("architect", "tools: [vscode, read, 'codegraph/*', 'kyber-weave/*', 'context7/*', search, web, todo]")]
    [InlineData("architect-v3", "tools: [vscode, read, 'codegraph/*', 'kyber-weave/*', 'context7/*', search, web, todo]")]
    [InlineData("github-devops", "tools: [vscode, execute, read, 'codegraph/*', 'kyber-weave/*', 'context7/*', edit, search, web, todo]")]
    // execute and agent are the widened reviewer profile reaching the tool allow-list; edit
    // is absent because filesystem.write=ask has no per-tool confirmation gate here and
    // safely narrows to deny — the reviewer returns findings rather than writing them.
    [InlineData("code-reviewer", "tools: [vscode, execute, read, 'codegraph/*', 'kyber-weave/*', 'context7/*', search, agent, web, todo]")]
    [InlineData("azure-reader", "tools: [vscode, read, 'codegraph/*', 'kyber-weave/*', 'context7/*', search, web, todo]")]
    [InlineData("research-agent", "tools: [vscode, read, 'codegraph/*', 'kyber-weave/*', 'context7/*', search, web, todo]")]
    [InlineData("bug-crusher-investigator", "tools: [vscode, execute, read, 'codegraph/*', 'kyber-weave/*', 'context7/*', search, web, todo]")]
    [InlineData("docs-dev", "tools: [vscode, read, 'codegraph/*', 'kyber-weave/*', 'context7/*', edit, search, todo]")]
    // Both lens seats are read-only: they read the diff and report. Neither executes, writes,
    // nor delegates — the reviewer that spawned them holds those grants, and a council seat
    // that could re-enter the council is a loop nobody bounded.
    [InlineData("review-lens", "tools: [vscode, read, 'codegraph/*', 'kyber-weave/*', 'context7/*', search, web, todo]")]
    [InlineData("review-triage", "tools: [vscode, read, 'codegraph/*', 'kyber-weave/*', 'context7/*', search, web, todo]")]
    public async Task RenderAsync_EmitsDeterministicCanonicalToolOrdering(string agentName, string expectedToolsLine)
    {
        SquadRendererRegistry registry = new([new CopilotRenderer()]);
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Copilot],
            Scope: SquadDeploymentScope.Project);

        SquadRenderResult result = await registry.RenderAsync(request);

        Assert.True(result.Success, string.Join("; ", result.Errors));

        SquadDeploymentFile file = Assert.Single(
            result.Files,
            f => f.RelativePath == $".github/agents/{agentName}.agent.md");

        string content = Encoding.UTF8.GetString(file.Content.Span);
        string actualToolsLine = ExtractToolsLine(ExtractFrontmatterText(content));

        Assert.Equal(expectedToolsLine, actualToolsLine);
    }

    /// <summary>
    /// The triage seat exists to run cheaper than the judgement seat. If its model profile
    /// silently resolved to the same tier, the second role would be pure overhead — so the
    /// tier is asserted rather than assumed.
    /// </summary>
    [Theory]
    [InlineData("review-lens", "Grok 4.5 (copilot)")]
    [InlineData("review-triage", "GPT-5.6 Luna (copilot)")]
    public async Task RenderAsync_LensSeatsResolveToTheirDeclaredModelTier(string agentName, string expectedModel)
    {
        SquadRendererRegistry registry = new([new CopilotRenderer()]);
        SquadRenderResult result = await registry.RenderAsync(new SquadRenderRequest(
            SourceDirectory: ProductRoot,
            Targets: [SquadTarget.Copilot],
            Scope: SquadDeploymentScope.Project));

        Assert.True(result.Success, string.Join("; ", result.Errors));

        SquadDeploymentFile file = Assert.Single(
            result.Files,
            f => f.RelativePath == $".github/agents/{agentName}.agent.md");

        Assert.Contains(
            $"model: {expectedModel}",
            Encoding.UTF8.GetString(file.Content.Span),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RenderAsync_SyntheticProfiles_VerifyCapabilityGatingAndUngovernedTools()
    {
        using TempDirectory temp = new();
        WriteSyntheticSquad(temp.Path, """
            schema: kyber-squad.capability-profiles/v1
            capabilities:
              - filesystem.read
              - filesystem.search
              - filesystem.write
              - process.execute
              - network.read
              - network.publish
              - delegate
            profiles:
              exec-only:
                permissions:
                  filesystem.read: deny
                  filesystem.search: deny
                  filesystem.write: deny
                  process.execute: allow
                  network.read: deny
                  network.publish: deny
                  delegate: deny
              read-only-custom:
                permissions:
                  filesystem.read: allow
                  filesystem.search: deny
                  filesystem.write: deny
                  process.execute: deny
                  network.read: deny
                  network.publish: deny
                  delegate: deny
              all-denied:
                permissions:
                  filesystem.read: deny
                  filesystem.search: deny
                  filesystem.write: deny
                  process.execute: deny
                  network.read: deny
                  network.publish: deny
                  delegate: deny
            """);

        CopilotRenderer renderer = new();
        SquadRenderRequest request = new(
            SourceDirectory: temp.Path,
            Targets: [SquadTarget.Copilot],
            Scope: SquadDeploymentScope.Project);

        SquadRenderResult result = await renderer.RenderAsync(request);
        Assert.True(result.Success, string.Join("; ", result.Errors));

        // 1. Agent with process.execute allow and filesystem.read deny -> [vscode, execute, todo] (no MCP wildcards)
        SquadDeploymentFile execAgent = Assert.Single(result.Files, f => f.RelativePath == ".github/agents/exec-bot.agent.md");
        Assert.Equal(
            "tools: [vscode, execute, todo]",
            ExtractToolsLine(ExtractFrontmatterText(Encoding.UTF8.GetString(execAgent.Content.Span))));

        // 2. Custom non-orchestrator agent with filesystem.read allow -> [vscode, read, 'codegraph/*', 'kyber-weave/*', 'context7/*', todo]
        SquadDeploymentFile readAgent = Assert.Single(result.Files, f => f.RelativePath == ".github/agents/read-bot.agent.md");
        Assert.Equal(
            "tools: [vscode, read, 'codegraph/*', 'kyber-weave/*', 'context7/*', todo]",
            ExtractToolsLine(ExtractFrontmatterText(Encoding.UTF8.GetString(readAgent.Content.Span))));

        // 3. Agent with all capabilities denied -> [vscode, todo]
        SquadDeploymentFile lockedAgent = Assert.Single(result.Files, f => f.RelativePath == ".github/agents/locked-bot.agent.md");
        Assert.Equal(
            "tools: [vscode, todo]",
            ExtractToolsLine(ExtractFrontmatterText(Encoding.UTF8.GetString(lockedAgent.Content.Span))));

        // 4. Pure orchestrator agent named conductor with filesystem.read allow -> excludes MCP wildcards
        SquadDeploymentFile conductorAgent = Assert.Single(result.Files, f => f.RelativePath == ".github/agents/conductor.agent.md");
        Assert.Equal(
            "tools: [vscode, read, todo]",
            ExtractToolsLine(ExtractFrontmatterText(Encoding.UTF8.GetString(conductorAgent.Content.Span))));
    }

    private static void WriteSyntheticSquad(string root, string capabilitiesYaml)
    {
        Directory.CreateDirectory(Path.Combine(root, "bundles"));
        Directory.CreateDirectory(Path.Combine(root, "profiles"));
        Directory.CreateDirectory(Path.Combine(root, "agents"));
        Directory.CreateDirectory(Path.Combine(root, "schemas"));

        foreach (string schemaFile in Directory.GetFiles(Path.Combine(ProductRoot, "schemas"), "*.json"))
        {
            File.Copy(schemaFile, Path.Combine(root, "schemas", Path.GetFileName(schemaFile)), overwrite: true);
        }

        File.WriteAllText(Path.Combine(root, "squad.yml"), """
            schema: kyber-squad.squad/v1
            name: test-squad
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

        File.WriteAllText(Path.Combine(root, "toolchain.yml"), """
            schema: kyber-squad.toolchain/v1
            required-features:
              - agent-ir/v1
            validated-release: null
            """);

        File.WriteAllText(Path.Combine(root, "mcp.json"), """{ "mcpServers": {} }""");

        File.WriteAllText(Path.Combine(root, "bundles", "full.yml"), """
            schema: kyber-squad.bundle/v1
            name: full
            agents:
              - exec-bot
              - read-bot
              - locked-bot
              - conductor
            skills: []
            """);

        File.WriteAllText(Path.Combine(root, "profiles", "models.yml"), """
            schema: kyber-squad.model-profiles/v1
            profiles:
              default:
                default: inherit
            """);

        File.WriteAllText(Path.Combine(root, "profiles", "capabilities.yml"), capabilitiesYaml);

        File.WriteAllText(Path.Combine(root, "profiles", "fallbacks.yml"), """
            schema: kyber-squad.fallback-profiles/v1
            profiles:
              none:
                no-primary-agent: skill
                no-agent-primitive: skill
            """);

        WriteAgent(root, "exec-bot", "exec-only");
        WriteAgent(root, "read-bot", "read-only-custom");
        WriteAgent(root, "locked-bot", "all-denied");
        WriteAgent(root, "conductor", "read-only-custom");
    }

    private static void WriteAgent(string root, string name, string capabilityProfile)
    {
        File.WriteAllText(Path.Combine(root, "agents", $"{name}.md"), $"""
            ---
            schema: kyber-squad.agent/v1
            name: {name}
            description: Test agent {name}
            invocation: subagent
            model-profile: default
            capability-profile: {capabilityProfile}
            delegates-to: []
            fallback: none
            aliases: []
            ---
            Instruction body for {name}.
            """);
    }

    private static string ExtractFrontmatterText(string markdown)
    {
        const string delimiter = "---\n";
        Assert.StartsWith(delimiter, markdown, StringComparison.Ordinal);
        int end = markdown.IndexOf("\n---\n", delimiter.Length, StringComparison.Ordinal);
        Assert.True(end > 0, "Expected closing frontmatter delimiter.");
        return markdown[delimiter.Length..end];
    }

    private static string ExtractToolsLine(string frontmatter)
    {
        string[] lines = frontmatter.Split('\n');
        string? toolsLine = lines.FirstOrDefault(l => l.StartsWith("tools:", StringComparison.Ordinal));
        Assert.NotNull(toolsLine);
        return toolsLine.Trim();
    }
}
