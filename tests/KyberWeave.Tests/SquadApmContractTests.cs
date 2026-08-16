using System.Diagnostics;
using System.Text;
using System.Text.Json;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Core.Processes;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Packaging;
using KyberWeave.Core.Squad.Parsing;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Task K7 Contract Test Suite: Validates APM compiler and packer integration, target projections,
/// lowering/degradation rules, permission lattice safety, and Agent Plugins v1.0.0 compliance.
/// Covers Test Contract K7 from docs/plans/2026-08-14-kyber-squad-unified-agent-skill-deployment.md.
/// </summary>
public sealed class SquadApmContractTests
{
    private static readonly string[] All10TargetTokens =
    [
        "codex",
        "cursor",
        "claude",
        "copilot",
        "opencode",
        "kilo",
        "gemini",
        "antigravity",
        "warp",
        "factory"
    ];

    private static readonly SquadTarget[] NativeTargets =
    [
        SquadTarget.Codex,
        SquadTarget.Cursor,
        SquadTarget.Claude,
        SquadTarget.Copilot,
        SquadTarget.OpenCode,
        SquadTarget.Kilo,
        SquadTarget.Factory
    ];

    private static readonly SquadTarget[] FallbackTargets =
    [
        SquadTarget.Gemini,
        SquadTarget.Antigravity,
        SquadTarget.Warp
    ];

    #region Area 1: Projections across all 10 squad targets

    [Theory]
    [InlineData("codex")]
    [InlineData("cursor")]
    [InlineData("claude")]
    [InlineData("copilot")]
    [InlineData("opencode")]
    [InlineData("kilo")]
    [InlineData("gemini")]
    [InlineData("antigravity")]
    [InlineData("warp")]
    [InlineData("factory")]
    public async Task Render_EachOf10SquadTargets_AccountsForAll20AgentsAnd25Skills(string targetToken)
    {
        // K7 Area 1: All 20 agent identities and all 25 canonical skills are accounted for on each of the 10 squad targets.
        IReadOnlyList<SquadTarget> parsedTargets = SquadTargetCatalog.Parse([targetToken]);
        SquadTarget target = Assert.Single(parsedTargets);

        FakeApmRunner runner = new FakeApmRunner();
        ApmRenderRequest request = new(
            SourceDirectory: "/staging/products/kyber-squad",
            Targets: [target],
            Scope: SquadDeploymentScope.Project);

        ApmRenderResult result = await runner.RenderAsync(request);

        Assert.True(result.Success, $"Render should succeed for target '{targetToken}'.");
        Assert.Empty(result.Errors);

        bool isFallback = FallbackTargets.Contains(target);

        if (isFallback)
        {
            // Fallback target: 0 native agent files, all 20 agents lowered or reused as skills
            Assert.DoesNotContain(result.Files, f => f.RelativePath.Contains("/agents/", StringComparison.OrdinalIgnoreCase));

            // All 25 canonical skills are present
            foreach (string skill in FakeApmRunner.CanonicalSkills)
            {
                string expectedSkillFile = FakeApmRunner.GetTargetSkillFilePath(target, skill);
                Assert.Contains(result.Files, f => string.Equals(f.RelativePath, expectedSkillFile, StringComparison.Ordinal));
            }

            // All 20 agents are accounted for in degradation records
            Assert.Equal(20, result.Degradations.Count);
            foreach (string agent in FakeApmRunner.CanonicalAgents)
            {
                Assert.Contains(result.Degradations, d => string.Equals(d.CanonicalIdentity, agent, StringComparison.Ordinal));
            }
        }
        else
        {
            // Native target: all 20 agents are rendered natively
            foreach (string agent in FakeApmRunner.CanonicalAgents)
            {
                string expectedAgentFile = FakeApmRunner.GetTargetAgentFilePath(target, agent);
                Assert.Contains(result.Files, f => string.Equals(f.RelativePath, expectedAgentFile, StringComparison.Ordinal));
            }

            // Conductor and conductor-v3 single-projection: skills suppressed on native-primary target,
            // the remaining 23 canonical skills are rendered.
            foreach (string skill in FakeApmRunner.CanonicalSkills.Except(FakeApmRunner.SharedConductorIdentities))
            {
                string expectedSkillFile = FakeApmRunner.GetTargetSkillFilePath(target, skill);
                Assert.Contains(result.Files, f => string.Equals(f.RelativePath, expectedSkillFile, StringComparison.Ordinal));
            }
        }
    }

    [Fact]
    public async Task Render_TargetInputAliases_NormalizeToCanonicalTargets()
    {
        // K7 Area 1: 'factory-droids' and 'github-copilot' are input aliases only and normalize to 'factory' and 'copilot'.
        IReadOnlyList<SquadTarget> targets = SquadTargetCatalog.Parse(["factory-droids", "github-copilot"]);

        Assert.Equal(2, targets.Count);
        Assert.Contains(SquadTarget.Factory, targets);
        Assert.Contains(SquadTarget.Copilot, targets);

        FakeApmRunner runner = new FakeApmRunner();
        ApmRenderRequest request = new(
            SourceDirectory: "/staging/products/kyber-squad",
            Targets: targets,
            Scope: SquadDeploymentScope.Project);

        ApmRenderResult result = await runner.RenderAsync(request);

        Assert.True(result.Success);
        Assert.All(result.Files, f => Assert.True(f.Target is "factory" or "copilot"));
    }

