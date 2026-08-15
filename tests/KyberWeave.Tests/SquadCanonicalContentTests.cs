using KyberWeave.Core.Squad.Parsing;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace KyberWeave.Tests;

public sealed class SquadCanonicalContentTests
{
    private const string MigrationSchema = "kyber-squad.migration/v1";
    private const string MigrationSourceCommit = "d7547f46ab6bb8e447096345abbe5d4c7840bfc0";
    private const string DefaultOrchestrationTrigger = "default entry point";
    private const string ExplicitOrchestrationTrigger = "explicit";
    private const string TestFirstOrchestrationTrigger = "test-first";

    private static readonly string[] HarnessSpecificConfigurationTokens =
    [
        ".agent/",
        ".claude/",
        ".codex/",
        ".cursor/",
        ".cursorrules",
        ".factory/",
        ".gemini/",
        ".github/agents/",
        ".github/copilot-instructions.md",
        ".kilo/",
        ".kilocode/",
        ".opencode/",
        ".warp/",
        "AGENTS.md",
        "AskUserQuestion",
        "CLAUDE.md",
        "GEMINI.md",
        "SendMessage",
        "TaskCreate",
        "TaskList",
        "TaskUpdate",
        "WARP.md"
    ];

    private static readonly string[] ExpectedAgents =
    [
        "architect",
        "architect-v3",
        "azure-reader",
        "bug-crusher-investigator",
        "code-reviewer",
        "conductor",
        "conductor-v3",
        "dal-dev",
        "docs-dev",
        "dotnet-dev",
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

    private static readonly string[] ExpectedSkills =
    [
        "app-docs-standard",
        "architecture-decision-record",
        "azure-cli",
        "azure-naming",
        "bug-crusher",
        "code-review",
        "conductor",
        "conductor-v3",
        "create-pull-request",
        "create-pull-request-github",
        "csp-security",
        "dal-dev",
        "dotnet-dev",
        "dp-code-reviewer",
        "github-cli",
        "github-devops",
        "lm-studio-cli",
        "maui-dev",
        "pr-review-fix-comments",
        "product-owner",
        "python-dev",
        "second-brain",
        "security-review",
        "setup-dev-environment",
        "test-dev"
    ];

    private static readonly string[] ExpectedAgentSkillIntersections =
    [
        "conductor",
        "conductor-v3",
        "dal-dev",
        "dotnet-dev",
        "github-devops",
        "maui-dev",
        "product-owner",
        "python-dev",
        "test-dev"
    ];

    private static readonly string[] SharedAgentSkillIdentities =
    [
        "conductor",
        "conductor-v3"
    ];

    private static readonly string[] DistinctBodyAgentSkillCollisions =
    [
        "dal-dev",
        "dotnet-dev",
        "github-devops",
        "maui-dev",
        "product-owner",
        "python-dev",
        "test-dev"
    ];

    private static readonly IReadOnlyDictionary<string, string> ExpectedBaselines =
        ExpectedAgents.ToDictionary(
            name => name,
            name => name switch
            {
                "conductor" => ".opencode/agents/conductor-v2.md",
                "conductor-v3" => ".opencode/agents/conductor-v3.md",
                _ => $".claude/agents/{name}.md"
            },
            StringComparer.Ordinal);

    [Fact]
    public void Load_FullProductTree_ContainsExactlyApprovedAgentIdentities()
    {
        var source = SquadSourceLoader.Load(ProductRoot);

        Assert.Equal(ExpectedAgents, source.Agents.Select(agent => agent.Name));
        Assert.Equal(ExpectedAgents, source.Bundle.AgentNames.Order(StringComparer.Ordinal));
        Assert.All(
            source.Agents,
            agent => Assert.Equal($"agents/{agent.Name}.md", agent.SourcePath));
    }

    [Fact]
    public void Load_FullProductTree_ContainsExactlyApprovedSkillIdentities()
    {
        var source = SquadSourceLoader.Load(ProductRoot);

        Assert.Equal(ExpectedSkills, source.Skills.Select(skill => skill.Name));
        Assert.Equal(ExpectedSkills, source.Bundle.SkillNames.Order(StringComparer.Ordinal));
        Assert.All(
            source.Skills,
            skill => Assert.Equal($"skills/{skill.Name}/SKILL.md", skill.SourcePath));
    }

    [Fact]
    public void Load_FullProductTree_ExcludesKyberWeaveDocsSkill()
    {
        var source = SquadSourceLoader.Load(ProductRoot);

        Assert.DoesNotContain(source.Skills, skill => skill.Name == "kyber-weave-docs");
        Assert.False(Directory.Exists(Path.Combine(ProductRoot, "skills", "kyber-weave-docs")));
    }

    [Fact]
    public void Load_RoleSkillFallbackProfile_DeclaresGlobalBodyAndIdentityProjection()
    {
        var profile = ReadRoleSkillProfile();

        Assert.Equal("skill", profile.NoPrimaryAgent);
        Assert.Equal("skill", profile.NoAgentPrimitive);
        Assert.Equal("agent", profile.BodySource);
        Assert.Equal("agent-name", profile.UnoccupiedIdentity);
        Assert.Equal("reuse-skill", profile.SharedIdentity);
        Assert.Equal("role-prefixed-agent-name", profile.CollisionIdentity);
        Assert.Equal("role-", profile.Prefix);
        Assert.Equal(SharedAgentSkillIdentities, profile.SharedIdentities);
    }

    [Fact]
    public void Load_AgentSkillIntersection_HasPinnedSharedAndPrefixedFallbackIdentities()
    {
        var source = SquadSourceLoader.Load(ProductRoot);
        var skillsByName = source.Skills.ToDictionary(skill => skill.Name, StringComparer.Ordinal);
        var intersections = source.Agents
            .Where(agent => skillsByName.ContainsKey(agent.Name))
            .Select(agent => agent.Name)
            .ToArray();
        var unexpectedlySharedBodies = DistinctBodyAgentSkillCollisions
            .Where(name => string.Equals(
                Assert.Single(source.Agents, agent => agent.Name == name).InstructionBody,
                skillsByName[name].InstructionBody,
                StringComparison.Ordinal))
            .ToArray();
        var generatedProjectionNames = DistinctBodyAgentSkillCollisions
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
    public void Load_SharedConductorIdentities_CanSelectAgentOrSkillWithoutChangingCanonicalBody()
    {
        var source = SquadSourceLoader.Load(ProductRoot);
        var agentsByName = source.Agents.ToDictionary(agent => agent.Name, StringComparer.Ordinal);
        var skillsByName = source.Skills.ToDictionary(skill => skill.Name, StringComparer.Ordinal);
        var mismatches = SharedAgentSkillIdentities
            .Where(name => !string.Equals(
                agentsByName[name].InstructionBody,
                skillsByName[name].InstructionBody,
                StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            mismatches.Length == 0,
            "Shared conductor identities must use one normalized body for native-primary agent " +
            $"or unsupported-primary same-name skill output: {string.Join(", ", mismatches)}");
    }

    [Fact]
    public void Load_ConductorV2MigrationName_ResolvesOnlyToConductorAlias()
    {
        var source = SquadSourceLoader.Load(ProductRoot);
        var aliases = source.Agents
            .SelectMany(agent => agent.Aliases.Select(alias => (Alias: alias, Canonical: agent.Name)))
            .OrderBy(pair => pair.Alias, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal([("conductor-v2", "conductor")], aliases);
        Assert.DoesNotContain(source.Agents, agent => agent.Name == "conductor-v2");
    }

    [Fact]
    public void Load_CanonicalAgentBodies_HaveNoDuplicateDigest()
    {
        var source = SquadSourceLoader.Load(ProductRoot);
        var duplicateBodies = source.Agents
            .GroupBy(agent => agent.BodyDigest, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key}: {string.Join(", ", group.Select(agent => agent.Name))}")
            .ToArray();

        Assert.True(
            duplicateBodies.Length == 0,
            $"Canonical agents must not share an instruction body digest: {string.Join("; ", duplicateBodies)}");
    }

    [Fact]
    public void Load_PrimaryOrchestrators_DeclareOneGeneralDefaultAndExplicitTestFirstAlternative()
    {
        var source = SquadSourceLoader.Load(ProductRoot);
        var defaultAgents = source.Agents
            .Where(agent => agent.Description.Contains(
                DefaultOrchestrationTrigger,
                StringComparison.OrdinalIgnoreCase))
            .Select(agent => agent.Name)
            .ToArray();
        var testFirst = Assert.Single(source.Agents, agent => agent.Name == "conductor-v3");

        Assert.Equal(["conductor"], defaultAgents);
        Assert.Contains(
            ExplicitOrchestrationTrigger,
            testFirst.Description,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            TestFirstOrchestrationTrigger,
            testFirst.Description,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_CanonicalAgentBodies_ContainNoHarnessSpecificConfigurationTokens()
    {
        var source = SquadSourceLoader.Load(ProductRoot);
        var findings = source.Agents
            .SelectMany(agent => HarnessSpecificConfigurationTokens
                .Where(token => agent.InstructionBody.Contains(token, StringComparison.OrdinalIgnoreCase))
                .Select(token => $"{agent.SourcePath}: {token}"))
            .ToArray();

        Assert.True(
            findings.Length == 0,
            "Canonical agent bodies must describe target-neutral behavior, not harness configuration: " +
            string.Join("; ", findings));
    }

    [Fact]
    public void Load_MigrationReports_DeclareLockedSourcesAndActualCanonicalDigest()
    {
        var source = SquadSourceLoader.Load(ProductRoot);
        var migrationRoot = Path.Combine(ProductRoot, "migration");
        Assert.True(
            Directory.Exists(migrationRoot),
            $"K2 canonical migration directory is missing: {migrationRoot}");

        var reportNames = Directory.EnumerateFiles(migrationRoot, "*.md", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileNameWithoutExtension)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(ExpectedAgents, reportNames);

        var expectedSources = ParseExpectedSourceHashes();
        foreach (var agent in source.Agents)
        {
            var reportPath = Path.Combine(migrationRoot, $"{agent.Name}.md");
            var report = ReadMigrationReport(reportPath);

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

    private static string ProductRoot =>
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");

    private static RoleSkillProfile ReadRoleSkillProfile()
    {
        var path = Path.Combine(ProductRoot, "profiles", "fallbacks.yml");
        var yaml = new YamlStream();
        yaml.Load(new StringReader(File.ReadAllText(path)));
        var root = RequireMapping(yaml.Documents.Single().RootNode, "root", path);
        var profiles = RequireMapping(RequireNode(root, "profiles", path), "profiles", path);
        var roleSkill = RequireMapping(RequireNode(profiles, "role-skill", path), "role-skill", path);
        var outputIdentity = RequireMapping(
            RequireNode(roleSkill, "output-identity", path),
            "output-identity",
            path);
        var sharedIdentities = RequireSequence(
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
        var entries = ExpectedSourceHashManifest
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('|'))
            .Select(parts => new
            {
                Agent = SourceAgentName(parts[0]),
                Path = parts[0],
                Hash = parts[1]
            });

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
        var fileName = Path.GetFileName(sourcePath);
        var name = fileName.EndsWith(".agent.md", StringComparison.Ordinal)
            ? fileName[..^".agent.md".Length]
            : Path.GetFileNameWithoutExtension(fileName);
        return name == "conductor-v2" ? "conductor" : name;
    }

    private static MigrationReport ReadMigrationReport(string path)
    {
        var content = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!content.StartsWith("---\n", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Migration report '{path}' has no YAML frontmatter.");
        }

        var end = content.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (end < 0)
        {
            throw new InvalidDataException($"Migration report '{path}' has unterminated YAML frontmatter.");
        }

        var yaml = new YamlStream();
        yaml.Load(new StringReader(content[4..end]));
        var root = RequireMapping(yaml.Documents.Single().RootNode, "frontmatter", path);
        var sources = RequireMapping(RequireNode(root, "sources", path), "sources", path)
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
        foreach (var pair in mapping.Children)
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
        .claude/agents/architect-v3.md|6b312b32b4d7f423e3d9fc0aaefffd389e8864e6f60aba1339b6e40976525c43
        .claude/agents/architect.md|c003c4d9061d41c670676f4379bf96726c69f1cec243422370a66863360a5910
        .claude/agents/azure-reader.md|cfe68f279be0c86223d7b3d8e18688c68a0297626ebfcf4822fd35b53af6c012
        .claude/agents/bug-crusher-investigator.md|ffae92b2f88a13524dc422e9607d2e76eed23993e032b7374a5e6a0666ba96a1
        .claude/agents/code-reviewer.md|301e887cd283bd3fd3eb090557ad2df3b1cce170930c5accdeaa2d131d93bab8
        .claude/agents/dal-dev.md|09f9d6f1a5f4311a29934049bb0d4e5e91814a7ebf29eb864f68db3378894f4b
        .claude/agents/docs-dev.md|6fef4156bd54ab4cb111aeac519daf4481555d204884bd9844051f5a98a7f4b9
        .claude/agents/dotnet-dev.md|36de7a9673c75b7d9ee35c47a9df44972b98d49e9944e701e7103f31b94352b1
        .claude/agents/github-devops.md|16d02c53b1aa2336ca0e841e5373f42d4edb9d7977f0ba0091f788a001052711
        .claude/agents/maui-dev.md|db44cd66076d87ddd44cb357978b2dc0de7e1ef05eadf1148fb454996d6191da
        .claude/agents/product-owner.md|1648960d4ee258b16dcb2f7e2862dedd5f548e12bbafbff383e01847d3d7f56c
        .claude/agents/pulumi-dev.md|28382557b95d98bb1fa7e9f6ee2e45098e65ff9ae7fdf33d85197e6e9c88b09b
        .claude/agents/python-dev.md|79e21f9c6dd947434ed1d79137bd4e60917f660e066416680edd048d35a7a4bd
        .claude/agents/react-dev.md|5bdb08955c9981583f4371597d01770cc91ee53a8d1fc3b34be868799e709fdc
        .claude/agents/research-agent.md|7a263b79530bf959593b08cab1f06453b8b1d1c2c7b8e087240c4799e30bb314
        .claude/agents/sql-database-architect.md|fa601470d69f439c185c2578eac5418cd6c95fb21f3b518559d67e9be2d5acc4
        .claude/agents/tauri-dev.md|930ba43922d1608a9ad80342f170d3b1a121483e63361ab813bf25f396a123bc
        .claude/agents/test-dev.md|f5cbeddcca72d6786607d0b07f6a3f3a5ce2c336a927fb1d70f4d19f7f6333ac
        .codex/agents/architect-v3.toml|8ce47ea00e09b96aa145802fcb578da96eb96d181ce968f7942f3e9ff8311e70
        .codex/agents/architect.toml|ea0fce1e9c51ffe9aefe4a0c051e40faa13a0ae0526e89b0ff1329ae17533520
        .codex/agents/azure-reader.toml|3b8d75165fb36380643a4077020a884973c1ad44cc28171603baa270bda9ab0d
        .codex/agents/bug-crusher-investigator.toml|dea8626c601d48fa5407ce1257da8c2ff8a27c3dfd0720f692bac5afe543c0b9
        .codex/agents/code-reviewer.toml|4f919b491271d65ce7b10108bb3715308bc9037c0de1916c483ed23e566ce8c8
        .codex/agents/dal-dev.toml|416ce76e948cd447bb3fb97c6585fa87f23d36b57da8d74429cada745fe71c9d
        .codex/agents/docs-dev.toml|93be095ce082aca68241ffb2161812b8212ce873414725a117c65c29b89fd6e2
        .codex/agents/dotnet-dev.toml|aa7612e3bd3ffa6ec76ca907aeb58bc6aa60c54cecd424efe3abe048bf87620e
        .codex/agents/github-devops.toml|861b99bcb75ec969c30d6c8ec1855b182fccdf9aafa6f5bcded35e0cda9e800a
        .codex/agents/maui-dev.toml|beafe3cdb9841b8dfeae192f97c2108132576144534722fffdd184de3109c960
        .codex/agents/product-owner.toml|fe6ecca5dbf8d8c471a77ad4e8ac91040973627b1a9486032815693f382a5bd1
        .codex/agents/pulumi-dev.toml|86292bb21823ca958fdde3ed32f12064eda4c1baba0456ed91d0f897b84c4c72
        .codex/agents/python-dev.toml|e4f35c89f89bdf5f88ad3465e78a22f3d9cccff8ba99f086325f66c4d317fce4
        .codex/agents/react-dev.toml|29aee883289f0630b1467d84e0c7121da1dbc47d8dfd78a3d6b4c19deb730fe3
        .codex/agents/research-agent.toml|e7191addc253a08b7cb1c9205c046f35cf3f2facc45d08e997eceb92abe4a2c6
        .codex/agents/sql-database-architect.toml|3dd58d14028ff8aafaac8c868b824747f44fc5504d09211470efdc7cad686cc6
        .codex/agents/tauri-dev.toml|b941336e8fd75c6961b57be25b7b5a2af9243c31502793e9adaf4bd54fbf69e9
        .codex/agents/test-dev.toml|7f3669af6145e0d39f50f67e4c6070e06114ec98569a7871aa98334b145e6a78
        .cursor/agents/architect-v3.agent.md|6fbb40364b86fb88043145e3b9f9c895803b1bc0eabbe4e0cc57aaa5c328da7f
        .cursor/agents/architect.agent.md|21530e88dc55fc5b8abb4f64489c56f71b2946fdee126439d6405763df1f3529
        .cursor/agents/azure-reader.agent.md|c1d20516aafa70b6b7a039b5a3e123f2409a0433ab7a5109cd4316e93cc39896
        .cursor/agents/bug-crusher-investigator.agent.md|c60bbeb8c849d58db3bca7eb7f9bc5027cbb35b61e23c0fa7f22bf4d1258d606
        .cursor/agents/code-reviewer.agent.md|abbc10e4d5758d50d032d60598f48da0f590f3f4aef61fe68dd6a499d6a2626f
        .cursor/agents/dal-dev.agent.md|f55c14cc1dbca99edca8912e4094f0696837289764a577d56ffb0521a8cbb43f
        .cursor/agents/docs-dev.agent.md|d21c3c06d105b517234d2eb5e9faf4cce02ec677e922cb81edb08ea8bd1fbe8e
        .cursor/agents/dotnet-dev.agent.md|908e5f0813a3a4a0ff6b1d983645744c24b7bce6574cb97dff34dd52b6f0b616
        .cursor/agents/github-devops.agent.md|d04bef3702c88bb3fe601243ba142e01c4514179e30e2c582af8337c8da26107
        .cursor/agents/maui-dev.agent.md|f8f404b37f11eee6882f2a9394cb665056314df1b7be89978122c1cf1eaeae69
        .cursor/agents/product-owner.agent.md|e8bc63a89a1b078f4df4aee15eae89b159972ce20ecdc0507473d4f75b635513
        .cursor/agents/pulumi-dev.agent.md|87c7ab97c90c3d048d5c0b1c98f2e22c10bb0f5bd8b669ac7e736c57cb792448
        .cursor/agents/python-dev.agent.md|f54df2691e7a9eefe39ebc7594c76ad1219b849231db175e7be231d4863bb5c6
        .cursor/agents/react-dev.agent.md|c3b779757917432ec8403622d7920578a1b9c47f07e968ff161912eab25cd68b
        .cursor/agents/research-agent.agent.md|e431eba99ecdcdbb4daf648a3cfaf9c3ebf4189e056d9b0daf9e408963965dcd
        .cursor/agents/sql-database-architect.agent.md|57119991dbea2d3a2e7ac9e1d52ea9a612402036a66428807af70dfa86c1a8a2
        .cursor/agents/tauri-dev.agent.md|956b9e594dd5de07d600240de3987fb396a6c038c65d8a54e326a7471cff543d
        .cursor/agents/test-dev.agent.md|77ac564aa1a783fab81e6fa702f16496c99c8aebf6dc7cf5f7bfe1b02452e6c7
        .github/agents/architect-v3.agent.md|c973df5e8d283f7b5d9b36b95c583d9fb29e3d3633bf318603d2c4ba4ddc8208
        .github/agents/architect.agent.md|f4efb0dc22263c22469cdcfc7a77c172782748f874a69209c869b4ab09001534
        .github/agents/azure-reader.agent.md|b7f4848ffb5f056cd22296820ed65a468a68c8e7dd09a991d49d8846a17df1df
        .github/agents/bug-crusher-investigator.agent.md|d1d2eccd8cf6b08c1d53b05a7c7bd339f6ca50005f796142ae75b2ee54fad6e2
        .github/agents/code-reviewer.agent.md|c4b31d87762a8814cd0e69772be4e1b43c481337117debb1acbb658addde7976
        .github/agents/conductor-v2.agent.md|e82cce130d47eba57ffb1aa1a9b53592e78879ff40f9743158359530375b7ef9
        .github/agents/dal-dev.agent.md|5a9e1b9ef11c9e62c4f7ce71776c7ea88009041fe79aca0fa9002e13fd427d51
        .github/agents/docs-dev.agent.md|6d064d541f50a38f471083352cb1dcc9de8cc745fd96b75972da61b3ce4a44ee
        .github/agents/dotnet-dev.agent.md|ea402133727e2b32d7987871a4aa7edb09fe5240340b2fe1dff8d61affdec72c
        .github/agents/github-devops.agent.md|9eb691c620287490893255a18bdeafbe0517e908886c300d2064e7dfecd64512
        .github/agents/maui-dev.agent.md|1cb5984dfbf6142e88c464c4f8edd040fa7332ca0956ec299443cf822bb9843b
        .github/agents/product-owner.agent.md|a3a01c8088cdbf4891b639a21b099be84ead7229b5b477b20b76b5f1d4df3729
        .github/agents/pulumi-dev.agent.md|8f05c11bbdf58801e749d4b5b07ff903fa7b281d4acf60fc4743154f9a8fdc8d
        .github/agents/python-dev.agent.md|99f132c8bcfc21649a115a4d87c000c76b410be147d7c32b1d70d4fb8d8c34ee
        .github/agents/react-dev.agent.md|0b089376574faafc5b3399032705b64e62482efdea6095e3e162e57aaa6b16a5
        .github/agents/research-agent.agent.md|1f9a585bd02881b9f502c54704dda05baa4cb9e9cde9b410f11c9caef4a1002b
        .github/agents/sql-database-architect.agent.md|91a27a358c43ce3719afbf9899d3f4f9942b57ed32e2768b36943475b327b8f8
        .github/agents/tauri-dev.agent.md|9c633575014701180012aaa839c4d0e7a79540627b398be81c9844d76bac8535
        .github/agents/test-dev.agent.md|bb2700d7d1fa57405a8de345de6ab08e17d66b3727b0a987883345f761225f22
        .opencode/agents/architect-v3.md|5b35cc86188a35bb2a1d4edd8ed1fd8788ca7b5df772bc197defdfe3f53b93e6
        .opencode/agents/architect.md|986174e95069d9693f98d6358850e6304973868467daca0129549f60cd0ccef5
        .opencode/agents/azure-reader.md|aa014571d5e760bcf88508a4c8f61c09987ecbad4733c08d58ee319f78a032a0
        .opencode/agents/bug-crusher-investigator.md|534c307e6fa95588e63185b43f4b60e15b33552ca3e5a220c234268a3e9a5b81
        .opencode/agents/code-reviewer.md|4e40f1a3130f9618f741d93b897c00a9606c6d7de970b583c7f8ad47d3a10de3
        .opencode/agents/conductor-v2.md|681ecbd25ebf61ed3d0550bc1f4e3e50639d7c155db1003aaa5b31a64d600180
        .opencode/agents/conductor-v3.md|2696d45e25dcc3bbfc0f20445026d5f0213a59e783ba247bd59dc109b112e95c
        .opencode/agents/dal-dev.md|9f3fd875d63276e02b0219fbed33e084f0fd3c95e7090cd7152b98ad120f9adf
        .opencode/agents/docs-dev.md|e95d461ea01b696d5c45e519f71fad135042bf683c04b9e348daaf2d1dd60ae1
        .opencode/agents/dotnet-dev.md|2190ab5b76e48203bf3250468e35741ec90625402bcf5ab7e2051cd3df3d1bb0
        .opencode/agents/github-devops.md|a66ba87bd4513493f3e8e98884a54a692645b02a272e597852a5f758aba2b2fc
        .opencode/agents/maui-dev.md|8c4133b41cd6439d078b1735482a5bdb10602078aa79f19a79b7a3dfe3951261
        .opencode/agents/product-owner.md|cdfefd1fe9000e568e7d35972753364892cfd0f3c90c67fa7407a7a79a21fb9b
        .opencode/agents/pulumi-dev.md|802635c4c85532f1a0a3c78198cef9212494e73b352e139fe98ac280db84aea8
        .opencode/agents/python-dev.md|4992401ae92c611b480b9733d4678fceeadd0d04825a6b3b77b08234a80566eb
        .opencode/agents/react-dev.md|e4ce5052baf16b1c3b9aa13b97d5078927c1dc901d959f8643f80bcf7046499f
        .opencode/agents/research-agent.md|7991063e7b5ede195f7a9ac74526a0e773d435facbe57fe3b207b0f317aada39
        .opencode/agents/sql-database-architect.md|da70672b7a0a9eda28e25de01fdc85646e03f4544e0895f0b8ad3be897830b37
        .opencode/agents/tauri-dev.md|dfc067c2aa69313d06b30bcba7c0605266b64dfe5b251a811ab64a4253c8f3a1
        .opencode/agents/test-dev.md|0c6262120157c680f5d3403973b8dfe773209f93605891cc939b1f1584144025
        """;
}
