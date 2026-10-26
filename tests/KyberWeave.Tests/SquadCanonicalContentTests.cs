using System.Text.Json;
using System.Text.RegularExpressions;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace KyberWeave.Tests;

public sealed class SquadCanonicalContentTests
{
    private const string MigrationSchema = "kyber-squad.migration/v1";
    private const string MigrationSourceCommit = "677c3a876ba9c62f1083608596b238c9deaff167";
    private const string DefaultOrchestrationTrigger = "default entry point";

    /// <summary>
    /// Closed <c>CIRCUIT_BREAKER_TRIGGER</c> vocabulary. Presence checks cannot
    /// reject a fifth token, so harvested trigger lines must equal this set.
    /// </summary>
    private static readonly string[] CircuitBreakerTriggerTokens =
    [
        "BLAST_RADIUS_EXCEEDED",
        "INVARIANT_CONTRADICTION",
        "ITERATION_CAP_EXCEEDED",
        "THRASH_OSCILLATION_DETECTED"
    ];

    private static readonly Regex CircuitBreakerTriggerEnumeration = new(
        @"CIRCUIT_BREAKER_TRIGGER:\s*<(?<list>[^>]+)>",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex CircuitBreakerTriggerSingleton = new(
        @"CIRCUIT_BREAKER_TRIGGER:\s*`?(?<token>[A-Z][A-Z0-9_]*)`?(?!\s*[|<])",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex CircuitBreakerTriggerToken = new(
        @"[A-Z][A-Z0-9_]*",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly string[] RetiredAgentIdentities =
    [
        "architect-v3",
        "conductor-v3",
        "task-reviewer",
        "task-reviewer-v3"
    ];

    /// <summary>
    /// Developer agents that run the deterministic <c>task-gate</c> exit gate and report its
    /// <c>TASK_GATE:</c> digest. Every other canonical agent is non-developer and must never emit one.
    /// </summary>
    private static readonly string[] DeveloperGateAgents =
    [
        "csharp-dev",
        "dal-dev",
        "github-devops",
        "go-dev",
        "maui-dev",
        "pulumi-dev",
        "python-dev",
        "react-dev",
        "rust-dev",
        "sql-database-architect",
        "tauri-dev",
        "test-dev",
        "typescript-dev"
    ];

    /// <summary>
    /// Agents imported from the designated Hotshot Copilot golden tree. Each carries a
    /// migration record naming the exact golden path, source commit, file digest, and
    /// normalized instruction-body digest.
    /// </summary>
    private static readonly string[] ExpectedMigratedAgents =
    [
        "architect",
        "azure-reader",
        "bug-crusher-investigator",
        "code-reviewer",
        "conductor",
        "csharp-dev",
        "dal-dev",
        "docs-dev",
        "github-devops",
        "maui-dev",
        "product-owner",
        "pulumi-dev",
        "python-dev",
        "react-dev",
        "research-agent",
        "sql-database-architect",
        "tauri-dev",
        "test-dev"
    ];

    /// <summary>
    /// Agents authored after the Hotshot migration — the review-council workers, `bug-crusher`,
    /// and the Rust, Go, and TypeScript specialists — are canonical-only roles and are deliberately excluded
    /// from Hotshot migration provenance.
    /// </summary>
    private static readonly string[] ExpectedNewAgents =
        ["bug-crusher", "go-dev", "review-lens", "review-triage", "rust-dev", "typescript-dev"];

    private static readonly string[] ExpectedAgents =
        [.. ExpectedMigratedAgents.Concat(ExpectedNewAgents).Order(StringComparer.Ordinal)];

    private static readonly string[] ExpectedSkills =
    [
        "app-docs-standard",
        "architecture-decision-record",
        "azure-cli",
        "azure-naming",
        "bug-crusher",
        "code-review",
        "code-review-loop",
        "create-pull-request",
        "csharp-dev",
        "csp-security",
        "dal-dev",
        "dp-code-reviewer",
        "github-cli",
        "github-devops",
        "go-dev",
        "lm-studio-cli",
        "maui-dev",
        "pr-review-fix-comments",
        "product-owner",
        "python-dev",
        "resharper-clt",
        "rust-dev",
        "security-review",
        "setup-dev-environment",
        "task-gate",
        "test-dev",
        "typescript-dev"
    ];

    /// <summary>
    /// Every identity carried by both an agent and a canonical skill. `product-owner` and
    /// `bug-crusher` are the primary shared identities (thin wrappers reusing the canonical
    /// skill as their entry point); the rest are distinct-body collisions lowered to a
    /// role-prefixed skill on a fallback target.
    /// </summary>
    private static readonly string[] ExpectedAgentSkillIntersections =
    [
        "bug-crusher",
        "csharp-dev",
        "dal-dev",
        "github-devops",
        "go-dev",
        "maui-dev",
        "product-owner",
        "python-dev",
        "rust-dev",
        "test-dev",
        "typescript-dev"
    ];

    /// <summary>
    /// The intersections whose agent and skill bodies differ AND are not declared shared
    /// identities, so a fallback target must project the agent to <c>role-&lt;name&gt;</c>.
    /// The two primary shared identities are excluded: their canonical skill is the entry point,
    /// so no role-prefixed duplicate is generated.
    /// </summary>
    private static readonly string[] DistinctBodyAgentSkillCollisions =
    [
        "csharp-dev",
        "dal-dev",
        "github-devops",
        "go-dev",
        "maui-dev",
        "python-dev",
        "rust-dev",
        "test-dev",
        "typescript-dev"
    ];

    /// <summary>The primary shared identities declared by the <c>role-skill</c> profile.</summary>
    private static readonly string[] ExpectedSharedIdentities = ["bug-crusher", "product-owner"];

    private static readonly IReadOnlyDictionary<string, string> ExpectedBaselines =
        ExpectedMigratedAgents.ToDictionary(
            name => name,
            name => $".github/agents/{name}.agent.md",
            StringComparer.Ordinal);

    [Fact]
    public void LoadFullProductTreeContainsExactlyApprovedAgentIdentities()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);

        Assert.Equal(ExpectedAgents, source.Agents.Select(agent => agent.Name));
        Assert.Equal(ExpectedAgents, source.Bundle.AgentNames.Order(StringComparer.Ordinal));
        Assert.All(
            source.Agents,
            agent => Assert.Equal($"agents/{agent.Name}.md", agent.SourcePath));
    }

    [Fact]
    public void LoadFullProductTreeContainsExactlyApprovedSkillIdentities()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);