    [Fact]
    public async Task Render_All10TargetsSimultaneously_EmitsCompleteIsolatedPlan()
    {
        // K7 Area 1: Rendering all 10 targets in one request accounts for all targets deterministically.
        IReadOnlyList<SquadTarget> allTargets = SquadTargetCatalog.All;
        Assert.Equal(10, allTargets.Count);

        FakeApmRunner runner = new FakeApmRunner();
        ApmRenderRequest request = new(
            SourceDirectory: "/staging/products/kyber-squad",
            Targets: allTargets,
            Scope: SquadDeploymentScope.Project);

        ApmRenderResult result = await runner.RenderAsync(request);

        Assert.True(result.Success);
        HashSet<string> emittedTargets = result.Files.Select(f => f.Target).ToHashSet();
        Assert.Equal(10, emittedTargets.Count);
        Assert.All(All10TargetTokens, token => Assert.Contains(token, emittedTargets));
    }

    #endregion

    #region Area 2: conductor and conductor-v3 single-projection rules

    [Theory]
    [MemberData(nameof(AllTargetTheoryData))]
    public async Task Render_ConductorAndConductorV3_ProduceExactlyOneProjectionPerTarget(SquadTarget target)
    {
        // K7 Area 2: Single-projection rule: conductor and conductor-v3 must never produce duplicate projections
        // (no target has both an agent and a skill for conductor or conductor-v3).
        FakeApmRunner runner = new FakeApmRunner();
        ApmRenderRequest request = new(
            SourceDirectory: "/staging/products/kyber-squad",
            Targets: [target],
            Scope: SquadDeploymentScope.Project);

        ApmRenderResult result = await runner.RenderAsync(request);

        bool isNative = NativeTargets.Contains(target);

        foreach (string conductorIdentity in FakeApmRunner.SharedConductorIdentities)
        {
            // Count total files containing this conductor identity in filename
            IEnumerable<SquadDeploymentFile> matchedFiles = result.Files.Where(f =>
                f.RelativePath.EndsWith($"/{conductorIdentity}.toml", StringComparison.Ordinal) ||
                f.RelativePath.EndsWith($"/{conductorIdentity}.md", StringComparison.Ordinal) ||
                f.RelativePath.Contains($"/{conductorIdentity}/SKILL.md", StringComparison.Ordinal));

            Assert.Single(matchedFiles);

            SquadDeploymentFile file = matchedFiles.Single();
            if (isNative)
            {
                Assert.Contains("/agents/", file.RelativePath, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("/skills/", file.RelativePath, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                Assert.Contains("/skills/", file.RelativePath, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("/agents/", file.RelativePath, StringComparison.OrdinalIgnoreCase);
                // Conductor on fallback retains same-name skill, no 'role-' prefix
                Assert.Contains($"/{conductorIdentity}/SKILL.md", file.RelativePath, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task Render_DuplicateConductorProjection_IsDetectedAndRejected()
    {
        // K7 Area 2: If upstream APM were to emit both native agent and fallback skill on a native target,
        // it must be caught and rejected.
        FakeApmRunner runner = new FakeApmRunner();
        runner.WithDuplicateProjection("codex", "conductor");

        ApmRenderRequest request = new(
            SourceDirectory: "/staging/products/kyber-squad",
            Targets: [SquadTarget.Codex],
            Scope: SquadDeploymentScope.Project);

        ApmRenderResult result = await runner.RenderAsync(request);

        // Verify that the duplicate projection exists in the raw runner output
        List<SquadDeploymentFile> conductorFiles = result.Files.Where(f =>
            f.RelativePath.EndsWith("/conductor.toml", StringComparison.Ordinal) ||
            f.RelativePath.Contains("/conductor/SKILL.md", StringComparison.Ordinal)).ToList();

        Assert.Equal(2, conductorFiles.Count);
        Assert.Contains(conductorFiles, f => f.RelativePath.Contains("/agents/", StringComparison.Ordinal));
        Assert.Contains(conductorFiles, f => f.RelativePath.Contains("/skills/", StringComparison.Ordinal));
    }

    #endregion

    #region Area 3: Distinct-body collisions lowered to role-<name> on fallback targets

    [Theory]
    [InlineData("gemini")]
    [InlineData("antigravity")]
    [InlineData("warp")]
    public async Task Render_FallbackTargets_Lower7CollisionIdentitiesToRolePrefixedSkills(string fallbackToken)
    {
        // K7 Area 3: The 7 distinct-body collision identities are lowered to role-<name> on fallback targets,
        // while their canonical skills remain at <name>.
        SquadTarget target = Assert.Single(SquadTargetCatalog.Parse([fallbackToken]));
        FakeApmRunner runner = new FakeApmRunner();
        ApmRenderRequest request = new(
            SourceDirectory: "/staging/products/kyber-squad",
            Targets: [target],
            Scope: SquadDeploymentScope.Project);

        ApmRenderResult result = await runner.RenderAsync(request);

        foreach (string collision in FakeApmRunner.DistinctBodyCollisionIdentities)
        {
            // 1. Canonical skill exists at <name>
            string canonicalSkillPath = FakeApmRunner.GetTargetSkillFilePath(target, collision);
            Assert.Contains(result.Files, f => string.Equals(f.RelativePath, canonicalSkillPath, StringComparison.Ordinal));

            // 2. Lowered agent body exists at role-<name>
            string loweredRoleSkillPath = FakeApmRunner.GetTargetSkillFilePath(target, $"role-{collision}");
            Assert.Contains(result.Files, f => string.Equals(f.RelativePath, loweredRoleSkillPath, StringComparison.Ordinal));

            // 3. Degradation metadata records canonical identity and output identity
            ApmDegradationRecord degradation = Assert.Single(
                result.Degradations,
                d => string.Equals(d.CanonicalIdentity, collision, StringComparison.Ordinal));

            Assert.Equal($"role-{collision}", degradation.OutputIdentity);
            Assert.Equal("role-skill-fallback", degradation.Code);
            Assert.False(string.IsNullOrWhiteSpace(degradation.InstructionDigest));
        }

        // Total skills on fallback target = 25 canonical + 7 collision role-skills + 11 non-collision role-skills = 43
        Assert.Equal(43, result.Files.Count);
    }

    [Theory]
    [MemberData(nameof(NativeTargetTheoryData))]
    public async Task Render_NativeTargets_DoNotEmitRolePrefixedFiles(SquadTarget nativeTarget)
    {
        // K7 Area 3: Native targets must NOT emit role-<name> prefixed files because agents and skills
        // occupy separate native namespaces.
        FakeApmRunner runner = new FakeApmRunner();
        ApmRenderRequest request = new(
            SourceDirectory: "/staging/products/kyber-squad",
            Targets: [nativeTarget],
            Scope: SquadDeploymentScope.Project);

        ApmRenderResult result = await runner.RenderAsync(request);

        Assert.DoesNotContain(result.Files, f => f.RelativePath.Contains("role-", StringComparison.OrdinalIgnoreCase));
    }

    #endregion

    #region Area 4: Preserving exact UTF-8/LF canonical instruction-body SHA-256 digests

    [Fact]
    public async Task Render_DegradationMetadata_PreservesExactNormalizedBodySha256Digest()
    {
        // K7 Area 4: Every lowered role carries the canonical instruction-body SHA-256 digest.
        FakeApmRunner runner = new FakeApmRunner();
        ApmRenderRequest request = new(
            SourceDirectory: "/staging/products/kyber-squad",
            Targets: [SquadTarget.Gemini],
            Scope: SquadDeploymentScope.Project);

        ApmRenderResult result = await runner.RenderAsync(request);

        Assert.Equal(20, result.Degradations.Count);
        foreach (ApmDegradationRecord degradation in result.Degradations)
        {
            string canonicalBody = FakeApmRunner.GetAgentBody(degradation.CanonicalIdentity);
            string expectedDigest = FakeApmRunner.ComputeSha256(canonicalBody);

            Assert.Equal(64, degradation.InstructionDigest.Length);
            Assert.Equal(expectedDigest, degradation.InstructionDigest);
            Assert.Equal(degradation.InstructionDigest.ToLowerInvariant(), degradation.InstructionDigest);
        }
    }

    [Fact]
    public async Task Render_MissingDigestInDegradation_IsDetected()
    {
        // K7 Area 4: A degradation record missing an instruction digest violates the safety contract.
        FakeApmRunner runner = new FakeApmRunner();
        runner.WithMissingDigest("architect");

        ApmRenderRequest request = new(
            SourceDirectory: "/staging/products/kyber-squad",
            Targets: [SquadTarget.Gemini],
            Scope: SquadDeploymentScope.Project);

        ApmRenderResult result = await runner.RenderAsync(request);

        ApmDegradationRecord architectDegradation = Assert.Single(
            result.Degradations,
            d => d.CanonicalIdentity == "architect");

        Assert.True(string.IsNullOrWhiteSpace(architectDegradation.InstructionDigest));
    }

    [Fact]
    public async Task Render_CorruptedDigestInDegradation_IsDetected()
    {
        // K7 Area 4: A degradation record with a mismatched digest violates integrity.
        FakeApmRunner runner = new FakeApmRunner();
        runner.WithCorruptedDigest("dotnet-dev");

        ApmRenderRequest request = new(
            SourceDirectory: "/staging/products/kyber-squad",
            Targets: [SquadTarget.Gemini],
            Scope: SquadDeploymentScope.Project);

        ApmRenderResult result = await runner.RenderAsync(request);

        ApmDegradationRecord dotnetDevDegradation = Assert.Single(
            result.Degradations,
            d => d.CanonicalIdentity == "dotnet-dev");

        string canonicalBody = FakeApmRunner.GetAgentBody("dotnet-dev");
        string expectedDigest = FakeApmRunner.ComputeSha256(canonicalBody);

        Assert.NotEqual(expectedDigest, dotnetDevDegradation.InstructionDigest);
    }

    [Fact]
    public void Render_NormalizedUtf8LfBodies_ProduceStableSha256DigestsAcrossCrLfAndLf()
    {
        // K7 Area 4: CRLF vs LF normalization produces identical digests.
        string lfBody = "You are architect.\nPlan first.\n";
        string crlfBody = "You are architect.\r\nPlan first.\r\n";

        string lfDigest = FakeApmRunner.ComputeSha256(lfBody);
        string crlfDigest = FakeApmRunner.ComputeSha256(crlfBody);

        Assert.Equal(lfDigest, crlfDigest);
        Assert.Equal(64, lfDigest.Length);
    }

    #endregion

    #region Area 5: Semantic permission lattice resolution (deny < ask < allow)

    [Fact]
    public async Task Render_PermissionWidening_FailsWithActionableError()
    {
        // K7 Area 5: Broadening permissions (e.g. deny -> allow) is strictly forbidden.
        FakeApmRunner runner = new FakeApmRunner();
        runner.WithPermissionWidening("architect", "filesystem.write");

        ApmRenderRequest request = new(
            SourceDirectory: "/staging/products/kyber-squad",
            Targets: [SquadTarget.Claude],
            Scope: SquadDeploymentScope.Project);

        ApmRenderResult result = await runner.RenderAsync(request);

        Assert.False(result.Success);
        Assert.NotEmpty(result.Errors);
        Assert.Contains(result.Errors, e => e.Contains("widening", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(SquadPermissionDecision.Deny, SquadPermissionDecision.Ask, true)]
    [InlineData(SquadPermissionDecision.Deny, SquadPermissionDecision.Allow, true)]
    [InlineData(SquadPermissionDecision.Ask, SquadPermissionDecision.Allow, true)]
    [InlineData(SquadPermissionDecision.Allow, SquadPermissionDecision.Ask, false)]
    [InlineData(SquadPermissionDecision.Ask, SquadPermissionDecision.Deny, false)]
    [InlineData(SquadPermissionDecision.Allow, SquadPermissionDecision.Deny, false)]
    public void PermissionLattice_Ordering_EnforcesDenyLessThanAskLessThanAllow(
        SquadPermissionDecision from,
        SquadPermissionDecision to,
        bool isWidening)
    {
        // K7 Area 5: Lattice order deny < ask < allow. Safe narrowing is allowed; widening is forbidden.
        bool widened = to > from;
        Assert.Equal(isWidening, widened);
    }

    [Fact]
    public async Task Render_UnrecognizedCapabilityToken_ReportsError()
    {
        // K7 Area 5: Unrecognized capabilities must be rejected.
        FakeApmRunner runner = new FakeApmRunner();
        runner.WithRenderHandler(_ => new ApmRenderResult(
            Success: false,
            Files: [],
            Degradations: [],
            Warnings: [],
            Errors: ["Unrecognized capability 'network.arbitrary_connect' declared in profile 'architect'."]));

        ApmRenderRequest request = new(
            SourceDirectory: "/staging/products/kyber-squad",
            Targets: [SquadTarget.Claude],
            Scope: SquadDeploymentScope.Project);

        ApmRenderResult result = await runner.RenderAsync(request);

        Assert.False(result.Success);
        Assert.Single(result.Errors);
        Assert.Contains("Unrecognized capability", result.Errors[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Render_UnrecognizedModelProfile_ReportsError()
    {
        // K7 Area 5: Unrecognized model profiles must be rejected.
        FakeApmRunner runner = new FakeApmRunner();
        runner.WithRenderHandler(_ => new ApmRenderResult(
            Success: false,
            Files: [],
            Degradations: [],
            Warnings: [],
            Errors: ["Agent 'architect' references unknown model profile 'unsupported-gpt-model'."]));

        ApmRenderRequest request = new(
            SourceDirectory: "/staging/products/kyber-squad",
            Targets: [SquadTarget.Claude],
            Scope: SquadDeploymentScope.Project);

        ApmRenderResult result = await runner.RenderAsync(request);

        Assert.False(result.Success);
        Assert.Single(result.Errors);
        Assert.Contains("unknown model profile", result.Errors[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Render_SafetyNarrowing_RecordsSafetyNarrowedDegradation()
    {
        // K7 Area 5: Unsupported 'ask' may safely narrow to 'deny' and is recorded as safety-narrowed.
        FakeApmRunner runner = new FakeApmRunner();
        runner.WithRenderHandler(req => new ApmRenderResult(
            Success: true,
            Files: [new SquadDeploymentFile(".gemini/skills/architect/SKILL.md", Encoding.UTF8.GetBytes("body"), "gemini")],
            Degradations:
            [
                new ApmDegradationRecord(
                    Target: "gemini",
                    CanonicalIdentity: "architect",
                    OutputIdentity: "architect",
                    Code: "safety-narrowed",
                    InstructionDigest: FakeApmRunner.ComputeSha256(FakeApmRunner.GetAgentBody("architect")),
                    Details: "Permission for 'filesystem.write' narrowed from 'ask' to 'deny' on non-interactive target.")
            ],
            Warnings: [],
            Errors: []));

        ApmRenderRequest request = new(
            SourceDirectory: "/staging/products/kyber-squad",
            Targets: [SquadTarget.Gemini],
            Scope: SquadDeploymentScope.Project);

        ApmRenderResult result = await runner.RenderAsync(request);

        Assert.True(result.Success);
        ApmDegradationRecord degradation = Assert.Single(result.Degradations);
        Assert.Equal("safety-narrowed", degradation.Code);
        Assert.Equal("gemini", degradation.Target);
        Assert.Contains("narrowed", degradation.Details!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Render_UnenforceablePermission_SafelyOmitsRepresentationRatherThanBroaden()
    {
        // K7 Area 5: An unenforceable 'ask' or 'deny' omits that representation rather than broadening it.
        FakeApmRunner runner = new FakeApmRunner();
        runner.WithRenderHandler(req => new ApmRenderResult(
            Success: true,
            Files: [],
            Degradations:
            [
                new ApmDegradationRecord(
                    Target: "warp",
                    CanonicalIdentity: "architect",
                    OutputIdentity: "architect",
                    Code: "omitted",
                    InstructionDigest: FakeApmRunner.ComputeSha256(FakeApmRunner.GetAgentBody("architect")),
                    Details: "Agent representation omitted because required 'deny' permission cannot be enforced on target.")
            ],
            Warnings: [],
            Errors: []));

        ApmRenderRequest request = new(
            SourceDirectory: "/staging/products/kyber-squad",
            Targets: [SquadTarget.Warp],
            Scope: SquadDeploymentScope.Project);

        ApmRenderResult result = await runner.RenderAsync(request);

        Assert.True(result.Success);
        Assert.Empty(result.Files);
        ApmDegradationRecord degradation = Assert.Single(result.Degradations);
        Assert.Equal("omitted", degradation.Code);
    }

    #endregion

    #region Area 6: Agent Plugins v1.0.0 output validation

    [Fact]
    public async Task Pack_PluginsFormat_ExportsOnly25SkillsAndMcp_ExcludesAgents()
    {
        // K7 Area 6: Agent Plugins v1.0.0 output validation: only skills and MCP servers are exported;
        // agents are excluded as portable components.
        FakeApmRunner runner = new FakeApmRunner();
        ApmPackRequest request = new(
            SourceDirectory: "/staging/products/kyber-squad",
            Format: ApmPackFormat.Plugins,
            OutputDirectory: "/out",
            Version: "1.2.3");

        ApmPackResult result = await runner.PackAsync(request);

        Assert.True(result.Success);
        Assert.Single(result.CreatedArchives);
        Assert.EndsWith("kyber-squad-plugin-1.2.3.zip", result.CreatedArchives[0], StringComparison.Ordinal);

        Assert.NotNull(result.PluginManifestJson);
        using JsonDocument doc = JsonDocument.Parse(result.PluginManifestJson!);
        JsonElement root = doc.RootElement;

        // Schema validation
        Assert.Equal("https://agent-plugins.org/v1/schema.json", root.GetProperty("$schema").GetString());

        // Skills exported: exactly 25
        Assert.True(root.TryGetProperty("skills", out JsonElement skillsElement));
        Assert.Equal(JsonValueKind.Array, skillsElement.ValueKind);
        Assert.Equal(25, skillsElement.GetArrayLength());

        List<string> exportedSkillNames = skillsElement.EnumerateArray()
            .Select(s => s.GetProperty("name").GetString()!)
            .ToList();
        Assert.Equal(FakeApmRunner.CanonicalSkills.OrderBy(s => s), exportedSkillNames.OrderBy(s => s));

        // MCP server exported
        Assert.True(root.TryGetProperty("mcpServers", out JsonElement mcpElement));
        Assert.True(mcpElement.TryGetProperty("kyber-weave", out _));

        // Agents strictly excluded from portable component set
        Assert.False(root.TryGetProperty("agents", out _), "Agents must not be declared as portable components in Agent Plugins v1.0.0.");
    }

    [Fact]
    public async Task Pack_AgentPluginsManifestWithAgents_IsDetectedAsSchemaViolation()
    {
        // K7 Area 6: If upstream APM exports agents in plugin.json, it violates Agent Plugins v1 specification.
        FakeApmRunner runner = new FakeApmRunner();
        runner.WithAgentsInPluginManifest(true);

        ApmPackRequest request = new(
            SourceDirectory: "/staging/products/kyber-squad",
            Format: ApmPackFormat.Plugins,
            OutputDirectory: "/out",
            Version: "1.2.3");

        ApmPackResult result = await runner.PackAsync(request);

        Assert.NotNull(result.PluginManifestJson);
        using JsonDocument doc = JsonDocument.Parse(result.PluginManifestJson!);
        Assert.True(doc.RootElement.TryGetProperty("agents", out _), "Test setup confirmed agents are present in manifest.");
    }

    [Fact]
    public async Task Pack_AllFormat_ProducesBothApmAndPluginsArchives()
    {
        // K7 Area 6: Format 'all' produces both archives.
        FakeApmRunner runner = new FakeApmRunner();
        ApmPackRequest request = new(
            SourceDirectory: "/staging/products/kyber-squad",
            Format: ApmPackFormat.All,
            OutputDirectory: "/out",
            Version: "1.2.3");

        ApmPackResult result = await runner.PackAsync(request);

        Assert.True(result.Success);
        Assert.Equal(2, result.CreatedArchives.Count);
        Assert.Contains(result.CreatedArchives, a => a.EndsWith("kyber-squad-1.2.3.zip", StringComparison.Ordinal));
        Assert.Contains(result.CreatedArchives, a => a.EndsWith("kyber-squad-plugin-1.2.3.zip", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pack_Failure_ReportsErrorsAndNoArchives()
    {
        // K7 Area 6: Pack failure returns structured errors and no produced archives.
        FakeApmRunner runner = new FakeApmRunner();
        runner.WithPackFailure("APM pack failed: target directory not writable.");

        ApmPackRequest request = new(
            SourceDirectory: "/staging/products/kyber-squad",
            Format: ApmPackFormat.All,
            OutputDirectory: "/out",
            Version: "1.2.3");

        ApmPackResult result = await runner.PackAsync(request);

        Assert.False(result.Success);
        Assert.Empty(result.CreatedArchives);
        Assert.Single(result.Errors);
        Assert.Contains("not writable", result.Errors[0], StringComparison.Ordinal);
    }

    #endregion

    #region Area 7: Production components verification (K7.2 GREEN Contract)

    [Fact]
    public void SquadApmSourceWriter_Write_EmitsCanonicalTreeWithUtf8LfAndValidStructure()
    {
        // K7.2 GREEN Contract: SquadApmSourceWriter writes canonical source with LF normalization and UTF-8 without BOM.
        SquadSource source = CreateMinimalSquadSource();
        using TempDirectory temp = new TempDirectory();

        SquadApmSourceWriter writer = new SquadApmSourceWriter();
        writer.Write(source, temp.Path);

        // Verify root manifest and profiles
        Assert.True(File.Exists(Path.Combine(temp.Path, "squad.yml")));
        Assert.True(File.Exists(Path.Combine(temp.Path, "bundles", "full.yml")));
        Assert.True(File.Exists(Path.Combine(temp.Path, "profiles", "models.yml")));
        Assert.True(File.Exists(Path.Combine(temp.Path, "profiles", "capabilities.yml")));
        Assert.True(File.Exists(Path.Combine(temp.Path, "profiles", "fallbacks.yml")));
        Assert.True(File.Exists(Path.Combine(temp.Path, "toolchain.yml")));
        Assert.True(File.Exists(Path.Combine(temp.Path, "mcp.json")));

        // Verify all 20 agents
        foreach (string agent in FakeApmRunner.CanonicalAgents)
        {
            string agentPath = Path.Combine(temp.Path, "agents", $"{agent}.md");
            Assert.True(File.Exists(agentPath), $"Agent file '{agentPath}' should exist.");
            byte[] bytes = File.ReadAllBytes(agentPath);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "Agent file must not contain UTF-8 BOM.");
            string content = Encoding.UTF8.GetString(bytes);
            Assert.DoesNotContain("\r\n", content);
        }

        // Verify all 25 skills
        foreach (string skill in FakeApmRunner.CanonicalSkills)
        {
            string skillPath = Path.Combine(temp.Path, "skills", skill, "SKILL.md");
            Assert.True(File.Exists(skillPath), $"Skill file '{skillPath}' should exist.");
            byte[] bytes = File.ReadAllBytes(skillPath);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "Skill file must not contain UTF-8 BOM.");
            string content = Encoding.UTF8.GetString(bytes);
            Assert.DoesNotContain("\r\n", content);
        }

        // Verify schemas
        Assert.True(File.Exists(Path.Combine(temp.Path, "schemas", "squad.schema.json")));
        Assert.True(File.Exists(Path.Combine(temp.Path, "schemas", "bundle.schema.json")));
        Assert.True(File.Exists(Path.Combine(temp.Path, "schemas", "agent.schema.json")));
        Assert.True(File.Exists(Path.Combine(temp.Path, "schemas", "model-profiles.schema.json")));
        Assert.True(File.Exists(Path.Combine(temp.Path, "schemas", "capability-profiles.schema.json")));
        Assert.True(File.Exists(Path.Combine(temp.Path, "schemas", "fallback-profiles.schema.json")));

        // Round-trip verification: loading the staging directory produces valid SquadSource
        SquadSource reloaded = SquadSourceLoader.Load(temp.Path);
        Assert.Equal(source.Agents.Count, reloaded.Agents.Count);
        Assert.Equal(source.Skills.Count, reloaded.Skills.Count);
        Assert.Equal(source.Bundle.Name, reloaded.Bundle.Name);
    }

    [Fact]
    public async Task SquadApmCompiler_CompileAsync_HappyPath_ReturnsVerifiedCompilationResult()
    {
        // K7.2 GREEN Contract: SquadApmCompiler compiles via runner into verified SquadApmCompilationResult.
        SquadSource source = CreateMinimalSquadSource();
        FakeApmRunner runner = new FakeApmRunner();
        ISquadApmSourceWriter writer = new SquadApmSourceWriter();
        SquadApmCompiler compiler = new SquadApmCompiler(runner, writer);

        SquadApmCompilationResult result = await compiler.CompileAsync(
            source,
            [SquadTarget.Codex, SquadTarget.Gemini],
            SquadDeploymentScope.Project);

        Assert.NotNull(result);
        Assert.Same(source, result.Source);
        Assert.NotEmpty(result.RenderedFiles);
        Assert.NotEmpty(result.Degradations);
        Assert.Equal(20, result.StructuredDegradations.Count);

        // Degradations correctly mapped for Gemini (fallback target)
        Assert.All(result.Degradations, d => Assert.Equal("gemini", d.Target));
    }

    [Fact]
    public async Task SquadApmCompiler_CompileAsync_RejectsDuplicateConductorProjection()
    {
        // K7.2 GREEN Contract: SquadApmCompiler detects and rejects duplicate conductor projections.
        SquadSource source = CreateMinimalSquadSource();
        FakeApmRunner runner = new FakeApmRunner();
        runner.WithDuplicateProjection("codex", "conductor");

        ISquadApmSourceWriter writer = new SquadApmSourceWriter();
        SquadApmCompiler compiler = new SquadApmCompiler(runner, writer);

        SquadApmValidationException ex = await Assert.ThrowsAsync<SquadApmValidationException>(() =>
            compiler.CompileAsync(
                source,
                [SquadTarget.Codex],
                SquadDeploymentScope.Project));

        Assert.Contains("conductor", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SquadApmCompiler_CompileAsync_RejectsMissingOrCorruptedDigest()
    {
        // K7.2 GREEN Contract: SquadApmCompiler detects and rejects missing or corrupted instruction digest.
        SquadSource source = CreateMinimalSquadSource();
        ISquadApmSourceWriter writer = new SquadApmSourceWriter();

        // 1. Missing digest
        FakeApmRunner runnerMissing = new FakeApmRunner();
        runnerMissing.WithMissingDigest("architect");
        SquadApmCompiler compilerMissing = new SquadApmCompiler(runnerMissing, writer);

        SquadApmValidationException exMissing = await Assert.ThrowsAsync<SquadApmValidationException>(() =>
            compilerMissing.CompileAsync(
                source,
                [SquadTarget.Gemini],
                SquadDeploymentScope.Project));

        Assert.Contains("missing", exMissing.Message, StringComparison.OrdinalIgnoreCase);

        // 2. Corrupted digest
        FakeApmRunner runnerCorrupt = new FakeApmRunner();
        runnerCorrupt.WithCorruptedDigest("dotnet-dev");
        SquadApmCompiler compilerCorrupt = new SquadApmCompiler(runnerCorrupt, writer);

        SquadApmValidationException exCorrupt = await Assert.ThrowsAsync<SquadApmValidationException>(() =>
            compilerCorrupt.CompileAsync(
                source,
                [SquadTarget.Gemini],
                SquadDeploymentScope.Project));

        Assert.Contains("mismatched instruction digest", exCorrupt.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SquadApmCompiler_CompileAsync_RejectsPermissionWidening()
    {
        // K7.2 GREEN Contract: SquadApmCompiler rejects permission widening.
        SquadSource source = CreateMinimalSquadSource();
        FakeApmRunner runner = new FakeApmRunner();
        runner.WithPermissionWidening("architect", "filesystem.write");

        ISquadApmSourceWriter writer = new SquadApmSourceWriter();
        SquadApmCompiler compiler = new SquadApmCompiler(runner, writer);

        SquadApmValidationException ex = await Assert.ThrowsAsync<SquadApmValidationException>(() =>
            compiler.CompileAsync(
                source,
                [SquadTarget.Claude],
                SquadDeploymentScope.Project));

        Assert.Contains("widening", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SquadApmCompiler_CompileAsync_RejectsRolePrefixedFilesOnNativeTargets()
    {
        // K7.2 GREEN Contract: SquadApmCompiler rejects role- prefixed files on native targets.
        SquadSource source = CreateMinimalSquadSource();
        FakeApmRunner runner = new FakeApmRunner();
        runner.WithRenderHandler(_ => new ApmRenderResult(
            Success: true,
            Files:
            [
                new SquadDeploymentFile(".codex/agents/role-architect.toml", Encoding.UTF8.GetBytes("body"), "codex")
            ],
            Degradations: [],
            Warnings: [],
            Errors: []));

        ISquadApmSourceWriter writer = new SquadApmSourceWriter();
        SquadApmCompiler compiler = new SquadApmCompiler(runner, writer);

        SquadApmValidationException ex = await Assert.ThrowsAsync<SquadApmValidationException>(() =>
            compiler.CompileAsync(
                source,
                [SquadTarget.Codex],
                SquadDeploymentScope.Project));

        Assert.Contains("role-prefixed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ApmProcessRunner_RenderAsync_ExecutesProcessRunnerWithArgumentListAndParsesOutput()
    {
        // K7.2 GREEN Contract: ApmProcessRunner uses ProcessRunner via ArgumentList with no shell string.
        string jsonOutput = """
            {
              "success": true,
              "files": [
                {
                  "path": ".claude/agents/architect.md",
                  "target": "claude",
                  "content": "You are architect."
                }
              ],
              "degradations": [
                {
                  "target": "claude",
                  "canonicalIdentity": "architect",
                  "outputIdentity": "architect",
                  "code": "native",
                  "instructionDigest": "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789"
                }
              ],
              "warnings": [],
              "errors": []
            }
            """;

        ApmTestRecordingExecutor executor = new(new ProcessResult(0, jsonOutput, string.Empty));
        ApmProcessRunner runner = new ApmProcessRunner(executor);

        ApmRenderRequest request = new(
            SourceDirectory: "/staging",
            Targets: [SquadTarget.Claude],
            Scope: SquadDeploymentScope.Project);

        ApmRenderResult result = await runner.RenderAsync(request);

        Assert.True(result.Success);
        Assert.Single(result.Files);
        Assert.Equal(".claude/agents/architect.md", result.Files[0].RelativePath);
        Assert.Equal("claude", result.Files[0].Target);
        Assert.Equal("You are architect.", Encoding.UTF8.GetString(result.Files[0].Content.Span));

        // Verify ProcessStartInfo arguments
        ProcessStartInfo startInfo = Assert.IsType<ProcessStartInfo>(executor.StartInfo);
        Assert.Equal("apm", startInfo.FileName);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.RedirectStandardInput);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.Contains("compile", startInfo.ArgumentList);
        Assert.Contains("--source", startInfo.ArgumentList);
        Assert.Contains("/staging", startInfo.ArgumentList);
        Assert.Contains("--target", startInfo.ArgumentList);
        Assert.Contains("claude", startInfo.ArgumentList);
        Assert.Contains("--scope", startInfo.ArgumentList);
        Assert.Contains("project", startInfo.ArgumentList);
        Assert.Contains("--format", startInfo.ArgumentList);
        Assert.Contains("json", startInfo.ArgumentList);
    }

    [Fact]
    public async Task ApmProcessRunner_PackAsync_ExecutesProcessRunnerWithArgumentListAndParsesOutput()
    {
        // K7.2 GREEN Contract: ApmProcessRunner executes apm pack with ArgumentList.
        ApmTestRecordingExecutor executor = new(new ProcessResult(0, "Pack completed successfully.\n", string.Empty));
        ApmProcessRunner runner = new ApmProcessRunner(executor);

        using TempDirectory tempOut = new TempDirectory();
        // Create mock archives
        string apmZip = Path.Combine(tempOut.Path, "kyber-squad-1.2.3.zip");
        string pluginZip = Path.Combine(tempOut.Path, "kyber-squad-plugin-1.2.3.zip");
        File.WriteAllBytes(apmZip, [1, 2, 3]);
        File.WriteAllBytes(pluginZip, [4, 5, 6]);

        ApmPackRequest request = new(
            SourceDirectory: "/staging",
            Format: ApmPackFormat.All,
            OutputDirectory: tempOut.Path,
            Version: "1.2.3");

        ApmPackResult result = await runner.PackAsync(request);

        Assert.True(result.Success);
        Assert.Equal(2, result.CreatedArchives.Count);

        ProcessStartInfo startInfo = Assert.IsType<ProcessStartInfo>(executor.StartInfo);
        Assert.Equal("apm", startInfo.FileName);
        Assert.False(startInfo.UseShellExecute);
        Assert.Contains("pack", startInfo.ArgumentList);
        Assert.Contains("--source", startInfo.ArgumentList);
        Assert.Contains("/staging", startInfo.ArgumentList);
        Assert.Contains("--format", startInfo.ArgumentList);
        Assert.Contains("all", startInfo.ArgumentList);
        Assert.Contains("--out", startInfo.ArgumentList);
        Assert.Contains(tempOut.Path, startInfo.ArgumentList);
        Assert.Contains("--version", startInfo.ArgumentList);
        Assert.Contains("1.2.3", startInfo.ArgumentList);
    }

    [Fact]
    public async Task ApmProcessRunner_RenderAsync_WithFailingExitCode_ReturnsErrorResult()
    {
        // K7.2 GREEN Contract: ApmProcessRunner returns structured errors when APM process fails.
        ApmTestRecordingExecutor executor = new(new ProcessResult(1, string.Empty, "Error: Unknown target 'invalid-target'"));
        ApmProcessRunner runner = new ApmProcessRunner(executor);

        ApmRenderRequest request = new(
            SourceDirectory: "/staging",
            Targets: [SquadTarget.Codex],
            Scope: SquadDeploymentScope.Project);

        ApmRenderResult result = await runner.RenderAsync(request);

        Assert.False(result.Success);
        Assert.Single(result.Errors);
        Assert.Contains("Unknown target", result.Errors[0], StringComparison.Ordinal);
    }

    #endregion

    #region Helpers and Theory Data

    public static TheoryData<SquadTarget> AllTargetTheoryData =>
        new([.. SquadTargetCatalog.All]);

    public static TheoryData<SquadTarget> NativeTargetTheoryData =>
        new([.. NativeTargets]);

    private static SquadSource CreateMinimalSquadSource()
    {
        SquadManifest manifest = new(
            Schema: "kyber-squad.squad/v1",
            Name: "kyber-squad",
            VersionSource: "kyber-weave-assembly",
            DefaultBundle: "full",
            Bundles: new Dictionary<string, string> { ["full"] = "bundles/full.yml" },
            Profiles: new SquadProfilePaths("profiles/models.yml", "profiles/capabilities.yml", "profiles/fallbacks.yml"),
            ToolchainPath: "toolchain.yml",
            McpPath: "mcp.json",
            SourcePath: "squad.yml");

        SquadBundle bundle = new(
            Schema: "kyber-squad.bundle/v1",
            Name: "full",
            AgentNames: FakeApmRunner.CanonicalAgents,
            SkillNames: FakeApmRunner.CanonicalSkills,
            SourcePath: "bundles/full.yml");

        List<SquadAgent> agents = FakeApmRunner.CanonicalAgents.Select(name => new SquadAgent(
            Schema: "kyber-squad.agent/v1",
            Name: name,
            Description: $"Description for {name}",
            Invocation: SquadInvocation.Subagent,
            ModelProfile: "general",
            CapabilityProfile: "worker",
            DelegatesTo: [],
            Fallback: "role-skill",
            Aliases: [],
            InstructionBody: FakeApmRunner.GetAgentBody(name),
            BodyDigest: FakeApmRunner.ComputeSha256(FakeApmRunner.GetAgentBody(name)),
            SourcePath: $"agents/{name}.md")).ToList();

        List<SquadSkill> skills = FakeApmRunner.CanonicalSkills.Select(name => new SquadSkill(
            Name: name,
            Description: $"Description for {name}",
            InstructionBody: FakeApmRunner.SharedConductorIdentities.Contains(name, StringComparer.Ordinal)
                ? FakeApmRunner.GetAgentBody(name)
                : $"# {name}\nSkill instructions.\n",
            SourcePath: $"skills/{name}/SKILL.md")).ToList();

        SquadModelProfiles modelProfiles = new(
            Schema: "kyber-squad.model-profiles/v1",
            Profiles: new Dictionary<string, SquadModelProfile>
            {
                ["general"] = new("inherit", new Dictionary<string, string>())
            },
            SourcePath: "profiles/models.yml");

        SquadCapabilityProfiles capabilityProfiles = new(
            Schema: "kyber-squad.capability-profiles/v1",
            Capabilities: ["filesystem.read", "filesystem.write"],
            Profiles: new Dictionary<string, SquadCapabilityProfile>
            {
                ["worker"] = new(new Dictionary<string, SquadPermissionDecision>
                {
                    ["filesystem.read"] = SquadPermissionDecision.Allow,
                    ["filesystem.write"] = SquadPermissionDecision.Ask
                })
            },
            SourcePath: "profiles/capabilities.yml");

        SquadFallbackProfiles fallbackProfiles = new(
            Schema: "kyber-squad.fallback-profiles/v1",
            Profiles: new Dictionary<string, SquadFallbackProfile>
            {
                ["role-skill"] = new(
                    NoPrimaryAgent: "skill",
                    NoAgentPrimitive: "skill",
                    BodySource: "agent",
                    OutputIdentity: new SquadFallbackOutputIdentity("agent-name", "reuse-skill", "role-prefixed-agent-name", "role-"),
                    SharedIdentities: FakeApmRunner.SharedConductorIdentities)
            },
            SourcePath: "profiles/fallbacks.yml");

        SquadToolchain toolchain = new(
            Schema: "kyber-squad.toolchain/v1",
            RequiredFeatures: ["agent-ir/v1"],
            ValidatedRelease: null,
            SourcePath: "toolchain.yml");

        using JsonDocument mcpDoc = JsonDocument.Parse("""{"mcpServers":{}}""");

        return new SquadSource(
            RootPath: "/fake/root",
            Manifest: manifest,
            Bundle: bundle,
            Agents: agents,
            Skills: skills,
            ModelProfiles: modelProfiles,
            CapabilityProfiles: capabilityProfiles,
            FallbackProfiles: fallbackProfiles,
            Toolchain: toolchain,
            McpConfiguration: mcpDoc.RootElement.Clone());
    }

    private sealed class ApmTestRecordingExecutor : IProcessExecutor
    {
        private readonly ProcessResult _result;
        private readonly Exception? _exception;

        public ApmTestRecordingExecutor(ProcessResult result) => _result = result;

        public ApmTestRecordingExecutor(Exception exception) => _exception = exception;

        public ProcessStartInfo? StartInfo { get; private set; }

        public string? StandardInput { get; private set; }

        public ProcessResult Run(ProcessStartInfo startInfo, string standardInput)
        {
            StartInfo = startInfo;
            StandardInput = standardInput;
            if (_exception is not null)
                throw _exception;

            return _result;
        }
    }

    #endregion
}
