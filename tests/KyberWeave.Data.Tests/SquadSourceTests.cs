using KyberWeave.Testing.Squad;
using System.Text;
using System.Text.Json;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using KyberWeave.Core.Squad.Validation;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Tests;

public sealed class SquadSourceTests
{
    private const string NormalizedArchitectBody = "You are architect.\nPlan first.\n";
    private const string NormalizedArchitectBodySha256 =
        "856711e391c077cbee5211b2dfb76861163472c622bf414b5a52d6ce15e0b546";

    [Fact]
    public void LoadValidSourceLoadsDeterministically()
    {
        SquadFixture fixture = SquadFixture.CreateValid();

        SquadSource first = fixture.Load();
        SquadSource second = fixture.Load();

        Assert.Equal("kyber-squad", first.Manifest.Name);
        Assert.Equal("full", first.Bundle.Name);
        Assert.Equal(
            ["architect", "dev-medium"],
            first.Agents.Select(agent => agent.Name));
        Assert.All(
            first.Agents,
            agent => Assert.Equal(["vscode", "read"], agent.CopilotTools));
        Assert.All(first.Agents, agent => Assert.Null(agent.CopilotCapabilityProfile));
        Assert.All(first.Agents, agent => Assert.Null(agent.DevinCapabilityProfile));
        Assert.Equal(["dev-medium"], first.Skills.Select(skill => skill.Name));
        Assert.Equal(
            first.Agents.Select(agent => (agent.Name, agent.SourcePath, agent.BodyDigest)),
            second.Agents.Select(agent => (agent.Name, agent.SourcePath, agent.BodyDigest)));
        Assert.Equal(
            first.Skills.Select(skill => (skill.Name, skill.SourcePath)),
            second.Skills.Select(skill => (skill.Name, skill.SourcePath)));
    }