        Assert.Equal(ExpectedSkills, source.Skills.Select(skill => skill.Name));
        Assert.Equal(ExpectedSkills, source.Bundle.SkillNames.Order(StringComparer.Ordinal));
        Assert.All(
            source.Skills,
            skill => Assert.Equal($"skills/{skill.Name}/SKILL.md", skill.SourcePath));
    }

    [Fact]
    public void LoadFullProductTreeExcludesKyberWeaveDocsSkill()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);

        Assert.DoesNotContain(source.Skills, skill => skill.Name == "kyber-weave-docs");
        Assert.False(Directory.Exists(Path.Combine(ProductRoot, "skills", "kyber-weave-docs")));
    }

    /// <summary>
    /// The retired <c>second-brain</c> skill moves to Kyber-Docs. The Squad bundle file and
    /// the loaded bundle roster must not name it, or pack and install keep shipping it.
    /// </summary>
    [Fact]
    public void LoadFullProductTreeContainsNoSecondBrainInBundleOrRoster()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        string bundleText = File.ReadAllText(Path.Combine(ProductRoot, "bundles", "full.yml"));

        Assert.DoesNotContain(source.Skills, skill => skill.Name == "second-brain");
        Assert.DoesNotContain("second-brain", source.Bundle.SkillNames);
        Assert.DoesNotContain("second-brain", bundleText, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(ProductRoot, "skills", "second-brain")));
    }

    /// <summary>
    /// The product README is the human-readable skill roster. It must not list the retired
    /// skill in its tree or its canonical-skill counts once the source no longer ships it.
    /// </summary>
    [Fact]
    public void ProductReadmeRosterLacksSecondBrain()
    {
        string readme = File.ReadAllText(Path.Combine(ProductRoot, "README.md"));

        Assert.DoesNotContain("second-brain", readme, StringComparison.Ordinal);
        Assert.DoesNotContain("28 canonical skill", readme, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoadRoleSkillFallbackProfileDeclaresGlobalBodyAndIdentityProjection()
    {
        RoleSkillProfile profile = ReadRoleSkillProfile();

        Assert.Equal("skill", profile.NoPrimaryAgent);
        Assert.Equal("skill", profile.NoAgentPrimitive);
        Assert.Equal("agent", profile.BodySource);
        Assert.Equal("agent-name", profile.UnoccupiedIdentity);
        Assert.Equal("reuse-skill", profile.SharedIdentity);
        Assert.Equal("role-prefixed-agent-name", profile.CollisionIdentity);
        Assert.Equal("role-", profile.Prefix);
        Assert.Equal(ExpectedSharedIdentities, profile.SharedIdentities.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void LoadAgentSkillIntersectionHasPinnedSharedAndPrefixedFallbackIdentities()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        Dictionary<string, SquadSkill> skillsByName =
            source.Skills.ToDictionary(skill => skill.Name, StringComparer.Ordinal);
        string[] intersections = source.Agents
            .Where(agent => skillsByName.ContainsKey(agent.Name))
            .Select(agent => agent.Name)
            .ToArray();
        string[] unexpectedlySharedBodies = DistinctBodyAgentSkillCollisions
            .Where(name => string.Equals(
                Assert.Single(source.Agents, agent => agent.Name == name).InstructionBody,
                skillsByName[name].InstructionBody,
                StringComparison.Ordinal))
            .ToArray();
        string[] generatedProjectionNames = DistinctBodyAgentSkillCollisions
            .Select(name => $"role-{name}")
            .ToArray();

        Assert.Equal(ExpectedAgentSkillIntersections, intersections);
        Assert.Empty(unexpectedlySharedBodies);
        Assert.DoesNotContain(source.Skills, skill => generatedProjectionNames.Contains(
            skill.Name,
            StringComparer.Ordinal));
        Assert.Equal(ExpectedSkills, source.Skills.Select(skill => skill.Name));
    }

    [Fact]
    public void LoadConductorHasOneUnoccupiedFallbackIdentity()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);

        Assert.Single(source.Agents, agent => agent.Name == "conductor");
        Assert.DoesNotContain(source.Skills, skill => skill.Name == "conductor");
        // conductor is unoccupied; the declared shared identities are the two root wrappers,
        // each of which is a primary agent reusing its canonical skill as the entry point.
        Assert.Equal(
            ExpectedSharedIdentities,
            source.FallbackProfiles.Profiles["role-skill"].SharedIdentities.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void LoadUnifiedOrchestrationStackContainsNoVersionedIdentityOrAlias()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        (string Alias, string Canonical)[] aliases = source.Agents
            .SelectMany(agent => agent.Aliases.Select(alias => (Alias: alias, Canonical: agent.Name)))
            .OrderBy(pair => pair.Alias, StringComparer.Ordinal)
            .ToArray();
        string[] versionedDelegates = source.Agents
            .SelectMany(agent => agent.DelegatesTo)
            .Where(IsVersionedIdentity)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string migrationRoot = Path.Combine(ProductRoot, "migration");

        Assert.Empty(aliases);
        Assert.DoesNotContain(source.Agents, agent => IsVersionedIdentity(agent.Name));
        Assert.DoesNotContain(source.Bundle.AgentNames, IsVersionedIdentity);
        Assert.Empty(versionedDelegates);
        Assert.DoesNotContain("test-first-orchestration", source.ModelProfiles.Profiles.Keys);
        Assert.All(
            RetiredAgentIdentities,
            name => Assert.False(
                File.Exists(Path.Combine(migrationRoot, $"{name}.md")),
                $"Retired identity '{name}' must not retain an active migration report."));
    }

    [Fact]
    public void LoadCanonicalAgentBodiesHaveNoDuplicateDigest()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        string[] duplicateBodies = source.Agents
            .GroupBy(agent => agent.BodyDigest, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key}: {string.Join(", ", group.Select(agent => agent.Name))}")
            .ToArray();

        Assert.True(
            duplicateBodies.Length == 0,
            $"Canonical agents must not share an instruction body digest: {string.Join("; ", duplicateBodies)}");
    }

    [Fact]
    public void LoadUnifiedOrchestrationStackDeclaresCanonicalDelegationAndModelProfiles()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        string[] defaultAgents = source.Agents
            .Where(agent => agent.Description.Contains(
                DefaultOrchestrationTrigger,
                StringComparison.OrdinalIgnoreCase))
            .Select(agent => agent.Name)
            .ToArray();
        SquadAgent conductor = Assert.Single(source.Agents, agent => agent.Name == "conductor");
        SquadAgent architect = Assert.Single(source.Agents, agent => agent.Name == "architect");

        Assert.Equal(["conductor"], defaultAgents);
        Assert.Equal("orchestration", conductor.ModelProfile);
        Assert.Equal("architect", architect.ModelProfile);
        Assert.Contains("architect", conductor.DelegatesTo);
        Assert.Contains("product-owner", conductor.DelegatesTo);
        Assert.DoesNotContain("task-reviewer", conductor.DelegatesTo);
        Assert.Contains("code-reviewer", conductor.DelegatesTo);
        Assert.Contains("docs-dev", conductor.DelegatesTo);
    }

    [Fact]
    public void LoadConductorDefinesThreeIntakePathsAndModeAwareExecution()
    {
        string contract = ReadAgentContract("conductor");

        Assert.Contains("plan path", contract, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("spec path", contract, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("intake path", contract, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("development-mode", contract, StringComparison.Ordinal);
        Assert.Contains("test-first", contract, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("standard", contract, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("approve and execute", contract, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Ready", contract, StringComparison.Ordinal);
        Assert.Contains("Draft", contract, StringComparison.Ordinal);
        Assert.Contains("superseded", contract, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoadConductorDefinesIterationCircuitBreakerAndOscillationDetection()
    {
        string contract = ReadAgentContract("conductor");

        Assert.Contains("circuit-breaker", contract, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("loop detection", contract, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("failure cluster", contract, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("oscillation", contract, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Maximum of 2 rework dispatches", contract, StringComparison.Ordinal);
        Assert.Contains("ITERATION_CAP_EXCEEDED", contract, StringComparison.Ordinal);
        Assert.Contains("THRASH_OSCILLATION_DETECTED", contract, StringComparison.Ordinal);
        Assert.Contains("INVARIANT_CONTRADICTION", contract, StringComparison.Ordinal);
        Assert.Contains("BLAST_RADIUS_EXCEEDED", contract, StringComparison.Ordinal);
        Assert.Contains("ESCALATION: circuit-breaker", contract, StringComparison.Ordinal);
        Assert.Contains("RECOMMENDED_ACTION", contract, StringComparison.Ordinal);
        AssertClosedCircuitBreakerTriggerSet(contract);
    }

    [Theory]
    [InlineData("csharp-dev")]
    [InlineData("test-dev")]
    [InlineData("github-devops")]
    public void LoadDeveloperAgentsDefineJevCheckpointsAndIterationCircuitBreaker(string agentName)
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadAgent agent = Assert.Single(source.Agents, a => a.Name == agentName);

        Assert.Contains("Maximum of 3 incremental", agent.InstructionBody, StringComparison.Ordinal);
        Assert.Contains("JEV", agent.InstructionBody, StringComparison.Ordinal);
        Assert.Contains("circuit breaker", agent.InstructionBody, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("STATUS: ESCALATION", agent.InstructionBody, StringComparison.Ordinal);
        Assert.Contains("CIRCUIT_BREAKER_TRIGGER", agent.InstructionBody, StringComparison.Ordinal);
        Assert.Contains("ITERATION_CAP_EXCEEDED", agent.InstructionBody, StringComparison.Ordinal);
        Assert.Contains("THRASH_OSCILLATION_DETECTED", agent.InstructionBody, StringComparison.Ordinal);
        Assert.Contains("INVARIANT_CONTRADICTION", agent.InstructionBody, StringComparison.Ordinal);
        Assert.Contains("BLAST_RADIUS_EXCEEDED", agent.InstructionBody, StringComparison.Ordinal);
        Assert.Contains("FAILURE_CLUSTER", agent.InstructionBody, StringComparison.Ordinal);
        Assert.Contains("CONTRADICTORY_INVARIANTS", agent.InstructionBody, StringComparison.Ordinal);
        Assert.Contains("BLAST_RADIUS", agent.InstructionBody, StringComparison.Ordinal);
        Assert.Contains("RECOMMENDED_ACTION", agent.InstructionBody, StringComparison.Ordinal);
        AssertClosedCircuitBreakerTriggerSet(agent.InstructionBody);
    }

    [Theory]
    [InlineData("architect")]
    [InlineData("bug-crusher-investigator")]
    [InlineData("code-reviewer")]
    [InlineData("csharp-dev")]
    [InlineData("dal-dev")]
    [InlineData("docs-dev")]
    [InlineData("github-devops")]
    [InlineData("go-dev")]
    [InlineData("maui-dev")]
    [InlineData("pulumi-dev")]
    [InlineData("python-dev")]
    [InlineData("react-dev")]
    [InlineData("review-lens")]
    [InlineData("review-triage")]
    [InlineData("rust-dev")]
    [InlineData("sql-database-architect")]
    [InlineData("tauri-dev")]
    [InlineData("test-dev")]
    [InlineData("typescript-dev")]
    public void LoadCodeReadingAgentsDirectIndexedDiscoveryBeforeRawReads(string agentName)
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadAgent agent = Assert.Single(source.Agents, a => a.Name == agentName);

        AssertDiscoveryContract(agent.InstructionBody, agentName);
    }

    /// <summary>
    /// Task sessions are worker-only: the deterministic <c>task-gate</c> is not a session-bearing
    /// agent, so the conductor records and resumes exactly one session per task.
    /// </summary>
    [Fact]
    public void ConductorRetainsOnlyWorkerSessionsForTaskRework()
    {
        string contract = ReadAgentContract("conductor");

        Assert.Contains("original worker session", contract, StringComparison.Ordinal);
        Assert.Contains("worker-session-id", contract, StringComparison.Ordinal);
        Assert.Contains("Resuming a worker counts as a rework dispatch", contract, StringComparison.Ordinal);
        Assert.Contains("unrelated task", contract, StringComparison.Ordinal);
        Assert.Contains("continuation is unavailable", contract, StringComparison.Ordinal);
        Assert.Contains("context is unreliable", contract, StringComparison.Ordinal);
        Assert.DoesNotContain("reviewer-session-id", contract, StringComparison.Ordinal);
        Assert.DoesNotContain("Every worker invocation is cold", contract, StringComparison.Ordinal);
        Assert.DoesNotContain("any available worker", contract, StringComparison.Ordinal);
    }

    /// <summary>
    /// A failing <c>TASK_GATE</c> digest is rejected back to the original worker as a rework item. The
    /// retired per-task reviewer's three-pass audit must not survive as a second routing path.
    /// </summary>
    [Fact]
    public void ConductorRejectsTaskGateFailuresBackToTheWorkerInsteadOfThreePassAudit()
    {
        string contract = ReadAgentContract("conductor");

        Assert.Contains("TASK_GATE", contract, StringComparison.Ordinal);
        Assert.Contains("reject", contract, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("task-gate/", contract, StringComparison.Ordinal);
        Assert.DoesNotContain("Three-pass task audit", contract, StringComparison.Ordinal);
        Assert.DoesNotContain("task-reviewer", contract, StringComparison.Ordinal);
    }

    [Fact]
    public void ConductorDiagnosesDefectsBeforePromotingExplicitPlanOrSpecIntent()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadAgent conductor = Assert.Single(source.Agents, agent => agent.Name == "conductor");
        string contract = ReadAgentContract("conductor");
        int defect = conductor.InstructionBody.IndexOf("**Defect first**", StringComparison.Ordinal);
        int explicitChoice = conductor.InstructionBody.IndexOf("**Explicit plan or spec**", StringComparison.Ordinal);

        Assert.True(defect >= 0 && explicitChoice > defect,
            "Diagnose a concrete failure before routing an explicit artifact choice.");
        Assert.Contains("conductor/references/intake-path.md", conductor.InstructionBody, StringComparison.Ordinal);
        Assert.Contains("Read **Defect diagnosis**", conductor.InstructionBody, StringComparison.Ordinal);
        Assert.Contains("## Defect diagnosis", contract, StringComparison.Ordinal);
        Assert.Contains("STATUS: INTENT_STATED", contract, StringComparison.Ordinal);
        Assert.Contains("STATUS: DEFECT", contract, StringComparison.Ordinal);
        Assert.Contains("CHOICE: PLAN | SPEC | UNSTATED", contract, StringComparison.Ordinal);
        Assert.Contains("existing Ready", contract, StringComparison.Ordinal);
        Assert.Contains("diagnosis is attached", contract, StringComparison.Ordinal);

        string assessment =
            File.ReadAllText(Path.Combine(ProductRoot, "agents", "architect", "references", "intake-assessment.md"));
        Assert.Contains("STATUS: INTENT_STATED", assessment, StringComparison.Ordinal);
        Assert.Contains("STATUS: DEFECT", assessment, StringComparison.Ordinal);
        Assert.DoesNotContain("STATUS: BLOCKED", assessment, StringComparison.Ordinal);
        Assert.Contains("CHOICE: PLAN | SPEC", assessment, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadyTaskDefectsRequiringArchitectureHaveAnExplicitAssessmentHandoff()
    {
        string intake =
            File.ReadAllText(Path.Combine(ProductRoot, "agents", "conductor", "references", "intake-path.md"));
        string assessment =
            File.ReadAllText(Path.Combine(ProductRoot, "agents", "architect", "references", "intake-assessment.md"));
        int ready = intake.IndexOf("For a failure in an existing Ready", StringComparison.Ordinal);
        int noAuthority = intake.IndexOf("Without existing Ready authority", ready, StringComparison.Ordinal);
        string readyRoute = intake[ready..noAuthority];

        Assert.Contains("NEEDS_ARCHITECT", readyRoute, StringComparison.Ordinal);
        Assert.Contains("read-only diagnosed-task assessment", readyRoute, StringComparison.Ordinal);
        Assert.Contains("TASK_ASSESSED", readyRoute, StringComparison.Ordinal);
        Assert.Contains("REVISION_REQUIRED", readyRoute, StringComparison.Ordinal);
        Assert.Contains("original worker", readyRoute, StringComparison.Ordinal);
        Assert.Contains("reopen the applicable approval gate", readyRoute, StringComparison.Ordinal);
        Assert.Contains("STATUS: TASK_ASSESSED", assessment, StringComparison.Ordinal);
        Assert.Contains("CONTRACT: UNCHANGED | REVISION_REQUIRED", assessment, StringComparison.Ordinal);
        Assert.Contains("Do not change the Ready artifact", assessment, StringComparison.Ordinal);
        Assert.Contains("do not ask for a new plan/spec choice", assessment, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("review-lens")]
    [InlineData("review-triage")]
    public void DiscoveryContractRejectsDirectReadPermissionAfterTheNextRoleHeading(string agentName)
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        string body = Assert.Single(source.Agents, agent => agent.Name == agentName).InstructionBody;
        string directReadRule = Assert.Single(body.Split('\n'), line =>
            line.Contains("Read or search a file directly", StringComparison.Ordinal));
        string misplacedRule = body.Replace(directReadRule, string.Empty, StringComparison.Ordinal) + "\n" +
                               directReadRule;

        Assert.NotNull(Record.Exception(() => AssertDiscoveryContract(misplacedRule, agentName)));
    }

    [Fact]
    public void ProductOwnerReportsDiscoveryInItsStructuredDigest()
    {
        string skill = File.ReadAllText(Path.Combine(ProductRoot, "skills", "product-owner", "SKILL.md"));
        AssertDiscoveryContract(skill, "product-owner");
        Assert.Contains("\nDiscovery: codegraph N, docs N, direct reads N\n", skill, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadDocsDevDefinesCloseoutMappingTableAndWaiverConventions()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadAgent docsDev = Assert.Single(source.Agents, a => a.Name == "docs-dev");

        Assert.Contains("Plan closeout", docsDev.InstructionBody, StringComparison.Ordinal);
        Assert.Contains("mapping table", docsDev.InstructionBody, StringComparison.Ordinal);
        Assert.Contains("ADR waiver", docsDev.InstructionBody, StringComparison.Ordinal);
        Assert.Contains("Documentation waiver", docsDev.InstructionBody, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadArchitectAndConductorDefinePermanentDecisionTableContract()
    {
        string architectContract = ReadAgentContract("architect");
        string conductorContract = ReadAgentContract("conductor");

        Assert.Contains("Decisions", architectContract, StringComparison.Ordinal);
        Assert.Contains("Mitigating and supporting information", architectContract, StringComparison.Ordinal);
        Assert.Contains("Status / approval provenance", architectContract, StringComparison.Ordinal);
        Assert.Contains("NO_QUESTIONS", architectContract, StringComparison.Ordinal);

        Assert.Contains("Decisions", conductorContract, StringComparison.Ordinal);
        Assert.Contains("PLAN_READY", conductorContract, StringComparison.Ordinal);
        Assert.Contains("PLAN_FINALIZED", conductorContract, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadProductOwnerIsHeadlessAndReturnsStructuredPhaseAndGapMarkers()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadAgent productOwner = Assert.Single(source.Agents, agent => agent.Name == "product-owner");
        string skillContract = ReadSkillContract("product-owner");

        Assert.DoesNotContain("ask, verbatim", productOwner.InstructionBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GATE 1", productOwner.InstructionBody, StringComparison.Ordinal);
        // The root agent is a thin wrapper: the role contract lives in the canonical skill, and
        // the agent body only points at it. Both facts are pinned so the wrapper cannot quietly
        // grow a second copy of the role.
        Assert.Contains("Load the `product-owner` skill", productOwner.InstructionBody, StringComparison.Ordinal);
        Assert.DoesNotContain("STATUS: PHASE_APPROVED", productOwner.InstructionBody, StringComparison.Ordinal);
        Assert.Empty(productOwner.Resources);
        SquadSkill productOwnerSkill = Assert.Single(source.Skills, skill => skill.Name == "product-owner");
        SquadResource specAuthoring = Assert.Single(
            productOwnerSkill.Resources,
            resource => resource.RelativePath == "references/spec-authoring.md");
        Assert.Contains(
            "initialize the file via shell (for example, `touch <file>`) before editing.",
            specAuthoring.Content,
            StringComparison.Ordinal);
        Assert.Contains("STATUS: READY_FOR_REVIEW", skillContract, StringComparison.Ordinal);
        Assert.Contains("STATUS: REQUIREMENTS_GAP", skillContract, StringComparison.Ordinal);
        Assert.Contains("STATUS: DESIGN_GAP", skillContract, StringComparison.Ordinal);
        Assert.Contains("GAPS:", skillContract, StringComparison.Ordinal);
        Assert.Contains("OPEN_QUESTIONS:", skillContract, StringComparison.Ordinal);
    }

    /// <summary>
    /// Pins the published schema enum, the loader vocabulary, and the
    /// canonical <c>target:</c> markers to one set so a <c>const: copilot</c>
    /// schema, a fourth or typo target in capabilities.yml, or a stale
    /// schema entry fails closed. A non-string enum entry fails naming
    /// its <c>ValueKind</c> and raw text. Membership is compared as
    /// sets: enum order is not a published contract.
    /// </summary>
    [Fact]
    public void CapabilityProfilesSchemaAllowsTheTargetScopedVocabulary()
    {
        string schemaPath = Path.Combine(ProductRoot, "schemas", "capability-profiles.schema.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(schemaPath));
        JsonElement target = document.RootElement
            .GetProperty("properties")
            .GetProperty("profiles")
            .GetProperty("additionalProperties")
            .GetProperty("properties")
            .GetProperty("target");

        Assert.False(target.TryGetProperty("const", out _));
        JsonElement allowed = target.GetProperty("enum");
        string[] schemaTargets =
        [
            .. allowed.EnumerateArray().Select(item =>
                item.ValueKind == JsonValueKind.String
                    ? item.GetString()!
                    : throw new InvalidOperationException(
                        $"Capability-profile schema enum entry {item.ValueKind} '{item.GetRawText()}' is not a string."))
        ];
        Assert.Equal(
            SquadSourceLoader.TargetScopedProfileTargets.Order(StringComparer.Ordinal),
            schemaTargets.Order(StringComparer.Ordinal));

        string[] declaredTargets = ReadCapabilityProfileTargets();
        Assert.All(
            declaredTargets,
            name => Assert.Contains(name, SquadSourceLoader.TargetScopedProfileTargets));
        Assert.All(
            SquadSourceLoader.TargetScopedProfileTargets,
            name => Assert.Contains(name, declaredTargets));
    }

    /// <summary>
    /// Developer agents close every task by reporting the <c>task-gate</c> digest. A developer body
    /// that does not name the gate would silently skip the exit check.
    /// </summary>
    [Theory]
    [InlineData("csharp-dev")]
    [InlineData("dal-dev")]
    [InlineData("github-devops")]
    [InlineData("go-dev")]
    [InlineData("maui-dev")]
    [InlineData("pulumi-dev")]
    [InlineData("python-dev")]
    [InlineData("react-dev")]
    [InlineData("rust-dev")]
    [InlineData("sql-database-architect")]
    [InlineData("tauri-dev")]
    [InlineData("test-dev")]
    [InlineData("typescript-dev")]
    public void DeveloperAgentBodyNamesTaskGateAndEmitsDigestLine(string agentName)
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadAgent agent = Assert.Single(source.Agents, a => a.Name == agentName);

        Assert.Contains("task-gate", agent.InstructionBody, StringComparison.Ordinal);
        Assert.Contains("TASK_GATE:", agent.InstructionBody, StringComparison.Ordinal);
    }

    /// <summary>
    /// The <c>TASK_GATE:</c> digest is a developer exit contract. A non-developer body that emits it
    /// would claim a gate it never ran.
    /// </summary>
    [Fact]
    public void NonDeveloperAgentBodiesNeverEmitTaskGateDigest()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        string[] offenders = source.Agents
            .Where(agent => !DeveloperGateAgents.Contains(agent.Name, StringComparer.Ordinal))
            .Where(agent => agent.InstructionBody.Contains("TASK_GATE:", StringComparison.Ordinal))
            .Select(agent => agent.Name)
            .ToArray();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// The skill is the deterministic exit gate itself: it runs review gates through the CLI, applies
    /// fixes, caps the cycle, and reports a closed <c>TASK_GATE:</c> digest.
    /// </summary>
    [Fact]
    public void TaskGateSkillDefinesTheDeterministicReviewGateContract()
    {
        string path = Path.Combine(ProductRoot, "skills", "task-gate", "SKILL.md");
        Assert.True(File.Exists(path), $"Canonical task-gate skill is missing: {path}");
        string skill = File.ReadAllText(path);

        Assert.Contains("review gates", skill, StringComparison.Ordinal);
        Assert.Contains("--agent", skill, StringComparison.Ordinal);
        Assert.Contains("--fix", skill, StringComparison.Ordinal);
        Assert.Contains("2-cycle cap", skill, StringComparison.Ordinal);
        Assert.Contains("ITERATION_CAP_EXCEEDED", skill, StringComparison.Ordinal);
        Assert.Contains("task-gate/<gate-id>", skill, StringComparison.Ordinal);
        Assert.Contains("TASK_GATE: PASS | FAIL | NONE_DECLARED | NONE_APPLICABLE | UNAVAILABLE", skill, StringComparison.Ordinal);
        Assert.Contains("every declared gate was skipped", skill, StringComparison.Ordinal);
        AssertClosedCircuitBreakerTriggerSet(skill);
    }

    /// <summary>
    /// The per-task reviewer is retired outright. Its agent, its reference folder, and its migration
    /// record must not survive in the squad source, or the orchestrator can still route to it.
    /// </summary>
    [Fact]
    public void TaskReviewerAgentReferenceFolderAndMigrationRecordAreRemoved()
    {
        string agentsRoot = Path.Combine(ProductRoot, "agents");

        Assert.False(
            File.Exists(Path.Combine(agentsRoot, "task-reviewer.md")),
            "The retired task-reviewer agent body must be deleted.");
        Assert.False(
            Directory.Exists(Path.Combine(agentsRoot, "task-reviewer")),
            "The retired task-reviewer reference folder must be deleted.");
        Assert.False(
            File.Exists(Path.Combine(ProductRoot, "migration", "task-reviewer.md")),
            "The retired task-reviewer migration record must be deleted.");
    }

    /// <summary>
    /// Scans every squad source entry, by name and by content, so no surface (bundle, README,
    /// sibling skill, or reviewer array) keeps routing to the retired reviewer.
    /// </summary>
    [Fact]
    public void NoSquadSourceEntryNamesOrQuotesRetiredTaskReviewer()
    {
        string[] nameOffenders = Directory.EnumerateFileSystemEntries(ProductRoot, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path).Contains("task-reviewer", StringComparison.Ordinal))
            .Select(RelativeSourcePath)
            .ToArray();
        string[] textOffenders = Directory.EnumerateFiles(ProductRoot, "*", SearchOption.AllDirectories)
            .Where(path => File.ReadAllText(path).Contains("task-reviewer", StringComparison.Ordinal))
            .Select(RelativeSourcePath)
            .ToArray();

        Assert.Empty(nameOffenders.Concat(textOffenders).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void CodeReviewerBodyAndArchitectPlanAuthoringDoNotNameRetiredTaskReviewer()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadAgent codeReviewer = Assert.Single(source.Agents, agent => agent.Name == "code-reviewer");
        string planAuthoring =
            File.ReadAllText(Path.Combine(ProductRoot, "agents", "architect", "references", "plan-authoring.md"));

        Assert.False(
            codeReviewer.InstructionBody.Contains("task-reviewer", StringComparison.Ordinal),
            "The code-reviewer body must not name the retired task-reviewer.");
        Assert.False(
            planAuthoring.Contains("task-reviewer", StringComparison.Ordinal),
            "The architect plan-authoring reference must not name the retired task-reviewer.");
    }

    private static string RelativeSourcePath(string path) =>
        Path.GetRelativePath(ProductRoot, path).Replace(Path.DirectorySeparatorChar, '/');

    [Fact]
    public void LoadMigrationReportsDeclareLockedSourcesAndActualCanonicalDigest()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        string migrationRoot = Path.Combine(ProductRoot, "migration");
        Assert.True(
            Directory.Exists(migrationRoot),
            $"K2 canonical migration directory is missing: {migrationRoot}");

        string?[] reportNames = Directory.EnumerateFiles(migrationRoot, "*.md", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileNameWithoutExtension)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(ExpectedMigratedAgents, reportNames);

        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> expectedSources = ParseExpectedSourceHashes();
        foreach (SquadAgent agent in source.Agents.Where(a =>
                     ExpectedMigratedAgents.Contains(a.Name, StringComparer.Ordinal)))
        {
            string reportPath = Path.Combine(migrationRoot, $"{agent.Name}.md");
            MigrationReport report = ReadMigrationReport(reportPath);

            Assert.Equal(MigrationSchema, report.Schema);
            Assert.Equal(agent.Name, report.Agent);
            Assert.Equal(MigrationSourceCommit, report.SourceCommit);
            Assert.Equal(ExpectedBaselines[agent.Name], report.SelectedBaseline);
            Assert.Equal(
                expectedSources[agent.Name]
                    .Select(pair => $"{pair.Key}={pair.Value}")
                    .Order(StringComparer.Ordinal),
                report.SourceHashes
                    .Select(pair => $"{pair.Key}={pair.Value}")
                    .Order(StringComparer.Ordinal));
            Assert.Equal(agent.BodyDigest, report.FinalBodySha256);
        }
    }

    /// <summary>
    /// The migration directory records one specific event: reconciling the live harness
    /// copies at the locked source commit. An agent written after that has nothing to
    /// reconcile, and a record claiming otherwise would be false in exactly the place
    /// falsehood is least detectable.
    /// </summary>
    [Fact]
    public void LoadNetNewAgentsCarryNoFabricatedMigrationRecord()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        string migrationRoot = Path.Combine(ProductRoot, "migration");

        Assert.Equal(
            ExpectedNewAgents,
            source.Agents
                .Select(agent => agent.Name)
                .Where(name => !ExpectedMigratedAgents.Contains(name, StringComparer.Ordinal))
                .ToArray());

        Assert.All(
            ExpectedNewAgents,
            name => Assert.False(
                File.Exists(Path.Combine(migrationRoot, $"{name}.md")),
                $"'{name}' was authored after the migration and must not claim a migration baseline."));
    }

    /// <summary>
    /// <c>code-review</c> carries the pre-push self-review trigger and gate. Nothing else
    /// pins this section, so deleting this test returns it to no coverage.
    /// </summary>
    [Fact]
    public void CodeReviewSkillRetainsSelfReviewTriggerAndGate()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadSkill? skill = source.Skills.FirstOrDefault(candidate => candidate.Name == "code-review");

        Assert.NotNull(skill);
        Assert.Contains("self-review gate before pushing", skill.Description, StringComparison.Ordinal);
        Assert.Contains("## Pre-push self-review gate", skill.InstructionBody, StringComparison.Ordinal);
        Assert.Contains("major-or-worse", skill.InstructionBody, StringComparison.Ordinal);
    }

    private static string ProductRoot =>
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");

    private static bool IsVersionedIdentity(string name)
    {
        return name.EndsWith("-v2", StringComparison.Ordinal) ||
               name.EndsWith("-v3", StringComparison.Ordinal);
    }

    private static void AssertClosedCircuitBreakerTriggerSet(string contract)
    {
        string[] harvested = CircuitBreakerTriggerEnumeration.Matches(contract)
            .SelectMany(match => CircuitBreakerTriggerToken.Matches(match.Groups["list"].Value)
                .Select(token => token.Value))
            .Concat(CircuitBreakerTriggerSingleton.Matches(contract)
                .Select(match => match.Groups["token"].Value))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(CircuitBreakerTriggerTokens, harvested);
    }

    private static void AssertDiscoveryContract(string body, string agentName)
    {
        int heading = body.IndexOf("## Discovery order", StringComparison.Ordinal);
        Assert.True(heading >= 0, $"{agentName}: missing Discovery order section.");
        int nextHeading = body.IndexOf("\n#", heading + 1, StringComparison.Ordinal);
        string discovery = nextHeading < 0 ? body[heading..] : body[heading..nextHeading];
        int code = discovery.IndexOf("codegraph_explore", StringComparison.Ordinal);
        int docs = discovery.IndexOf("docs_explore", StringComparison.Ordinal);
        int directRead = discovery.IndexOf("Read or search a file directly", StringComparison.Ordinal);

        Assert.True(code >= 0 && docs >= 0 && directRead > code && directRead > docs,
            $"{agentName}: index discovery must precede direct-read authorization.");
        Assert.Contains("docs_for_symbol", discovery, StringComparison.Ordinal);
        Assert.Contains("docs_glossary", discovery, StringComparison.Ordinal);
        if (agentName is "review-lens" or "review-triage")
        {
            Assert.Contains("Discovery reporting is exempt", discovery, StringComparison.Ordinal);
            Assert.Contains("machine-consumed", discovery, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("Discovery: codegraph N, docs N, direct reads N", discovery, StringComparison.Ordinal);
            Assert.Contains("reason for each direct read", discovery, StringComparison.Ordinal);
        }
    }

    private static string ReadAgentContract(string name)
    {
        string agentRoot = Path.Combine(ProductRoot, "agents");
        string principal = File.ReadAllText(Path.Combine(agentRoot, $"{name}.md"));
        string referencesRoot = Path.Combine(agentRoot, name, "references");
        Assert.True(
            Directory.Exists(referencesRoot),
            $"Agent '{name}' must progressively disclose its conditional contract from '{referencesRoot}'.");

        string[] references = Directory.EnumerateFiles(referencesRoot, "*.md", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.NotEmpty(references);
        Assert.All(
            references,
            path => Assert.Contains(
                Path.GetRelativePath(agentRoot, path).Replace(Path.DirectorySeparatorChar, '/'),
                principal,
                StringComparison.Ordinal));

        return string.Join("\n", references.Prepend(Path.Combine(agentRoot, $"{name}.md")).Select(File.ReadAllText));
    }

    private static string[] ReadCapabilityProfileTargets()
    {
        string path = Path.Combine(ProductRoot, "profiles", "capabilities.yml");
        YamlStream yaml = new();
        yaml.Load(new StringReader(File.ReadAllText(path)));
        YamlMappingNode root = RequireMapping(yaml.Documents.Single().RootNode, "root", path);
        YamlMappingNode profiles = RequireMapping(RequireNode(root, "profiles", path), "profiles", path);

        return profiles.Children
            .Select(pair => RequireMapping(pair.Value, "profile", path))
            .Select(profile =>
            {
                foreach (KeyValuePair<YamlNode, YamlNode> field in profile.Children)
                {
                    if (field.Key is YamlScalarNode { Value: "target" })
                    {
                        return RequireScalar(field.Value, "target", path);
                    }
                }

                return null;
            })
            .OfType<string>()
            .ToArray();
    }

    private static string ReadSkillContract(string name)
    {
        string skillRoot = Path.Combine(ProductRoot, "skills", name);
        return string.Join(
            "\n",
            Directory.EnumerateFiles(skillRoot, "*.md", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal)
                .Select(File.ReadAllText));
    }

    private static RoleSkillProfile ReadRoleSkillProfile()
    {
        string path = Path.Combine(ProductRoot, "profiles", "fallbacks.yml");
        YamlStream yaml = new();
        yaml.Load(new StringReader(File.ReadAllText(path)));
        YamlMappingNode root = RequireMapping(yaml.Documents.Single().RootNode, "root", path);
        YamlMappingNode profiles = RequireMapping(RequireNode(root, "profiles", path), "profiles", path);
        YamlMappingNode roleSkill = RequireMapping(RequireNode(profiles, "role-skill", path), "role-skill", path);
        YamlMappingNode outputIdentity = RequireMapping(
            RequireNode(roleSkill, "output-identity", path),
            "output-identity",
            path);
        YamlSequenceNode sharedIdentities = RequireSequence(
            RequireNode(roleSkill, "shared-identities", path),
            "shared-identities",
            path);

        return new RoleSkillProfile(
            RequireScalar(roleSkill, "no-primary-agent", path),
            RequireScalar(roleSkill, "no-agent-primitive", path),
            RequireScalar(roleSkill, "body-source", path),
            RequireScalar(outputIdentity, "unoccupied", path),
            RequireScalar(outputIdentity, "shared", path),
            RequireScalar(outputIdentity, "collision", path),
            RequireScalar(outputIdentity, "prefix", path),
            sharedIdentities.Children
                .Select(node => RequireScalar(node, "shared identity", path))
                .ToArray());
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ParseExpectedSourceHashes()
    {
        IEnumerable<(string Agent, string Path, string Hash)> entries = ExpectedSourceHashManifest
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('|'))
            .Select(parts => (
                Agent: SourceAgentName(parts[0]),
                Path: parts[0],
                Hash: parts[1]
            ));

        return entries
            .GroupBy(entry => entry.Agent, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyDictionary<string, string>)group.ToDictionary(
                    entry => entry.Path,
                    entry => entry.Hash,
                    StringComparer.Ordinal),
                StringComparer.Ordinal);
    }

    private static string SourceAgentName(string sourcePath)
    {
        string fileName = Path.GetFileName(sourcePath);
        string name = fileName.EndsWith(".agent.md", StringComparison.Ordinal)
            ? fileName[..^".agent.md".Length]
            : Path.GetFileNameWithoutExtension(fileName);
        return name switch
        {
            "conductor-v2" => "conductor",
            "architect-v3" => "architect",
            "conductor-v3" => "conductor",
            "dotnet-dev" => "csharp-dev",
            _ => name
        };
    }

    private static MigrationReport ReadMigrationReport(string path)
    {
        string content = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!content.StartsWith("---\n", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Migration report '{path}' has no YAML frontmatter.");
        }

        int end = content.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (end < 0)
        {
            throw new InvalidDataException($"Migration report '{path}' has unterminated YAML frontmatter.");
        }

        YamlStream yaml = new();
        yaml.Load(new StringReader(content[4..end]));
        YamlMappingNode root = RequireMapping(yaml.Documents.Single().RootNode, "frontmatter", path);
        Dictionary<string, string> sources = RequireMapping(RequireNode(root, "sources", path), "sources", path)
            .Children
            .ToDictionary(
                pair => RequireScalar(pair.Key, "source path", path),
                pair => RequireScalar(pair.Value, "source hash", path),
                StringComparer.Ordinal);

        return new MigrationReport(
            RequireScalar(root, "schema", path),
            RequireScalar(root, "agent", path),
            RequireScalar(root, "source-commit", path),
            RequireScalar(root, "selected-baseline", path),
            sources,
            RequireScalar(root, "final-body-sha256", path));
    }

    private static YamlNode RequireNode(YamlMappingNode mapping, string key, string path)
    {
        foreach (KeyValuePair<YamlNode, YamlNode> pair in mapping.Children)
        {
            if (pair.Key is YamlScalarNode scalar &&
                string.Equals(scalar.Value, key, StringComparison.Ordinal))
            {
                return pair.Value;
            }
        }

        throw new InvalidDataException($"YAML document '{path}' is missing '{key}'.");
    }

    private static YamlMappingNode RequireMapping(YamlNode node, string field, string path)
    {
        return node as YamlMappingNode ??
               throw new InvalidDataException($"YAML document '{path}' field '{field}' must be a mapping.");
    }

    private static YamlSequenceNode RequireSequence(YamlNode node, string field, string path)
    {
        return node as YamlSequenceNode ??
               throw new InvalidDataException($"YAML document '{path}' field '{field}' must be a sequence.");
    }

    private static string RequireScalar(YamlMappingNode mapping, string key, string path)
    {
        return RequireScalar(RequireNode(mapping, key, path), key, path);
    }

    private static string RequireScalar(YamlNode node, string field, string path)
    {
        return node is YamlScalarNode { Value: not null } scalar
            ? scalar.Value
            : throw new InvalidDataException($"YAML document '{path}' field '{field}' must be a scalar.");
    }

    private sealed record RoleSkillProfile(
        string NoPrimaryAgent,
        string NoAgentPrimitive,
        string BodySource,
        string UnoccupiedIdentity,
        string SharedIdentity,
        string CollisionIdentity,
        string Prefix,
        IReadOnlyList<string> SharedIdentities);

    private sealed record MigrationReport(
        string Schema,
        string Agent,
        string SourceCommit,
        string SelectedBaseline,
        IReadOnlyDictionary<string, string> SourceHashes,
        string FinalBodySha256);

    private const string ExpectedSourceHashManifest = """
                                                      .github/agents/architect.agent.md|16afd840a7d528b941e86e4c9c3e050054a8655a423234c0ab484091373bc58c
                                                      .github/agents/architect-v3.agent.md|5eb0ce098c11a54c9f78f75daaf517d332fedda97f5d9431e6c968b2a9558fcb
                                                      .github/agents/azure-reader.agent.md|f4f8bf0fe71adc3933808dccf4112a5fa49154069a2ed0977cc92b0d005382ca
                                                      .github/agents/bug-crusher-investigator.agent.md|e0f68b7e28c9153de337dbb5daba0c756a192714c8906bc5aeb379d54553dbb2
                                                      .github/agents/code-reviewer.agent.md|98440b62230139ae74e4f1a54d15482db338ab34bf912d98afbc629b249ffd14
                                                      .github/agents/conductor.agent.md|fe8fa326b83780cafb4a9b8aba06312ddb106eb732de1f7a64f3a88b8fe773c6
                                                      .github/agents/conductor-v3.agent.md|0392bd1e3dd09ab67f45801cb39aac7d016b5ed43733ee2ae65ac6c5668d7204
                                                      .github/agents/csharp-dev.agent.md|09cd65841fbb82e81060cbbccdc91c6aac7ee209a84b97e30799cc3281cbe146
                                                      .github/agents/dal-dev.agent.md|efbd8ed5a6f2f8342a184cf57cba08c3649dba0e736e980257101a1056f3079a
                                                      .github/agents/docs-dev.agent.md|db63b4d3b09d0d352c5381feb6821e3fb982acdf49ad44f3b5d33861343410b0
                                                      .github/agents/github-devops.agent.md|52ed73aca2f79ad7f57ccaad94299fa96827f34c5e97133728b037aa3a6d2593
                                                      .github/agents/maui-dev.agent.md|d275ca8508a007832e6c00bb77f38735f1f849ac4bd8eb7a554ee1b05e371a2f
                                                      .github/agents/product-owner.agent.md|5193d5e0bdabec19ceb33a646ab1120362e8705dec1bfe5da7af23a361c5d76e
                                                      .github/agents/pulumi-dev.agent.md|968b2999eb8f998b8fc9863389ddca445661c8c444cecf079b3257dac9ae0040
                                                      .github/agents/python-dev.agent.md|f058efe4c9f5bd07ff6d908f539265eb5219a59806ac7db8b1be42525eefa944
                                                      .github/agents/react-dev.agent.md|8c34dadd97cd379d7e7b03c172f297c89ba88d9a540c4d1f0ce43d4cb6018fdd
                                                      .github/agents/research-agent.agent.md|e0f94800f1c177fec00ff70f977263a61e14b3af1820225b7f7d99f60f66a548
                                                      .github/agents/sql-database-architect.agent.md|7310fbcad5e1d87b90c9c1d95a031931a028f9a486f8235e52347721e8f74ee6
                                                      .github/agents/tauri-dev.agent.md|30f08182e6c42869088f2df5cc36aa6162182a97fa6576342b9ca2fff47051ad
                                                      .github/agents/test-dev.agent.md|b461f218e0645748eb5beec7675c3d5908191d6facf2cb19ac0338fdc638e548
                                                      """;
}
