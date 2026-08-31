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

    private static readonly string[] RetiredAgentIdentities =
    [
        "architect-v3",
        "conductor-v3",
        "task-reviewer-v3"
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
        "task-reviewer",
        "tauri-dev",
        "test-dev",
    ];

    /// <summary>
    /// Review-council workers are canonical-only roles and are deliberately excluded from
    /// Hotshot migration provenance.
    /// </summary>
    private static readonly string[] ExpectedNewAgents = ["review-lens", "review-triage"];

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
        "create-pull-request",
        "create-pull-request-github",
        "csharp-dev",
        "csp-security",
        "dal-dev",
        "dp-code-reviewer",
        "github-cli",
        "github-devops",
        "lm-studio-cli",
        "maui-dev",
        "pr-review-fix-comments",
        "product-owner",
        "python-dev",
        "resharper-clt",
        "second-brain",
        "security-review",
        "setup-dev-environment",
        "test-dev"
    ];

    private static readonly string[] ExpectedAgentSkillIntersections =
    [
        "csharp-dev",
        "dal-dev",
        "github-devops",
        "maui-dev",
        "product-owner",
        "python-dev",
        "test-dev"
    ];

    private static readonly string[] DistinctBodyAgentSkillCollisions =
    [
        "csharp-dev",
        "dal-dev",
        "github-devops",
        "maui-dev",
        "product-owner",
        "python-dev",
        "test-dev"
    ];

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
        Assert.Empty(profile.SharedIdentities);
    }

    [Fact]
    public void LoadAgentSkillIntersectionHasPinnedSharedAndPrefixedFallbackIdentities()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        Dictionary<string, SquadSkill> skillsByName = source.Skills.ToDictionary(skill => skill.Name, StringComparer.Ordinal);
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
        Assert.Empty(source.FallbackProfiles.Profiles["role-skill"].SharedIdentities);
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
        SquadAgent taskReviewer = Assert.Single(source.Agents, agent => agent.Name == "task-reviewer");

        Assert.Equal(["conductor"], defaultAgents);
        Assert.Equal("orchestration", conductor.ModelProfile);
        Assert.Equal("deep-planning", architect.ModelProfile);
        Assert.Equal("mai-code-flash", taskReviewer.ModelProfile);
        Assert.Contains("architect", conductor.DelegatesTo);
        Assert.Contains("product-owner", conductor.DelegatesTo);
        Assert.Contains("task-reviewer", conductor.DelegatesTo);
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
    public void LoadProductOwnerIsHeadlessAndReturnsStructuredPhaseAndGapMarkers()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadAgent productOwner = Assert.Single(source.Agents, agent => agent.Name == "product-owner");
        string skillContract = ReadSkillContract("product-owner");

        Assert.DoesNotContain("ask, verbatim", productOwner.InstructionBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GATE 1", productOwner.InstructionBody, StringComparison.Ordinal);
        Assert.Contains("STATUS: READY_FOR_REVIEW", skillContract, StringComparison.Ordinal);
        Assert.Contains("STATUS: REQUIREMENTS_GAP", skillContract, StringComparison.Ordinal);
        Assert.Contains("STATUS: DESIGN_GAP", skillContract, StringComparison.Ordinal);
        Assert.Contains("GAPS:", skillContract, StringComparison.Ordinal);
        Assert.Contains("OPEN_QUESTIONS:", skillContract, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadTaskReviewerDefinesThreePassesForBothDevelopmentModes()
    {
        string contract = ReadAgentContract("task-reviewer");

        Assert.Contains("pass 3", contract, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("test-first", contract, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("standard", contract, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("RED/GREEN", contract, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("verification contract", contract, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("code-reviewer", contract, StringComparison.OrdinalIgnoreCase);
    }

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
        foreach (SquadAgent agent in source.Agents.Where(a => ExpectedMigratedAgents.Contains(a.Name, StringComparer.Ordinal)))
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

    private static string ProductRoot =>
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");

    private static bool IsVersionedIdentity(string name) =>
        name.EndsWith("-v2", StringComparison.Ordinal) ||
        name.EndsWith("-v3", StringComparison.Ordinal);

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
        YamlStream yaml = new YamlStream();
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
            "task-reviewer-v3" => "task-reviewer",
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

        YamlStream yaml = new YamlStream();
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

    private static YamlMappingNode RequireMapping(YamlNode node, string field, string path) =>
        node as YamlMappingNode ??
        throw new InvalidDataException($"YAML document '{path}' field '{field}' must be a mapping.");

    private static YamlSequenceNode RequireSequence(YamlNode node, string field, string path) =>
        node as YamlSequenceNode ??
        throw new InvalidDataException($"YAML document '{path}' field '{field}' must be a sequence.");

    private static string RequireScalar(YamlMappingNode mapping, string key, string path) =>
        RequireScalar(RequireNode(mapping, key, path), key, path);

    private static string RequireScalar(YamlNode node, string field, string path) =>
        node is YamlScalarNode { Value: not null } scalar
            ? scalar.Value
            : throw new InvalidDataException($"YAML document '{path}' field '{field}' must be a scalar.");

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
        .github/agents/task-reviewer.agent.md|e096cb7d1507f7d6f1bc635dbc893d8f41a0289d105bfa5648bb2db5ba74b61c
        .github/agents/task-reviewer-v3.agent.md|27bc894fd3ed5b56172d4210d9f2ff3ac716fe7c8d79ee11a15d1d4c4800a1f8
        .github/agents/tauri-dev.agent.md|30f08182e6c42869088f2df5cc36aa6162182a97fa6576342b9ca2fff47051ad
        .github/agents/test-dev.agent.md|b461f218e0645748eb5beec7675c3d5908191d6facf2cb19ac0338fdc638e548
        """;
}