    /// <summary>
    /// Pi model tokens use <c>provider/modelId</c> values. This fixture writes a
    /// <c>pi:</c> value into its profile data and verifies that the closed model-profile
    /// field set accepts and exposes it.
    /// </summary>
    [Fact]
    public void LoadModelProfileWithPiHarnessModelExposesThePiModel()
    {
        SquadFixture fixture = SquadFixture.CreateValid();
        fixture.Write("profiles/models.yml", """
            schema: kyber-squad.model-profiles/v1
            profiles:
              deep-planning:
                default: inherit
                pi: provider/model
              general:
                default: inherit
            """);

        SquadSource source = fixture.Load();

        SquadModelProfile profile = source.ModelProfiles.Profiles["deep-planning"];
        Assert.Equal("inherit", profile.Default);
        Assert.True(
            profile.HarnessModels.TryGetValue("pi", out string? piModel),
            "The deep-planning profile does not expose its 'pi' harness model.");
        Assert.Equal("provider/model", piModel);
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
        SquadFixture fixture = SquadFixture.CreateValid();
        fixture.Replace(relativePath, validSchema, unsupportedSchema);

        Diagnostic diagnostic = AssertInvalid(fixture, relativePath, unsupportedSchema);

        Assert.Contains(validSchema, diagnostic.Hint!, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadAgentOmitsRequiredSchemaFieldReportsFieldAndSourceLocation()
    {
        SquadFixture fixture = SquadFixture.CreateValid();
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
        SquadFixture fixture = SquadFixture.CreateValid();
        fixture.Replace(
            "agents/architect.md",
            "aliases: []",
            "aliases: []\nharness-model: gpt-example");

        Diagnostic diagnostic = AssertInvalid(fixture, "agents/architect.md", "harness-model");

        Assert.Contains("remove", diagnostic.Hint!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoadAgentUsesUnknownCopilotToolReportsKnownCatalogAndSourceLocation()
    {
        SquadFixture fixture = SquadFixture.CreateValid();
        fixture.Replace(
            "agents/architect.md",
            "copilot-tools: [vscode, read]",
            "copilot-tools: [vscode, shell]");

        Diagnostic diagnostic = AssertInvalid(fixture, "agents/architect.md", "shell");

        Assert.Contains("known tool catalog", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("vscode", diagnostic.Hint!, StringComparison.Ordinal);
        Assert.Contains("edit/rename", diagnostic.Hint!, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadAgentRepeatsCopilotToolReportsDuplicateAndSourceLocation()
    {
        SquadFixture fixture = SquadFixture.CreateValid();
        fixture.Replace(
            "agents/architect.md",
            "copilot-tools: [vscode, read]",
            "copilot-tools: [vscode, read, read]");

        Diagnostic diagnostic = AssertInvalid(fixture, "agents/architect.md", "duplicate");

        Assert.Contains("Copilot tool", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("Remove", diagnostic.Hint!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoadAgentDeclaresNoCopilotToolsReportsRequiredMembership()
    {
        SquadFixture fixture = SquadFixture.CreateValid();
        fixture.Replace(
            "agents/architect.md",
            "copilot-tools: [vscode, read]",
            "copilot-tools: []");

        Diagnostic diagnostic = AssertInvalid(fixture, "agents/architect.md", "at least one");

        Assert.Contains("copilot-tools", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("Add", diagnostic.Hint!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ask")]
    [InlineData("deny")]
    public void LoadGovernedCopilotToolRequiresExplicitCapabilityAllow(string decision)
    {
        SquadFixture fixture = SquadFixture.CreateValid();
        fixture.Replace(
            "agents/dev-medium.md",
            "copilot-tools: [vscode, read]",
            "copilot-tools: [vscode, edit]");
        fixture.Replace(
            "profiles/capabilities.yml",
            "      filesystem.write: ask",
            $"      filesystem.write: {decision}");

        Diagnostic diagnostic = AssertInvalid(fixture, "agents/dev-medium.md", "filesystem.write");

        Assert.Contains($"'{decision}'", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("Remove 'edit'", diagnostic.Hint!, StringComparison.Ordinal);
        Assert.Contains("'allow'", diagnostic.Hint!, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadCopilotSpecificProfileAllowsExactToolsWithoutWideningSharedProfile()
    {
        SquadFixture fixture = SquadFixture.CreateValid();
        fixture.Replace(
            "profiles/capabilities.yml",
            "profiles:\n",
            "profiles:\n" +
            "  architect-copilot:\n" +
            "    target: copilot\n" +
            "    permissions:\n" +
            "      filesystem.read: allow\n" +
            "      filesystem.write: allow\n" +
            "      delegate: ask\n");
        fixture.Replace(
            "agents/architect.md",
            "capability-profile: architect\n" +
            "copilot-tools: [vscode, read]",
            "capability-profile: architect\n" +
            "copilot-capability-profile: architect-copilot\n" +
            "copilot-tools: [vscode, read, edit]");

        SquadSource source = fixture.Load();
        SquadAgent architect = Assert.Single(source.Agents, agent => agent.Name == "architect");

        Assert.Equal("architect", architect.CapabilityProfile);
        Assert.Equal("architect-copilot", architect.CopilotCapabilityProfile);
        Assert.Equal("copilot", source.CapabilityProfiles.Profiles["architect-copilot"].Target);
        Assert.Equal(SquadPermissionDecision.Deny, source.CapabilityProfiles.Profiles["architect"].Permissions["filesystem.write"]);
        Assert.Equal(SquadPermissionDecision.Allow, source.CapabilityProfiles.Profiles["architect-copilot"].Permissions["filesystem.write"]);
    }

    [Fact]
    public void LoadCopilotSpecificProfileAsSharedProfileFailsClosed()
    {
        SquadFixture fixture = SquadFixture.CreateValid();
        fixture.Replace(
            "profiles/capabilities.yml",
            "profiles:\n",
            "profiles:\n" +
            "  architect-copilot:\n" +
            "    target: copilot\n" +
            "    permissions:\n" +
            "      filesystem.read: allow\n" +
            "      filesystem.write: allow\n" +
            "      delegate: ask\n");
        fixture.Replace(
            "agents/architect.md",
            "capability-profile: architect\n" +
            "copilot-tools: [vscode, read]",
            "capability-profile: architect\n" +
            "copilot-capability-profile: architect-copilot\n" +
            "copilot-tools: [vscode, read, edit]");
        fixture.Replace(
            "agents/dev-medium.md",
            "capability-profile: worker",
            "capability-profile: architect-copilot");

        Diagnostic diagnostic = AssertInvalid(fixture, "agents/dev-medium.md", "Copilot-only");

        Assert.Contains("shared capability profile", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("copilot-capability-profile", diagnostic.Hint!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Plan D7/D8: the optional flat <c>pi-extensions</c> / <c>pi-tools</c> frontmatter keys
    /// are accepted by the closed agent schema, and the parsed <c>SquadAgent.PiExtensions</c>
    /// / <c>PiTools</c> values round-trip exactly (not just <c>CopilotTools</c>).
    /// </summary>
    [Fact]
    public void LoadAgentWithValidPiOverlayLoadsSuccessfully()
    {
        SquadFixture fixture = SquadFixture.CreateValid();
        fixture.Replace(
            "agents/architect.md",
            "copilot-tools: [vscode, read]",
            "copilot-tools: [vscode, read]\n" +
            "pi-extensions: [pi-mcp-adapter]\n" +
            "pi-tools: [ext:pi-mcp-adapter/mcp, ext:pi-mcp-adapter/mcp__context7__resolve_library_id]");

        SquadSource source = fixture.Load();
        SquadAgent architect = Assert.Single(source.Agents, agent => agent.Name == "architect");

        Assert.Equal(["vscode", "read"], architect.CopilotTools);
        Assert.Equal(["pi-mcp-adapter"], architect.PiExtensions);
        Assert.Equal(
            ["ext:pi-mcp-adapter/mcp", "ext:pi-mcp-adapter/mcp__context7__resolve_library_id"],
            architect.PiTools);
    }

    /// <summary>
    /// Plan D8: each <c>ext:</c> entry must name a non-empty extension and a non-empty
    /// tool around the <c>/</c> separator — a missing separator, an empty extension part,
    /// or an empty tool part fails closed at load time per the loader's well-formed rule.
    /// </summary>
    [Theory]
    [InlineData("ext:pi-mcp-adapter")]
    [InlineData("ext:/mcp")]
    [InlineData("ext:pi-mcp-adapter/")]
    public void LoadAgentWithMalformedPiToolReferenceRejectsWellFormedRule(string piTool)
    {
        SquadFixture fixture = SquadFixture.CreateValid();
        fixture.Replace(
            "agents/architect.md",
            "copilot-tools: [vscode, read]",
            "copilot-tools: [vscode, read]\n" +
            "pi-extensions: [pi-mcp-adapter]\n" +
            $"pi-tools: [{piTool}]");

        Diagnostic diagnostic = AssertInvalid(fixture, "agents/architect.md", "well-formed");

        Assert.Contains("pi-tools", diagnostic.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Plan D8: <c>pi-tools</c> entries are restricted to the <c>ext:</c> form — a bare
    /// built-in name would bypass the capability lattice (e.g. smuggling <c>bash</c> past
    /// <c>process.execute: deny</c>), so it fails closed.
    /// </summary>
    [Fact]
    public void LoadAgentWithBarePiToolNameRejectsExtForm()
    {
        SquadFixture fixture = SquadFixture.CreateValid();
        fixture.Replace(
            "agents/architect.md",
            "copilot-tools: [vscode, read]",
            "copilot-tools: [vscode, read]\n" +
            "pi-extensions: [pi-mcp-adapter]\n" +
            "pi-tools: [bash]");

        Diagnostic diagnostic = AssertInvalid(fixture, "agents/architect.md", "ext:");

        Assert.Contains("pi-tools", diagnostic.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Plan D8: every <c>ext:</c> entry's extension must be declared in <c>pi-extensions</c>
    /// (cross-check modeled on <c>ValidateCopilotToolCapabilities</c>).
    /// </summary>
    [Fact]
    public void LoadAgentWithPiToolExtensionNotDeclaredRejectsCrossCheck()
    {
        SquadFixture fixture = SquadFixture.CreateValid();
        fixture.Replace(
            "agents/architect.md",
            "copilot-tools: [vscode, read]",
            "copilot-tools: [vscode, read]\n" +
            "pi-extensions: [pi-mcp-adapter]\n" +
            "pi-tools: [ext:other-adapter/mcp]");

        Diagnostic diagnostic = AssertInvalid(fixture, "agents/architect.md", "declared in");

        Assert.Contains("pi-extensions", diagnostic.Hint!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Plan D8: both keys are optional, but when present they must be non-empty.
    /// </summary>
    [Fact]
    public void LoadAgentWithEmptyPiExtensionsRejectsRequiredMembership()
    {
        SquadFixture fixture = SquadFixture.CreateValid();
        fixture.Replace(
            "agents/architect.md",
            "copilot-tools: [vscode, read]",
            "copilot-tools: [vscode, read]\n" +
            "pi-extensions: []\n" +
            "pi-tools: [ext:pi-mcp-adapter/mcp]");

        Diagnostic diagnostic = AssertInvalid(fixture, "agents/architect.md", "at least one");

        Assert.Contains("pi-extensions", diagnostic.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Plan D8: duplicate <c>pi-tools</c> entries fail closed, like every other
    /// distinct-list field the loader enforces.
    /// </summary>
    [Fact]
    public void LoadAgentWithDuplicatePiToolsRejectsDuplicate()
    {
        SquadFixture fixture = SquadFixture.CreateValid();
        fixture.Replace(
            "agents/architect.md",
            "copilot-tools: [vscode, read]",
            "copilot-tools: [vscode, read]\n" +
            "pi-extensions: [pi-mcp-adapter]\n" +
            "pi-tools: [ext:pi-mcp-adapter/mcp, ext:pi-mcp-adapter/mcp]");

        Diagnostic diagnostic = AssertInvalid(fixture, "agents/architect.md", "duplicate");

        Assert.Contains("Remove", diagnostic.Hint!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Plan D8: extension ids use the portable charset (no <c>/</c> — it is the
    /// <c>ext:</c> separator) and the tool part reuses the loader's portable MCP-name
    /// charset.
    /// </summary>
    [Theory]
    [InlineData("pi/mcp-adapter")]
    [InlineData("Pi-Adapter!")]
    public void LoadAgentWithNonPortablePiExtensionRejectsCharset(string extension)
    {
        SquadFixture fixture = SquadFixture.CreateValid();
        fixture.Replace(
            "agents/architect.md",
            "copilot-tools: [vscode, read]",
            "copilot-tools: [vscode, read]\n" +
            $"pi-extensions: [{extension}]\n" +
            $"pi-tools: [ext:{extension}/mcp]");

        Diagnostic diagnostic = AssertInvalid(fixture, "agents/architect.md", "portable");

        Assert.Contains("pi-extensions", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadAgentReferencesUnknownCopilotCapabilityProfileFailsClosed()
    {
        SquadFixture fixture = SquadFixture.CreateValid();
        fixture.Replace(
            "agents/architect.md",
            "capability-profile: architect\n",
            "capability-profile: architect\n" +
            "copilot-capability-profile: missing-copilot\n");

        Diagnostic diagnostic = AssertInvalid(fixture, "agents/architect.md", "missing-copilot");

        Assert.Contains("unknown Copilot capability profile", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("profiles/capabilities.yml", diagnostic.Hint!, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadCopilotOverrideReferencesSharedProfileFailsClosed()
    {
        SquadFixture fixture = SquadFixture.CreateValid();
        fixture.Replace(
            "agents/architect.md",
            "capability-profile: architect\n",
            "capability-profile: architect\n" +
            "copilot-capability-profile: architect\n");

        Diagnostic diagnostic = AssertInvalid(fixture, "agents/architect.md", "not marked as Copilot-only");

        Assert.Contains("target: copilot", diagnostic.Hint!, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadAgentWithDevinSpecificCapabilityProfileSucceeds()
    {
        SquadFixture fixture = SquadFixture.CreateValid();
        fixture.Replace(
            "profiles/capabilities.yml",
            "profiles:\n",
            "profiles:\n" +
            "  architect-devin:\n" +
            "    target: devin\n" +
            "    permissions:\n" +
            "      filesystem.read: allow\n" +
            "      filesystem.write: allow\n" +
            "      delegate: ask\n");
        fixture.Replace(
            "agents/architect.md",
            "capability-profile: architect\n",
            "capability-profile: architect\n" +
            "devin-capability-profile: architect-devin\n");

        SquadSource source = fixture.Load();
        SquadAgent architect = Assert.Single(source.Agents, agent => agent.Name == "architect");

        Assert.Equal("architect", architect.CapabilityProfile);
        Assert.Equal("architect-devin", architect.DevinCapabilityProfile);
        Assert.Equal("devin", source.CapabilityProfiles.Profiles["architect-devin"].Target);
        Assert.Equal(SquadPermissionDecision.Deny, source.CapabilityProfiles.Profiles["architect"].Permissions["filesystem.write"]);
        Assert.Equal(SquadPermissionDecision.Allow, source.CapabilityProfiles.Profiles["architect-devin"].Permissions["filesystem.write"]);
    }

    [Fact]
    public void LoadDevinSpecificProfileAsSharedProfileFailsClosed()
    {
        SquadFixture fixture = SquadFixture.CreateValid();
        fixture.Replace(
            "profiles/capabilities.yml",
            "profiles:\n",
            "profiles:\n" +
            "  architect-devin:\n" +
            "    target: devin\n" +
            "    permissions:\n" +
            "      filesystem.read: allow\n" +
            "      filesystem.write: allow\n" +
            "      delegate: ask\n");
        fixture.Replace(
            "agents/architect.md",
            "capability-profile: architect\n",
            "capability-profile: architect\n" +
            "devin-capability-profile: architect-devin\n");
        fixture.Replace(
            "agents/dev-medium.md",
            "capability-profile: worker",
            "capability-profile: architect-devin");

        Diagnostic diagnostic = AssertInvalid(fixture, "agents/dev-medium.md", "Devin-only");

        Assert.Contains("shared capability profile", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("devin-capability-profile", diagnostic.Hint!, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadAgentReferencesUnknownDevinCapabilityProfileFailsClosed()
    {
        SquadFixture fixture = SquadFixture.CreateValid();
        fixture.Replace(
            "agents/architect.md",
            "capability-profile: architect\n",
            "capability-profile: architect\n" +
            "devin-capability-profile: missing-devin\n");

        Diagnostic diagnostic = AssertInvalid(fixture, "agents/architect.md", "missing-devin");

        Assert.Contains("unknown Devin capability profile", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("profiles/capabilities.yml", diagnostic.Hint!, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadDevinOverrideReferencesSharedProfileFailsClosed()
    {
        SquadFixture fixture = SquadFixture.CreateValid();
        fixture.Replace(
            "agents/architect.md",
            "capability-profile: architect\n",
            "capability-profile: architect\n" +
            "devin-capability-profile: architect\n");

        Diagnostic diagnostic = AssertInvalid(fixture, "agents/architect.md", "not marked as Devin-only");

        Assert.Contains("target: devin", diagnostic.Hint!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A typo in <c>target:</c> on a capability profile must fail at load, not
    /// become an unmarked shared profile that later evaporates at render.
    /// </summary>
    [Fact]
    public void LoadCapabilityProfileWithUnknownTargetFailsClosed()
    {
        SquadFixture fixture = SquadFixture.CreateValid();
        fixture.Replace(
            "profiles/capabilities.yml",
            "profiles:\n",
            "profiles:\n" +
            "  mistyped-devin:\n" +
            "    target: devni\n" +
            "    permissions:\n" +
            "      filesystem.read: allow\n" +
            "      filesystem.write: allow\n" +
            "      delegate: ask\n");

        Diagnostic diagnostic = AssertInvalid(fixture, "profiles/capabilities.yml", "devni");

        Assert.Contains("unsupported target", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("copilot", diagnostic.Hint!, StringComparison.Ordinal);
        Assert.Contains("devin", diagnostic.Hint!, StringComparison.Ordinal);
        Assert.Contains("profiles.mistyped-devin.target", diagnostic.Subject, StringComparison.Ordinal);
    }

    /// <summary>
    /// Devin lowers primary identities to skills and never consults
    /// <c>devin-capability-profile</c> for their tools, so a named override on a
    /// primary agent would validate and then evaporate.
    /// </summary>
    [Fact]
    public void LoadDevinOverrideOnPrimaryAgentFailsClosed()
    {
        SquadFixture fixture = SquadFixture.CreateValid();
        fixture.Replace(
            "profiles/capabilities.yml",
            "profiles:\n",
            "profiles:\n" +
            "  architect-devin:\n" +
            "    target: devin\n" +
            "    permissions:\n" +
            "      filesystem.read: allow\n" +
            "      filesystem.write: allow\n" +
            "      delegate: ask\n");
        fixture.Replace(
            "agents/dev-medium.md",
            "invocation: subagent\n",
            "invocation: primary\n");
        fixture.Replace(
            "agents/dev-medium.md",
            "capability-profile: worker\n",
            "capability-profile: worker\n" +
            "devin-capability-profile: architect-devin\n");

        Diagnostic diagnostic = AssertInvalid(fixture, "agents/dev-medium.md", "primary agent");

        Assert.Contains("primary agent", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("skill", diagnostic.Hint!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("devin-capability-profile", diagnostic.Hint!, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadVersionSourceIsNotAssemblyReportsRequiredLiteralAtManifestLocation()
    {
        SquadFixture fixture = SquadFixture.CreateValid();
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
        SquadFixture fixture = SquadFixture.CreateValid();
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

    /// <summary>
    /// A model profile is required: the model comes from the agent, so an agent without one would
    /// render a worker whose model nobody chose. The conductor is the single exception, and the error
    /// says so.
    /// </summary>
    [Fact]
    public void LoadAgentWithNoModelProfileFailsUnlessItIsTheConductor()
    {
        SquadFixture fixture = SquadFixture.CreateValid();
        fixture.Replace("agents/architect.md", "model-profile: deep-planning\n", string.Empty);

        Diagnostic diagnostic = AssertInvalid(fixture, "agents/architect.md", "architect");

        Assert.Contains("model-profile", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("conductor", diagnostic.Hint!, StringComparison.Ordinal);
        Assert.Contains("profiles/models.yml", diagnostic.Hint!, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadConductorWithNoModelProfileIsValid()
    {
        SquadFixture fixture = SquadFixture.CreateValid();
        fixture.Write("agents/conductor.md", """
            ---
            schema: kyber-squad.agent/v1
            name: conductor
            description: Routes work to the agents a run may dispatch.
            invocation: primary
            capability-profile: architect
            copilot-tools: [vscode, read]
            delegates-to: []
            fallback: role-skill
            aliases: []
            ---
            You are conductor.
            """);

        SquadSource source = fixture.Load();

        Assert.True(string.IsNullOrWhiteSpace(
            source.Agents.Single(agent => agent.Name == "conductor").ModelProfile));
    }

    [Fact]
    public void LoadProfileUsesUnknownCapabilityReportsCapabilityProfileLocation()
    {
        SquadFixture fixture = SquadFixture.CreateValid();
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
        SquadFixture fixture = SquadFixture.CreateValid();
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
        SquadFixture fixture = SquadFixture.CreateValid();
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
        SquadFixture fixture = SquadFixture.CreateValid();
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
        SquadFixture fixture = SquadFixture.CreateValid();
        fixture.Files.DeleteFile(fixture.AbsolutePath("schemas/fallback-profiles.schema.json"));

        Diagnostic diagnostic = AssertInvalid(
            fixture,
            "schemas/fallback-profiles.schema.json",
            "exists");

        Assert.Contains("schema", diagnostic.Hint!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoadMalformedFallbackProfilesSchemaReportsJsonAndSchemaLocation()
    {
        SquadFixture fixture = SquadFixture.CreateValid();
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
        SquadFixture fixture = SquadFixture.CreateValid();
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
        SquadFixture fixture = SquadFixture.CreateValid();
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
        SquadFixture fixture = SquadFixture.CreateValid();
        fixture.Replace("squad.yml", "profiles/models.yml", unsafePath);

        Diagnostic diagnostic = AssertInvalid(fixture, "squad.yml", unsafePath);

        Assert.Contains("inside", diagnostic.Hint!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoadReferencedSymlinkEscapesProductRootRejectsResolvedTarget()
    {
        SquadFixture fixture = SquadFixture.CreateValid();
        string outsideAgent = System.IO.Path.Combine(fixture.Files.Root, "outside-source", "architect.md");
        fixture.Files.CreateDirectory(System.IO.Path.GetDirectoryName(outsideAgent)!);
        fixture.Files.WriteAllText(outsideAgent, SquadFixture.ArchitectAgentLf);
        string linkedAgent = fixture.AbsolutePath("agents/architect.md");
        fixture.Files.DeleteFile(linkedAgent);
        fixture.Files.CreateFileLink(linkedAgent, outsideAgent);

        Diagnostic diagnostic = AssertInvalid(fixture, "agents/architect.md", "symbolic link");

        Assert.Contains("inside", diagnostic.Hint!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoadAgentIsNotValidUtf8ReportsEncodingAndSourceLocation()
    {
        SquadFixture fixture = SquadFixture.CreateValid();
        fixture.Files.WriteAllBytes(fixture.AbsolutePath("agents/architect.md"), [0xc3, 0x28]);

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
        SquadFixture fixture = SquadFixture.CreateValid();
        string anchor = identityKind == "agent" ? "  - dev-medium" : "  - dev-medium";
        fixture.Replace("bundles/full.yml", anchor, $"{anchor}\n  - {missingIdentity}");

        Diagnostic diagnostic = AssertInvalid(fixture, "bundles/full.yml", missingIdentity);

        Assert.Contains(identityKind, diagnostic.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("agent", "agents/reviewer.md", "architect")]
    [InlineData("skill", "skills/reviewer/SKILL.md", "dev-medium")]
    public void LoadDuplicateIdentityReportsBothSourceLocations(
        string identityKind,
        string duplicateRelativePath,
        string duplicateName)
    {
        SquadFixture fixture = SquadFixture.CreateValid();
        if (identityKind == "agent")
        {
            fixture.Write(
                duplicateRelativePath,
                SquadFixture.Agent("reviewer", name: duplicateName, aliases: "[]"));
            fixture.Replace("bundles/full.yml", "  - dev-medium", "  - dev-medium\n  - reviewer");
        }
        else
        {
            fixture.Write(
                duplicateRelativePath,
                SquadFixture.Skill("dev-medium", "A second path with the same identity."));
            fixture.Replace("bundles/full.yml", "  - dev-medium", "  - dev-medium\n  - reviewer");
        }

        SquadSourceValidationException exception = Assert.Throws<SquadSourceValidationException>(
            () => fixture.Load());
        Diagnostic diagnostic = Assert.Single(
            exception.Diagnostics.Items,
            item => item.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) &&
                    item.Message.Contains(duplicateName, StringComparison.Ordinal));

        Assert.Equal(duplicateRelativePath, diagnostic.FilePath);
        Assert.NotEmpty(diagnostic.RelatedLocations);
        Assert.All(
            diagnostic.RelatedLocations,
            location => Assert.False(Path.IsPathRooted(location.FilePath)));
        Assert.False(string.IsNullOrWhiteSpace(diagnostic.Hint));
    }

    [Theory]
    [InlineData("architect")]
    [InlineData("shared-alias")]
    public void LoadAliasCollidesWithCanonicalOrExistingAliasReportsDeclaringAgent(string alias)
    {
        SquadFixture fixture = SquadFixture.CreateValid();
        fixture.Write(
            "agents/dev-medium.md",
            SquadFixture.Agent("dev-medium", aliases: $"[{alias}]"));
        if (alias == "shared-alias")
        {
            fixture.Write(
                "agents/architect.md",
                SquadFixture.Agent("architect", aliases: "[shared-alias]"));
        }

        Diagnostic diagnostic = AssertInvalid(fixture, "agents/dev-medium.md", alias);

        Assert.Contains("alias", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoadAliasReferenceInBundleRejectsNonCanonicalIdentity()
    {
        SquadFixture fixture = SquadFixture.CreateValid();
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
    [InlineData("skill", "skills/dev-medium/SKILL.md", "role-test-dev")]
    public void LoadIdentityUsesReservedFallbackPrefixReportsDeclaringSourceAndFallbackProfile(
        string identityKind,
        string expectedSourcePath,
        string reservedIdentity)
    {
        SquadFixture fixture = SquadFixture.CreateValid();
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
                fixture.Replace(
                    "bundles/full.yml",
                    "skills:\n  - dev-medium",
                    $"skills:\n  - {reservedIdentity}");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(identityKind));
        }

        SquadSourceValidationException exception = Assert.Throws<SquadSourceValidationException>(
            () => fixture.Load());
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

    /// <summary>
    /// The retired roster is not a selectable value. An agent that declares it is told the one roster that ships,
    /// not pointed back at the value the removal deleted.
    /// </summary>
    [Theory]
    [InlineData("technology", "deleted")]
    [InlineData("side-by-side", "not a roster")]
    public void AnAgentDeclaringARosterThisBuildDoesNotHaveIsToldTheOnlyRoster(
        string declared,
        string expectedMessageFragment)
    {
        SquadFixture fixture = SquadFixture.CreateValid();
        fixture.Replace(
            "agents/architect.md",
            "aliases: []\n",
            $"aliases: []\nworker-roster: {declared}\n");

        Diagnostic diagnostic = AssertInvalid(fixture, "agents/architect.md", expectedMessageFragment);

        Assert.Contains("tiered", diagnostic.Hint!, StringComparison.Ordinal);
        Assert.DoesNotContain("both rosters", diagnostic.Hint!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("'technology' or", diagnostic.Hint!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoadIdentitiesDifferingOnlyByReservedPrefixCaseRemainValid()
    {
        SquadFixture fixture = SquadFixture.CreateValid();
        fixture.Write(
            "agents/architect.md",
            SquadFixture.Agent("architect", name: "Role-architect", aliases: "[Role-planner]"));
        fixture.Write(
            "skills/dev-medium/SKILL.md",
            SquadFixture.Skill("Role-test-dev", "Use when writing tests."));
        fixture.Replace("bundles/full.yml", "  - architect", "  - Role-architect");
        fixture.Replace("bundles/full.yml", "skills:\n  - dev-medium", "skills:\n  - Role-test-dev");

        SquadSource source = fixture.Load();

        SquadAgent agent = Assert.Single(source.Agents, item => item.Name == "Role-architect");
        Assert.Equal(["Role-planner"], agent.Aliases);
        Assert.Contains(source.Skills, item => item.Name == "Role-test-dev");
    }

    [Fact]
    public void LoadLfAndUtf8BomVariantsProduceSameNormalizedBodyAndStableSha256()
    {
        SquadFixture lfFixture = SquadFixture.CreateValid();
        SquadFixture crlfFixture = SquadFixture.CreateValid();
        crlfFixture.Write(
            "agents/architect.md",
            SquadFixture.ArchitectAgentLf.Replace("\n", "\r\n", StringComparison.Ordinal),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        SquadAgent lfAgent = lfFixture.Load().Agents.Single(agent => agent.Name == "architect");
        SquadAgent crlfAgent = crlfFixture.Load().Agents.Single(agent => agent.Name == "architect");

        Assert.Equal(NormalizedArchitectBody, lfAgent.InstructionBody);
        Assert.Equal(NormalizedArchitectBody, crlfAgent.InstructionBody);
        Assert.Equal(NormalizedArchitectBodySha256, lfAgent.BodyDigest);
        Assert.Equal(lfAgent.BodyDigest, crlfAgent.BodyDigest);
    }

    [Fact]
    public void LoadAgentWithUnknownFrontmatterFieldReportsMatchingMarkdownLineNumber()
    {
        SquadFixture fixture = SquadFixture.CreateValid();
        string invalidAgent =
            "---\n" +
            "schema: kyber-squad.agent/v1\n" +
            "name: architect\n" +
            "unknown-field: value\n" +
            "description: Use when planning.\n" +
            "invocation: subagent\n" +
            "model-profile: deep-planning\n" +
            "capability-profile: architect\n" +
            "copilot-tools: [vscode, read]\n" +
            "delegates-to: [dev-medium]\n" +
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
        SquadFixture fixture = SquadFixture.CreateValid();
        string invalidSkill =
            "---\n" +
            "name: dev-medium\n" +
            "unexpected: field\n" +
            "description: Use when writing tests.\n" +
            "license: MIT\n" +
            "---\n" +
            "# dev-medium\n";

        fixture.Write("skills/dev-medium/SKILL.md", invalidSkill);

        Diagnostic diagnostic = AssertInvalid(fixture, "skills/dev-medium/SKILL.md", "unexpected");
        Assert.Equal(3, diagnostic.StartLine);
    }

    [Fact]
    public void LoadAgentWithInvalidYamlSyntaxReportsMatchingMarkdownLineNumber()
    {
        SquadFixture fixture = SquadFixture.CreateValid();
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

    // ---------------------------------------------------------------------------------------
    // required-mcp-tools: the roster ZCodeRenderer grants by fully qualified name and
    // `squad doctor` checks a ZCode install against. It lives in source because it is an
    // external contract that drifts — context7 renamed `get-library-docs` to `query-docs` —
    // and a harness matching an allow-list by exact name breaks on a rename rather than
    // degrading. Names are validated at load so one that could never resolve fails here.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void LoadToolchainWithoutRequiredMcpToolsYieldsAnEmptyRoster()
    {
        SquadFixture fixture = SquadFixture.CreateValid();

        SquadSource source = fixture.Load();

        Assert.Empty(source.Toolchain.RequiredMcpTools);
    }

    [Fact]
    public void LoadToolchainReadsRequiredMcpToolsInStableOrder()
    {
        SquadFixture fixture = SquadFixture.CreateValid();
        fixture.Write("toolchain.yml", """
            schema: kyber-squad.toolchain/v1
            required-features: []
            required-mcp-tools:
              kyber-weave:
                - docs_explore
                - docs_glossary
              codegraph:
                - codegraph_explore
            validated-release: null
            """);

        SquadSource source = fixture.Load();

        Assert.Equal(["codegraph", "kyber-weave"], source.Toolchain.RequiredMcpTools.Keys);
        Assert.Equal(["codegraph_explore"], source.Toolchain.RequiredMcpTools["codegraph"]);
        Assert.Equal(
            ["docs_explore", "docs_glossary"],
            source.Toolchain.RequiredMcpTools["kyber-weave"]);
    }

    [Fact]
    public void LoadToolchainRejectsAServerDeclaringNoTools()
    {
        SquadFixture fixture = SquadFixture.CreateValid();
        fixture.Write("toolchain.yml", """
            schema: kyber-squad.toolchain/v1
            required-features: []
            required-mcp-tools:
              codegraph: []
            validated-release: null
            """);

        AssertInvalid(fixture, "toolchain.yml", "declares no tools");
    }

    [Fact]
    public void LoadToolchainRejectsDuplicateToolNames()
    {
        SquadFixture fixture = SquadFixture.CreateValid();
        fixture.Write("toolchain.yml", """
            schema: kyber-squad.toolchain/v1
            required-features: []
            required-mcp-tools:
              codegraph:
                - codegraph_explore
                - codegraph_explore
            validated-release: null
            """);

        AssertInvalid(fixture, "toolchain.yml", "duplicate");
    }

    [Theory]
    [InlineData("code graph", "codegraph_explore")]
    [InlineData("code:graph", "codegraph_explore")]
    [InlineData("codegraph", "explore tool")]
    [InlineData("codegraph", "explore/tool")]
    public void LoadToolchainRejectsNamesAHarnessWouldRewrite(string server, string tool)
    {
        // toModelVisibleMcpNamePart replaces anything outside [A-Za-z0-9_-], so a name carrying
        // another character would be rewritten and an exact-match allow-list would never match.
        SquadFixture fixture = SquadFixture.CreateValid();
        fixture.Write("toolchain.yml", $"""
            schema: kyber-squad.toolchain/v1
            required-features: []
            required-mcp-tools:
              "{server}":
                - "{tool}"
            validated-release: null
            """);

        AssertInvalid(fixture, "toolchain.yml", "qualified MCP tool name");
    }

    [Fact]
    public void LoadToolchainRejectsAnUnknownField()
    {
        SquadFixture fixture = SquadFixture.CreateValid();
        fixture.Write("toolchain.yml", """
            schema: kyber-squad.toolchain/v1
            required-features: []
            required-mcp-servers: []
            validated-release: null
            """);

        AssertInvalid(fixture, "toolchain.yml", "required-mcp-servers");
    }

    private static Diagnostic AssertInvalid(
        SquadFixture fixture,
        string expectedRelativePath,
        string expectedMessageFragment)
    {
        SquadSourceValidationException exception = Assert.Throws<SquadSourceValidationException>(
            () => fixture.Load());
        Diagnostic diagnostic = Assert.Single(
            exception.Diagnostics.Items,
            item => string.Equals(item.FilePath, expectedRelativePath, StringComparison.Ordinal) &&
                    (item.Message.Contains(expectedMessageFragment, StringComparison.OrdinalIgnoreCase) ||
                     (item.Hint?.Contains(expectedMessageFragment, StringComparison.OrdinalIgnoreCase) ?? false)));

        Assert.Equal(Severity.Error, diagnostic.Severity);
        Assert.False(Path.IsPathRooted(diagnostic.FilePath!));
        Assert.False(string.IsNullOrWhiteSpace(diagnostic.Hint));
        return diagnostic;
    }

    private sealed class SquadFixture
    {
        internal const string ArchitectAgentLf =
            "---\n" +
            "schema: kyber-squad.agent/v1\n" +
            "name: architect\n" +
            "description: Use when planning multi-domain work.\n" +
            "invocation: subagent\n" +
            "model-profile: deep-planning\n" +
            "capability-profile: architect\n" +
            "copilot-tools: [vscode, read]\n" +
            "delegates-to: [dev-medium]\n" +
            "fallback: role-skill\n" +
            "aliases: []\n" +
            "---\n" +
            NormalizedArchitectBody;

        private readonly InMemorySquadServices _services;

        private SquadFixture()
        {
            Files = new InMemoryFileSystem();
            _services = new InMemorySquadServices(Files);
            Path = System.IO.Path.Combine(Files.Root, "squad-source");
            Files.CreateDirectory(Path);
        }

        public InMemoryFileSystem Files { get; }

        public string Path { get; }

        public SquadSource Load() => _services.Load(Path);

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
                  - dev-medium
                skills:
                  - dev-medium
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
                      "args": ["--repo-root", "."]
                    }
                  }
                }
                """);
            fixture.Write("agents/architect.md", ArchitectAgentLf);
            fixture.Write("agents/dev-medium.md", Agent("dev-medium"));
            fixture.Write("skills/dev-medium/SKILL.md", Skill("dev-medium", "Use when writing tests."));

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
            "copilot-tools: [vscode, read]\n" +
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
            string content = Files.ReadAllText(path);
            Assert.Contains(oldValue, content, StringComparison.Ordinal);
            Files.WriteAllText(path, content.Replace(oldValue, newValue, StringComparison.Ordinal));
        }

        public void Write(string relativePath, string content) =>
            Write(relativePath, content, new UTF8Encoding(false));

        public void Write(string relativePath, string content, Encoding encoding)
        {
            string path = AbsolutePath(relativePath);
            Files.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            Files.WriteAllBytes(path, [.. encoding.GetPreamble(), .. encoding.GetBytes(content)]);
        }
    }
}
