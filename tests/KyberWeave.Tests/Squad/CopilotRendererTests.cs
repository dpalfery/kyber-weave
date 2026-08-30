using System.Text;
using System.Text.RegularExpressions;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using KyberWeave.Core.Squad.Rendering;
using Xunit;

namespace KyberWeave.Tests.Squad;

/// <summary>
/// Unit tests pinning GitHub Copilot agent rendering in <see cref="CopilotRenderer"/>,
/// specifically verifying YAML flow sequence formatting for the <c>tools</c> frontmatter field,
/// explicit single-quoting of MCP server wildcards, exact canonical source membership, and
/// deterministic tool ordering.
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
    public void CopilotToolCatalogHasExactApprovedOrder()
    {
        Assert.Equal(
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
            ],
            CopilotToolCatalog.OrderedTools);
    }

    [Theory]
    [InlineData("architect")]
    [InlineData("architect-v3")]
    public void ArchitectCopilotProjectionDoesNotWidenSharedCrossHarnessProfile(string agentName)
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadAgent agent = Assert.Single(source.Agents, candidate => candidate.Name == agentName);

        Assert.Equal("architect", agent.CapabilityProfile);
        Assert.Equal("architect-copilot", agent.CopilotCapabilityProfile);
        Assert.Equal(
            SquadPermissionDecision.Ask,
            source.CapabilityProfiles.Profiles[agent.CapabilityProfile].Permissions["filesystem.write"]);
        Assert.Equal(
            SquadPermissionDecision.Ask,
            source.CapabilityProfiles.Profiles[agent.CapabilityProfile].Permissions["process.execute"]);
        Assert.Equal(
            SquadPermissionDecision.Allow,
            source.CapabilityProfiles.Profiles[agent.CopilotCapabilityProfile!].Permissions["filesystem.write"]);
        Assert.Equal(
            SquadPermissionDecision.Allow,
            source.CapabilityProfiles.Profiles[agent.CopilotCapabilityProfile!].Permissions["process.execute"]);
    }

    [Theory]
    [InlineData("architect")]
    [InlineData("architect-v3")]
    public async Task ArchitectRenderedMetadataKeepsSharedCapabilityProfile(string agentName)
    {
        string frontmatter = await RenderFrontmatterAsync(agentName);

        Assert.Contains("capability-profile: architect", frontmatter, StringComparison.Ordinal);
        Assert.DoesNotContain("architect-copilot", frontmatter, StringComparison.Ordinal);
    }

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
        SquadDeploymentFile[] agentFiles = result.Files
            .Where(f => f.RelativePath.StartsWith(".github/agents/", StringComparison.Ordinal) &&
                        f.RelativePath.EndsWith(".agent.md", StringComparison.Ordinal))
            .ToArray();

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
        SquadDeploymentFile[] agentFiles = result.Files
            .Where(f => f.RelativePath.StartsWith(".github/agents/", StringComparison.Ordinal) &&
                        f.RelativePath.EndsWith(".agent.md", StringComparison.Ordinal))
            .ToArray();

        foreach (SquadDeploymentFile file in agentFiles)
        {
            string content = Encoding.UTF8.GetString(file.Content.Span);
            string toolsLine = ExtractToolsLine(ExtractFrontmatterText(content));

            Assert.Contains("vscode", toolsLine, StringComparison.Ordinal);
            Assert.Contains("todo", toolsLine, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("architect-v3", "tools: [vscode, read, todo, 'codegraph/*', 'kyber-weave/*', 'context7/*', search, execute, web, agent, edit/createDirectory, edit/createFile, edit/editFiles, edit/rename]")]
    [InlineData("architect", "tools: [vscode, read, todo, 'codegraph/*', 'kyber-weave/*', 'context7/*', search, execute, web, agent, edit/createDirectory, edit/createFile, edit/editFiles, edit/rename, vscodeGeneral/rename]")]
    [InlineData("azure-reader", "tools: [vscode, read, todo, 'codegraph/*', 'kyber-weave/*', 'context7/*', search, web]")]
    [InlineData("bug-crusher-investigator", "tools: [vscode, read, todo, 'codegraph/*', 'kyber-weave/*', 'context7/*', search, execute, web]")]
    [InlineData("code-reviewer", "tools: [vscode, read, todo, 'codegraph/*', 'kyber-weave/*', 'context7/*', search, execute, web, agent]")]
    [InlineData("conductor-v3", "tools: [vscode, read, todo, agent]")]
    [InlineData("conductor", "tools: [vscode, read, todo, agent]")]
    [InlineData("csharp-dev", "tools: [vscode, read, todo, 'codegraph/*', 'kyber-weave/*', 'context7/*', search, execute, edit]")]
    [InlineData("dal-dev", "tools: [vscode, read, todo, 'codegraph/*', 'kyber-weave/*', 'context7/*', search, execute, edit]")]
    [InlineData("docs-dev", "tools: [vscode, read, todo, 'codegraph/*', 'kyber-weave/*', 'context7/*', search, edit]")]
    [InlineData("github-devops", "tools: [vscode, read, todo, 'codegraph/*', 'kyber-weave/*', 'context7/*', search, execute, web, edit]")]
    [InlineData("maui-dev", "tools: [vscode, read, todo, 'codegraph/*', 'kyber-weave/*', 'context7/*', search, execute, edit]")]
    [InlineData("product-owner", "tools: [vscode, read, todo, 'codegraph/*', 'kyber-weave/*', 'context7/*', search, web, agent]")]
    [InlineData("pulumi-dev", "tools: [vscode, read, todo, 'codegraph/*', 'kyber-weave/*', 'context7/*', search, execute, edit]")]
    [InlineData("python-dev", "tools: [vscode, read, todo, 'codegraph/*', 'kyber-weave/*', 'context7/*', search, execute, edit]")]
    [InlineData("react-dev", "tools: [vscode, read, todo, 'codegraph/*', 'kyber-weave/*', 'context7/*', search, execute, edit]")]
    [InlineData("research-agent", "tools: [vscode, read, todo, 'codegraph/*', 'kyber-weave/*', 'context7/*', search, web]")]
    [InlineData("review-lens", "tools: [vscode, read, todo, 'codegraph/*', 'kyber-weave/*', 'context7/*', search, web]")]
    [InlineData("review-triage", "tools: [vscode, read, todo, 'codegraph/*', 'kyber-weave/*', 'context7/*', search, web]")]
    [InlineData("sql-database-architect", "tools: [vscode, read, todo, 'codegraph/*', 'kyber-weave/*', 'context7/*', search, execute, edit]")]
    [InlineData("task-reviewer", "tools: [vscode, read, todo, 'codegraph/*', 'kyber-weave/*', 'context7/*', search, execute, web]")]
    [InlineData("tauri-dev", "tools: [vscode, read, todo, 'codegraph/*', 'kyber-weave/*', 'context7/*', search, execute, edit]")]
    [InlineData("test-dev", "tools: [vscode, read, todo, 'codegraph/*', 'kyber-weave/*', 'context7/*', search, execute, edit]")]
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

    [Theory]
    [InlineData("task-reviewer")]
    [InlineData("test-dev")]
    public async Task MaiCodeProfileResolvesToExactCopilotModel(string agentName)
    {
        string frontmatter = await RenderFrontmatterAsync(agentName);

        Assert.Contains(
            "model: MAI-Code-1.1-Flash (copilot)",
            frontmatter,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RenderAsyncUsesExactSyntheticSourceMembershipWithoutInferringTools()
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

        SquadDeploymentFile execAgent = Assert.Single(result.Files, f => f.RelativePath == ".github/agents/exec-bot.agent.md");
        Assert.Equal(
            "tools: [vscode, todo, execute]",
            ExtractToolsLine(ExtractFrontmatterText(Encoding.UTF8.GetString(execAgent.Content.Span))));

        SquadDeploymentFile readAgent = Assert.Single(result.Files, f => f.RelativePath == ".github/agents/read-bot.agent.md");
        Assert.Equal(
            "tools: [vscode, read, todo, 'context7/*']",
            ExtractToolsLine(ExtractFrontmatterText(Encoding.UTF8.GetString(readAgent.Content.Span))));

        SquadDeploymentFile lockedAgent = Assert.Single(result.Files, f => f.RelativePath == ".github/agents/locked-bot.agent.md");
        Assert.Equal(
            "tools: [vscode, todo]",
            ExtractToolsLine(ExtractFrontmatterText(Encoding.UTF8.GetString(lockedAgent.Content.Span))));

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

        WriteAgent(root, "exec-bot", "exec-only", "[execute, todo, vscode]");
        WriteAgent(root, "read-bot", "read-only-custom", "[context7/*, todo, read, vscode]");
        WriteAgent(root, "locked-bot", "all-denied", "[todo, vscode]");
        WriteAgent(root, "conductor", "read-only-custom", "[todo, read, vscode]");
    }

    private static void WriteAgent(
        string root,
        string name,
        string capabilityProfile,
        string copilotTools)
    {
        File.WriteAllText(Path.Combine(root, "agents", $"{name}.md"), $"""
            ---
            schema: kyber-squad.agent/v1
            name: {name}
            description: Test agent {name}
            invocation: subagent
            model-profile: default
            capability-profile: {capabilityProfile}
            copilot-tools: {copilotTools}
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

    /// <summary>
    /// The "agent" tool grants a subagent the mechanism to delegate; "agents" names who it
    /// may reach. Emitting only the first leaves architect and code-reviewer holding a tool
    /// with an empty roster — delegation that looks configured and silently does nothing.
    /// A primary agent is dispatched from the top-level session and receives the full
    /// roster from the harness, so declaring one there would only narrow it.
    /// </summary>
    [Theory]
    [InlineData("architect", "agents: ['azure-reader', 'research-agent']")]
    [InlineData("architect-v3", "agents: ['azure-reader', 'research-agent']")]
    [InlineData("code-reviewer", "agents: ['azure-reader', 'review-lens', 'review-triage']")]
    [InlineData("product-owner", "agents: ['research-agent']")]
    public async Task RenderAsync_DeclaresDelegationRosterForDelegatingSubagents(
        string agentName,
        string expectedAgentsLine)
    {
        string frontmatter = await RenderFrontmatterAsync(agentName);

        string? actual = frontmatter
            .Split('\n')
            .FirstOrDefault(l => l.StartsWith("agents:", StringComparison.Ordinal))
            ?.Trim();

        Assert.Equal(expectedAgentsLine, actual);
    }

    /// <summary>
    /// A subagent that delegates to nothing, and every primary agent, must carry no roster:
    /// an empty or redundant "agents" key is a permission statement nobody meant to make.
    /// </summary>
    [Theory]
    [InlineData("csharp-dev")]
    [InlineData("review-lens")]
    [InlineData("azure-reader")]
    [InlineData("conductor")]
    [InlineData("conductor-v3")]
    public async Task RenderAsync_OmitsDelegationRosterWhereNoneIsDeclared(string agentName)
    {
        string frontmatter = await RenderFrontmatterAsync(agentName);

        Assert.DoesNotContain(
            frontmatter.Split('\n'),
            l => l.StartsWith("agents:", StringComparison.Ordinal));
    }

    private static async Task<string> RenderFrontmatterAsync(string agentName)
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

        return ExtractFrontmatterText(Encoding.UTF8.GetString(file.Content.Span));
    }

    private static string ExtractToolsLine(string frontmatter)
    {
        string[] lines = frontmatter.Split('\n');
        string? toolsLine = lines.FirstOrDefault(l => l.StartsWith("tools:", StringComparison.Ordinal));
        Assert.NotNull(toolsLine);
        return toolsLine.Trim();
    }
}
