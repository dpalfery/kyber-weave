using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace KyberWeave.Tests;

public sealed class SquadCanonicalContentTests
{
    private const string DefaultOrchestrationTrigger = "default entry point";

    /// <summary>
    /// The shared worker execution contract, relative to the canonical product root. It is a canonical
    /// skill rather than a conductor reference so a fallback target receives it, and so bug-crusher can
    /// load it without linking out of its own directory.
    /// </summary>
    private const string DeliveryExecutionSkillRelativePath = "skills/delivery-execution/SKILL.md";

    /// <summary>
    ///     The failure-loop rules a developer agent used to carry in its own body. T52 moved them to the
    ///     one reference the task gate loads after a failed fix (D43), so the pins below read that
    ///     reference rather than each agent's body, and <c>WorkerRuleSingleHomeTests</c> asserts the
    ///     bodies no longer carry them.
    /// </summary>
    private const string ReworkReferenceRelativePath = "skills/task-gate/references/rework.md";

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

    /// <summary>The surfaces that own the review run: the council's standing instructions and the skill's procedure.</summary>
    private static readonly string[] ReviewerSurfacePaths =
    [
        "agents/code-reviewer.md",
        "skills/code-review/SKILL.md"
    ];

    /// <summary>
    /// The surfaces a plan is authored from. A plan is written before the run, so it must not carry a review command
    /// that goes stale as the gate list and its flags move.
    /// </summary>
    private static readonly string[] PlanSurfacePaths =
    [
        "agents/architect/references/plan-authoring.md",
        "agents/conductor/references/plan-path.md"
    ];

    /// <summary>
    /// The <c>--gate</c> option as a whole token. A plain substring search also matches <c>--gates</c>, which is a
    /// different option on <c>review verdict</c>.
    /// </summary>
    private static readonly Regex GateOptionMention = new(
        @"(?<![\w-])--gate(?![\w-])",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// The opening of the one bullet that tells an agent to set the session title, verbatim and
    /// including the Markdown emphasis. What the bullet says is a prose rule in the catalog.
    /// </summary>
    private const string SessionTitleBulletLead = "- **Name the session after the work.**";

    /// <summary>
    /// The vocabulary of the shape itself: the title line, the bottom line, the sentence cap on it, and
    /// the rule that the format is never named. A surface that uses these words to say how an answer is
    /// arranged has started a second copy of what the shipped default guidance owns.
    /// </summary>
    /// <remarks>
    /// The gaps are <c>\s+</c> so a phrase wrapped across two lines is still seen, and the text is read
    /// without Markdown emphasis so a phrase split by <c>**</c> or backticks is seen too.
    /// </remarks>
    private static readonly Regex RestatedShapeRule = new(
        @"\btitle\s+line\b|\bbottom\s+line\b|\bat\s+most\s+(?:3|three)\s+sentences\b|\bmention\s+the\s+format\b|\bannouncing\s+which\s+shape\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

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

    /// <summary>The approved canonical agent roster, in ordinal order.</summary>
    private static readonly string[] ExpectedAgents =
    [
        "architect",
        "azure-reader",
        "bug-crusher",
        "bug-crusher-investigator",
        "code-reviewer",
        "conductor",
        "csharp-dev",
        "dal-dev",
        "docs-dev",
        "github-devops",
        "go-dev",
        "maui-dev",
        "product-owner",
        "pulumi-dev",
        "python-dev",
        "react-dev",
        "research-agent",
        "review-lens",
        "review-triage",
        "rust-dev",
        "sql-database-architect",
        "tauri-dev",
        "test-dev",
        "typescript-dev"
    ];

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
        "delivery-execution",
        "dp-code-reviewer",
        "github-cli",
        "github-devops",
        "go-dev",
        "kilo-review-loop",
        "lm-studio-cli",
        "maui-dev",
        "pr-review-fix-comments",
        "product-owner",
        "python-dev",
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
    /// One copy of the worker execution contract, in a canonical skill both entry points load: the corpus
    /// holds it, the bundle selects it so every target receives it, and the conductor's body and
    /// bug-crusher's workflow both name it.
    /// </summary>
    /// <remarks>
    /// Bug-crusher used to keep a partial second copy of these rules inside its own workflow, and the two
    /// drifted the first time one moved. The bodies are read from the loaded corpus rather than the files,
    /// because the loader's normalized instruction body is what a harness receives: a name that survives
    /// only in a frontmatter comment never reaches a reader.
    /// </remarks>
    [Fact]
    public void LoadFullProductTreeCarriesTheSharedExecutionSkillBothEntryPointsLoad()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        bool loaded = source.Skills.Any(skill => skill.Name == "delivery-execution");
        bool selected = source.Bundle.SkillNames.Contains("delivery-execution", StringComparer.Ordinal);

        Assert.True(
            loaded && selected,
            $"The corpus must hold '{DeliveryExecutionSkillRelativePath}' and the bundle must select it, or no " +
            $"target receives it and no entry point can load it. Loaded: {loaded}. Selected: {selected}. " +
            $"Roster: {string.Join(", ", source.Skills.Select(skill => skill.Name))}.");

        (string Surface, string Body)[] entryPoints =
        [
            ("agents/conductor.md", source.Agents.Single(agent => agent.Name == "conductor").InstructionBody),
            ("skills/bug-crusher/SKILL.md", source.Skills.Single(skill => skill.Name == "bug-crusher").InstructionBody)
        ];
        string[] silent =
        [
            .. entryPoints
                .Where(entryPoint => !entryPoint.Body.Contains("delivery-execution", StringComparison.Ordinal))
                .Select(entryPoint => entryPoint.Surface)
        ];

        Assert.True(
            silent.Length == 0,
            $"Both entry points must name the '{DeliveryExecutionSkillRelativePath}' skill they load. " +
            $"Silent: {string.Join(", ", silent)}.");
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
    /// The product README is the human-readable skill roster. It must not list the retired skill in its tree.
    /// It states the skills without a hardcoded count: the directories under <c>skills/</c> are the
    /// inventory, so a skill added later changes no prose.
    /// </summary>
    [Fact]
    public void ProductReadmeRosterLacksSecondBrain()
    {
        string readme = File.ReadAllText(Path.Combine(ProductRoot, "README.md"));

        Assert.DoesNotContain("second-brain", readme, StringComparison.Ordinal);
        Assert.Contains("canonical skill", readme, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The human-facing entry files carry the response preferences the shipped default owns, and they carry
    /// them through one block the deployer replaces. Naming the skill is not the mechanism any more.
    /// </summary>
    /// <remarks>
    /// A count of exactly one, not at least one: an entry with no block receives no guidance, and an entry with
    /// two receives it twice. The injector's own tests pin the replacement; this pins the carrier in the source.
    /// </remarks>
    [Theory]
    [InlineData("agents/conductor.md")]
    [InlineData("agents/bug-crusher.md")]
    [InlineData("skills/bug-crusher/SKILL.md")]
    public void EveryHumanFacingEntryCarriesExactlyOneResponsePreferencesBlock(string relativePath)
    {
        string path = Path.Combine(ProductRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

        Assert.True(File.Exists(path), $"Expected the canonical entry source at '{path}'.");
        int blocks = Regex.Count(
            File.ReadAllText(path),
            "<response-preferences>.*?</response-preferences>",
            RegexOptions.Singleline);
        Assert.True(
            blocks == 1,
            $"'{relativePath}' must carry exactly one <response-preferences> block; it carries {blocks}.");
    }

    /// <summary>
    /// The retired skill must not survive anywhere in the product tree: a name left behind routes an agent to a
    /// skill that no longer deploys.
    /// </summary>
    [Fact]
    public void NoProductFileNamesTheRetiredResponseFormatSkill()
    {
        string[] offenders = Directory
            .EnumerateFiles(ProductRoot, "*", SearchOption.AllDirectories)
            .Where(path => File.ReadAllText(path).Contains("response-format", StringComparison.Ordinal))
            .Select(RelativeSourcePath)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "These product files still name the retired 'response-format' skill: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// The architecture document carries the session-title matrix paragraph. What its sentences say
    /// about Factory and the pull-request title is checked by the prose rule catalog.
    /// </summary>
    [Fact]
    public void TheArchitectureDocumentCarriesTheSessionTitleMatrix()
    {
        string path = Path.Combine(
            KyberWeaveTestPaths.ToolRoot, "docs", "kyber-squad", "architecture.md");
        string matrix = ParagraphStartingWith(
            File.ReadAllText(path),
            "**Session-title matrix (reference).**");

        Assert.True(
            matrix.Length > 0,
            "docs/kyber-squad/architecture.md must carry the session-title matrix.");
    }

    /// <summary>
    /// The shipped default file exists at its product path and carries content. It is the one file
    /// the build embeds and a global install deploys, so an empty or missing default leaves every
    /// Squad answer without its shape.
    /// </summary>
    [Fact]
    public void TheShippedDefaultGuidanceFileExistsAndIsNonEmpty()
    {
        string defaultPath = ResponseGuidanceDefaultPath;

        Assert.True(
            File.Exists(defaultPath),
            $"The shipped response-guidance default must live at '{defaultPath}'.");
        Assert.False(
            string.IsNullOrWhiteSpace(File.ReadAllText(defaultPath)),
            $"The shipped response-guidance default at '{defaultPath}' must not be empty.");
    }

    /// <summary>
    /// The default file is the owner the build reads from, not a copy that can drift. The drift guard
    /// must name it as canonical, and the copy embedded as <c>Squad.response-guidance.md</c> must be
    /// byte-identical to it. Otherwise the deployed default and the documented default can disagree
    /// while both still build.
    /// </summary>
    [Fact]
    public void TheBuildEmbedsTheDefaultGuidanceAsTheCanonicalProductFile()
    {
        string defaultPath = ResponseGuidanceDefaultPath;
        Assert.True(
            File.Exists(defaultPath),
            $"The shipped response-guidance default must live at '{defaultPath}' before the build can own it.");
        string corePath = Path.Combine(KyberWeaveTestPaths.ToolRoot, "src", "KyberWeave.Core.Squad");
        XDocument project = XDocument.Load(Path.Combine(corePath, "KyberWeave.Core.Squad.csproj"));

        string? canonical = project
            .Descendants("CanonicalResponseGuidance")
            .Select(element => element.Attribute("Include")?.Value)
            .FirstOrDefault();
        string? embedded = project
            .Descendants("EmbeddedResource")
            .Where(element => element.Attribute("LogicalName")?.Value == "Squad.response-guidance.md")
            .Select(element => element.Attribute("Include")?.Value)
            .FirstOrDefault();

        Assert.NotNull(canonical);
        Assert.NotNull(embedded);
        Assert.Equal(
            defaultPath,
            Path.GetFullPath(ResolveProjectPath(corePath, canonical)));
        Assert.True(
            File.ReadAllBytes(Path.GetFullPath(ResolveProjectPath(corePath, embedded)))
                .AsSpan()
                .SequenceEqual(File.ReadAllBytes(defaultPath)),
            "The copy embedded as 'Squad.response-guidance.md' must be byte-identical to the shipped default " +
            $"at '{defaultPath}'.");
    }

    /// <summary>
    /// The shipped default carries the session-title bullet. The rule the bullet states, that the
    /// agent sets the title, is checked by the prose rule catalog.
    /// </summary>
    [Fact]
    public void TheShippedDefaultCarriesTheSessionTitleBullet()
    {
        string guidance = ReadResponseGuidanceDefault();
        string bullet = ListItemStartingWith(guidance, SessionTitleBulletLead) ?? string.Empty;

        Assert.True(
            bullet.Length > 0,
            $"The shipped default must state the session-title rule as the bullet '{SessionTitleBulletLead}'.");
    }

    /// <summary>
    /// The conductor resolves the <c>&lt;intake-assessment&gt;</c> property, and defect-first precedence
    /// comes ahead of that resolution, because a concrete failure still outranks the question of which
    /// artifact to write. That the assessment is opt-in is checked by the prose rule catalog.
    /// </summary>
    [Fact]
    public void ConductorPutsDefectFirstAheadOfTheIntakeAssessmentResolution()
    {
        string conductor = File.ReadAllText(Path.Combine(ProductRoot, "agents", "conductor.md"));
        int intake = conductor.IndexOf("<intake-assessment>", StringComparison.Ordinal);
        int defectFirst = conductor.IndexOf("**Defect first**", StringComparison.Ordinal);

        Assert.True(
            intake >= 0,
            "'agents/conductor.md' must carry an '<intake-assessment>' resolution order.");
        Assert.True(
            defectFirst >= 0 && defectFirst < intake,
            "'agents/conductor.md' must keep defect-first precedence ahead of the " +
            "'<intake-assessment>' resolution.");
    }

    /// <summary>
    /// The intake path names the <c>&lt;intake-assessment&gt;</c> property the plan path depends on.
    /// That the assessment is off by default is checked by the prose rule catalog.
    /// </summary>
    [Fact]
    public void IntakePathNamesTheIntakeAssessmentProperty()
    {
        string intakePath = File.ReadAllText(
            Path.Combine(ProductRoot, "agents", "conductor", "references", "intake-path.md"));

        Assert.True(
            intakePath.Contains("<intake-assessment>", StringComparison.Ordinal),
            "'agents/conductor/references/intake-path.md' must record the '<intake-assessment>' gate " +
            "the plan path depends on.");
    }

    /// <summary>
    /// The shape has one home (requirement R5). A surface that spells it out again carries a second copy
    /// that the user's own guidance file cannot reach, so an operator who replaces the wording still gets
    /// the old shape from whichever surface restated it. A surface binds the skill by name and says only
    /// what each response has to contain; how it is arranged is the skill's, and the operator's, to say.
    /// </summary>
    [Fact]
    public void NoAgentOrSkillRestatesTheResponseShapeOutsideTheDefaultGuidance()
    {
        string[] surfaces = Directory
            .EnumerateFiles(Path.Combine(ProductRoot, "agents"), "*.md", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(
                Path.Combine(ProductRoot, "skills"),
                "*.md",
                SearchOption.AllDirectories))
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] restatements = surfaces
            .SelectMany(path => RestatedShapeHits(RelativeSourcePath(path), File.ReadAllText(path)))
            .ToArray();

        Assert.NotEmpty(surfaces);
        Assert.True(
            restatements.Length == 0,
            "These agent and skill bodies restate the response shape the shipped default guidance owns, so a " +
            "user's own guidance file cannot change it there. Keep the response-preferences block and what the response " +
            $"must contain, and leave the arrangement to the default guidance:{Environment.NewLine}" +
            string.Join(Environment.NewLine, restatements));
    }

    /// <summary>
    /// The retired skill-copy path must not survive in source or test code. A build target or a
    /// doc comment that still names it points at a file the skill removal deletes, so the build
    /// and the tests would fail for a reason no reader can see from the code.
    /// </summary>
    [Fact]
    public void NoSourceOrTestFileNamesTheRetiredResponseGuidancePath()
    {
        Regex retired = new(
            @"skills[\\/]response-format[\\/]user-guidance\.md",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);
        string[] extensions = [".cs", ".csproj", ".props", ".targets"];
        string[] offenders =
        [
            .. Directory
                .EnumerateFiles(Path.Combine(KyberWeaveTestPaths.ToolRoot, "src"), "*", SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(
                    Path.Combine(KyberWeaveTestPaths.ToolRoot, "tests"),
                    "*",
                    SearchOption.AllDirectories))
                .Where(path => extensions.Contains(Path.GetExtension(path), StringComparer.Ordinal))
                .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(segment =>
                    segment is "bin" or "obj" or ".agent_scratchpad"))
                .Where(path => retired.IsMatch(File.ReadAllText(path)))
                .Order(StringComparer.Ordinal)
        ];

        Assert.Empty(offenders);
    }

    [Fact]
    public void ReviewSurfacesEnforceBinaryApproveOrRequestChangesVerdict()
    {
        string[] reviewSurfaces =
        [
            Path.Combine(ProductRoot, "agents", "code-reviewer.md"),
            Path.Combine(ProductRoot, "agents", "review-lens.md"),
            Path.Combine(ProductRoot, "skills", "code-review", "SKILL.md"),
            Path.Combine(ProductRoot, "skills", "dp-code-reviewer", "SKILL.md"),
            Path.Combine(ProductRoot, "skills", "bug-crusher", "SKILL.md"),
            Path.Combine(ProductRoot, "agents", "conductor", "references", "autonomy.md"),
            Path.Combine(ProductRoot, "skills", "delivery-execution", "SKILL.md")
        ];

        string[] forbiddenPhrases =
        [
            "NEEDS_HUMAN",
            "NEEDS HUMAN",
            "Needs-human",
            "needs-human",
            "always-human",
            "max-reviewable-lines"
        ];

        Assert.All(
            reviewSurfaces,
            path => Assert.True(File.Exists(path), $"Expected the review surface at '{path}'."));

        foreach (string surface in reviewSurfaces)
        {
            string fileText = File.ReadAllText(surface);
            foreach (string phrase in forbiddenPhrases)
            {
                Assert.DoesNotContain(phrase, fileText, StringComparison.OrdinalIgnoreCase);
            }
        }

        string codeReviewer = File.ReadAllText(Path.Combine(ProductRoot, "agents", "code-reviewer.md"));
        string codeReviewSkill = File.ReadAllText(Path.Combine(ProductRoot, "skills", "code-review", "SKILL.md"));

        Assert.Contains("APPROVE", codeReviewer, StringComparison.Ordinal);
        Assert.Contains("REQUEST_CHANGES", codeReviewer, StringComparison.Ordinal);

        Assert.Contains("APPROVE", codeReviewSkill, StringComparison.Ordinal);
        Assert.Contains("REQUEST_CHANGES", codeReviewSkill, StringComparison.Ordinal);
    }

    /// <summary>
    /// The restatement pin is worth having only if it can fail. It has to see the shape in each way the
    /// bug-crusher skill once spelled it out, and across a line wrap, and it has to let through a surface
    /// that only binds the skill by name.
    /// </summary>
    [Fact]
    public void TheRestatedShapePinSeesEveryWayTheShapeIsSpelledOut()
    {
        Assert.True(
            RestatesTheShape(
                "Each opens with a title line naming the work, states the bottom line in at most 3 sentences."),
            "The title line and the sentence cap are the shape and must be seen.");
        Assert.True(
            RestatesTheShape("Close with a bottom line of at most three sentences."),
            "A cap spelled out in words must be seen as well as one written as a digit.");
        Assert.True(
            RestatesTheShape("The grouped questions are bullets, with the title and bottom\nline above them."),
            "A phrase wrapped across two lines must still be seen.");
        Assert.True(
            RestatesTheShape("Never tag, header, or otherwise mention the **format** in the report."),
            "The never-name-it rule is the skill's too, and emphasis inside it must not hide it.");
        Assert.True(
            RestatesTheShape(
                "The reader should see a well-shaped answer, not an agent announcing which shape it chose."),
            "The reason given for the never-name-it rule is a restatement of it.");
        Assert.False(
            RestatesTheShape(
                "Each entry file carries the operator response preferences in a <response-preferences> block."),
            "The response-preferences block is the binding and must not be flagged.");
        Assert.False(
            RestatesTheShape(
                "The shape of an answer is the one thing this wrapper binds by name instead of restating."),
            "A surface that says it does not restate the shape must not be flagged for saying so.");
        Assert.False(
            RestatesTheShape(
                "- **Summary**: the problem and the outcome, in one to three sentences; why more than how."),
            "The length of a pull request summary is another artifact's rule, not the shape of a response.");
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

        Assert.Empty(aliases);
        Assert.DoesNotContain(source.Agents, agent => IsVersionedIdentity(agent.Name));
        Assert.DoesNotContain(source.Bundle.AgentNames, IsVersionedIdentity);
        Assert.Empty(versionedDelegates);
        Assert.DoesNotContain("test-first-orchestration", source.ModelProfiles.Profiles.Keys);
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

    /// <summary>
    /// The conductor contract names the plan's <c>development-mode</c> field and the Draft and Ready
    /// statuses. The intake paths, the mode values and the lifecycle rules are checked by the prose rule
    /// catalog.
    /// </summary>
    [Fact]
    public void LoadConductorNamesTheDevelopmentModeAndTheDraftAndReadyStatuses()
    {
        string contract = ReadAgentContract("conductor");

        Assert.Contains("development-mode", contract, StringComparison.Ordinal);
        Assert.Contains("Ready", contract, StringComparison.Ordinal);
        Assert.Contains("Draft", contract, StringComparison.Ordinal);
    }

    /// <summary>
    /// The conductor's iteration circuit-breaker tokens: the escalation marker, the recommended action,
    /// and the closed <c>CIRCUIT_BREAKER_TRIGGER</c> vocabulary. The rework limit and oscillation rules
    /// are checked by the prose rule catalog. The tokens live in the shared execution contract both entry
    /// points load, so that skill is the surface asserted here rather than the conductor's own body.
    /// </summary>
    [Fact]
    public void LoadConductorNamesTheCircuitBreakerTokens()
    {
        string contract = ReadProductFile(DeliveryExecutionSkillRelativePath);

        Assert.Contains("ITERATION_CAP_EXCEEDED", contract, StringComparison.Ordinal);
        Assert.Contains("THRASH_OSCILLATION_DETECTED", contract, StringComparison.Ordinal);
        Assert.Contains("INVARIANT_CONTRADICTION", contract, StringComparison.Ordinal);
        Assert.Contains("BLAST_RADIUS_EXCEEDED", contract, StringComparison.Ordinal);
        Assert.Contains("ESCALATION: circuit-breaker", contract, StringComparison.Ordinal);
        Assert.Contains("RECOMMENDED_ACTION", contract, StringComparison.Ordinal);
        AssertClosedCircuitBreakerTriggerSet(contract);
    }

    /// <summary>
    ///     The failure-loop tokens — JEV, the escalation status, the escalation payload fields and the
    ///     closed <c>CIRCUIT_BREAKER_TRIGGER</c> vocabulary — live in the one reference the task gate
    ///     loads after a failed fix. The iteration cap and the no-unconstrained-loop rule are checked by
    ///     the prose rule catalog.
    /// </summary>
    [Fact]
    public void TheReworkReferenceNamesTheJevAndCircuitBreakerTokens()
    {
        string contract = ReadProductFile(ReworkReferenceRelativePath);

        Assert.Contains("JEV", contract, StringComparison.Ordinal);
        Assert.Contains("STATUS: ESCALATION", contract, StringComparison.Ordinal);
        Assert.Contains("CIRCUIT_BREAKER_TRIGGER", contract, StringComparison.Ordinal);
        Assert.Contains("ITERATION_CAP_EXCEEDED", contract, StringComparison.Ordinal);
        Assert.Contains("THRASH_OSCILLATION_DETECTED", contract, StringComparison.Ordinal);
        Assert.Contains("INVARIANT_CONTRADICTION", contract, StringComparison.Ordinal);
        Assert.Contains("BLAST_RADIUS_EXCEEDED", contract, StringComparison.Ordinal);
        Assert.Contains("FAILURE_CLUSTER", contract, StringComparison.Ordinal);
        Assert.Contains("CONTRADICTORY_INVARIANTS", contract, StringComparison.Ordinal);
        Assert.Contains("BLAST_RADIUS", contract, StringComparison.Ordinal);
        Assert.Contains("RECOMMENDED_ACTION", contract, StringComparison.Ordinal);
        AssertClosedCircuitBreakerTriggerSet(contract);
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
    /// agent, so the conductor records and resumes exactly one session per task. The session rules
    /// live in the shared execution contract, so that skill is the surface asserted here.
    /// </summary>
    [Fact]
    public void ConductorRetainsOnlyWorkerSessionsForTaskRework()
    {
        string contract = ReadProductFile(DeliveryExecutionSkillRelativePath);

        Assert.Contains("worker-session-id", contract, StringComparison.Ordinal);
        Assert.DoesNotContain("reviewer-session-id", contract, StringComparison.Ordinal);
        Assert.DoesNotContain("Every worker invocation is cold", contract, StringComparison.Ordinal);
        Assert.DoesNotContain("any available worker", contract, StringComparison.Ordinal);
    }

    /// <summary>
    /// The conductor contract names the <c>TASK_GATE</c> digest and the <c>task-gate/</c> skill, and the
    /// retired per-task reviewer's three-pass audit must not survive as a second routing path. That a
    /// failing digest goes back to the worker is checked by the prose rule catalog.
    /// </summary>
    [Fact]
    public void ConductorNamesTheTaskGateAndNotTheRetiredThreePassAudit()
    {
        string contract = ReadAgentContract("conductor");

        Assert.Contains("TASK_GATE", contract, StringComparison.Ordinal);
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
        Assert.Contains("## Defect diagnosis", contract, StringComparison.Ordinal);
        Assert.Contains("STATUS: INTENT_STATED", contract, StringComparison.Ordinal);
        Assert.Contains("STATUS: DEFECT", contract, StringComparison.Ordinal);
        Assert.Contains("CHOICE: PLAN | SPEC | UNSTATED", contract, StringComparison.Ordinal);

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
        Assert.Contains("TASK_ASSESSED", readyRoute, StringComparison.Ordinal);
        Assert.Contains("REVISION_REQUIRED", readyRoute, StringComparison.Ordinal);
        Assert.Contains("STATUS: TASK_ASSESSED", assessment, StringComparison.Ordinal);
        Assert.Contains("CONTRACT: UNCHANGED | REVISION_REQUIRED", assessment, StringComparison.Ordinal);
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
    /// The shipped roster reaches its coding standard through the MCP tool. The developer agents
    /// are the roles that write code, so their instructions are what decide whether a standard
    /// arrives for a budget with the sections the task names — or is read whole from the file,
    /// 21,354 characters of it for the test standard alone against a 12,000-character default.
    /// </summary>
    /// <param name="agentName">The canonical developer agent identity.</param>
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
    public void DeveloperAgentBodyRoutesItsCodingStandardThroughTheTool(string agentName)
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadAgent agent = Assert.Single(source.Agents, a => a.Name == agentName);

        Assert.Contains("docs_get_coding_standard_for_technology", agent.InstructionBody, StringComparison.Ordinal);
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
        Assert.Contains("ITERATION_CAP_EXCEEDED", skill, StringComparison.Ordinal);
        Assert.Contains("task-gate/<gate-id>", skill, StringComparison.Ordinal);
        Assert.Contains("TASK_GATE: PASS | FAIL | NONE_DECLARED | NONE_APPLICABLE | UNAVAILABLE", skill,
            StringComparison.Ordinal);
        AssertClosedCircuitBreakerTriggerSet(skill);
    }

    /// <summary>
    /// The per-task reviewer is retired outright. Its agent and its reference folder must not survive
    /// in the squad source, or the orchestrator can still route to it.
    /// </summary>
    [Fact]
    public void TaskReviewerAgentAndReferenceFolderAreRemoved()
    {
        string agentsRoot = Path.Combine(ProductRoot, "agents");

        Assert.False(
            File.Exists(Path.Combine(agentsRoot, "task-reviewer.md")),
            "The retired task-reviewer agent body must be deleted.");
        Assert.False(
            Directory.Exists(Path.Combine(agentsRoot, "task-reviewer")),
            "The retired task-reviewer reference folder must be deleted.");
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

    /// <summary>
    /// `docs-dev` runs a closed list of commands, and the task file has to be deleted in the same
    /// commit that archives its plan. Without `git rm` on that list the only role that may archive
    /// a plan could not remove the file its own contract says it deletes.
    /// </summary>
    [Fact]
    public void DocsDevCommandListIncludesGitRmForTheTaskFile()
    {
        string docsDev = ReadProductFile("agents/docs-dev.md");

        int start = docsDev.IndexOf("## Commands you run", StringComparison.Ordinal);
        Assert.True(start >= 0, "docs-dev must carry a '## Commands you run' section.");

        int end = docsDev.IndexOf("\n## ", start + 1, StringComparison.Ordinal);
        string commands = end < 0 ? docsDev[start..] : docsDev[start..end];

        Assert.Contains("git rm", commands, StringComparison.Ordinal);
    }

    /// <summary>
    /// A specification's task file lives in the specification folder
    /// (<c>&lt;spec-folder&gt;/tasks.json</c>), removed before the folder is moved to the archive,
    /// so the closeout the owner text describes and the rule that catches a leftover file agree.
    /// </summary>
    [Fact]
    public void ProductOwnerTextFollowsTheSpecificationTaskFileForm()
    {
        string ownerBody = Path.Combine(ProductRoot, "agents", "product-owner.md");
        string ownerSkill = Path.Combine(ProductRoot, "skills", "product-owner");

        Assert.True(File.Exists(ownerBody), $"expected the product-owner body at '{ownerBody}'.");
        Assert.True(Directory.Exists(ownerSkill), $"expected the product-owner skill at '{ownerSkill}'.");

        string[] surfaces =
        [
            ownerBody,
            .. Directory.EnumerateFiles(ownerSkill, "*.md", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
        ];

        string? carrier = surfaces.FirstOrDefault(
            surface => File.ReadAllText(surface).Contains("<spec-folder>/tasks.json", StringComparison.Ordinal));

        Assert.True(
            carrier is not null,
            "the product-owner text must name `<spec-folder>/tasks.json` as the specification form of the " +
            "tracked task file. Searched: " + string.Join(", ", surfaces));
    }

    private static string RelativeSourcePath(string path)
    {
        return Path.GetRelativePath(ProductRoot, path).Replace(Path.DirectorySeparatorChar, '/');
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

    /// <summary>
    /// Review-council and conductor instructions must not keep the retired delivery wording that
    /// held a pushed change back until the council returned, or parked it unpushed. The scan covers
    /// every surface that carried the wording, so a copy left behind in one file fails the build.
    /// </summary>
    [Fact]
    public void ReviewInstructionsDropTheHoldUntilCouncilAndParkWording()
    {
        string[] reviewSurfaces =
        [
            Path.Combine(ProductRoot, "agents", "conductor", "references", "autonomy.md"),
            Path.Combine(ProductRoot, "skills", "delivery-execution", "SKILL.md"),
            Path.Combine(ProductRoot, "agents", "code-reviewer.md"),
            Path.Combine(ProductRoot, "skills", "code-review", "SKILL.md"),
            Path.Combine(ProductRoot, "skills", "bug-crusher", "SKILL.md"),
            Path.Combine(KyberWeaveTestPaths.ToolRoot, "docs", "reference", "autonomous-gates.md"),
            Path.Combine(KyberWeaveTestPaths.ToolRoot, "docs", "code-review", "architecture.md")
        ];
        string[] retiredPhrases =
        [
            "Do not commit, push, publish, or open a pull request until the council returns",
            "leave it unpushed",
            "Do not mark the pull request ready",
            "Park that change or task",
            "parked NEEDS_HUMAN",
            "at least one review"
        ];

        Assert.All(
            reviewSurfaces,
            path => Assert.True(File.Exists(path), $"Expected the review surface at '{path}'."));

        string[] offenders = reviewSurfaces
            .SelectMany(path => retiredPhrases
                .Where(phrase => File.ReadAllText(path).Contains(phrase, StringComparison.Ordinal))
                .Select(phrase => $"{Path.GetRelativePath(KyberWeaveTestPaths.ToolRoot, path)}: \"{phrase}\""))
            .ToArray();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// Both run-start surfaces carry the <c>doctor --ensure</c> step, gated on the docs-enabled condition,
    /// and state the stop rule instead of warn-and-continue.
    /// </summary>
    /// <remarks>
    /// This is the owner's reversal of "never block a run" (D1, decided 2026-11-07), not a weakening of the
    /// check: a required tool that cannot be repaired now stops the run before any worker is dispatched,
    /// because every agent downstream would otherwise work from joins that are silently absent. The
    /// docs-enabled condition is unchanged, and a repository with no governed docs still skips the step.
    /// </remarks>
    [Theory]
    [InlineData("agents/conductor.md")]
    [InlineData("skills/bug-crusher/SKILL.md")]
    public void RunStartSurfacesStopTheRunWhenTheDocsToolingCannotBeEnsured(string relativePath)
    {
        string text =
            File.ReadAllText(Path.Combine(ProductRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));

        Assert.Contains("kyber-weave doctor --ensure", text, StringComparison.Ordinal);
        Assert.Contains(".kyber-weave/kyber-weave.yml", text, StringComparison.Ordinal);
        Assert.Contains("docs-root", text, StringComparison.Ordinal);
        Assert.Contains("stop-on-tool-failure", text, StringComparison.Ordinal);
        Assert.DoesNotContain("warn and continue", text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The conductor's run-start step sits in <c>Shared lifecycle invariants</c>, the earliest section that applies to
    /// every route (D17), so it runs before any routing decision rather than on one branch of it.
    /// </summary>
    [Fact]
    public void ConductorRunStartDoctorStepIsInSharedLifecycleInvariants()
    {
        string conductor = File.ReadAllText(Path.Combine(ProductRoot, "agents", "conductor.md"));
        int invariants = conductor.IndexOf("## Shared lifecycle invariants", StringComparison.Ordinal);
        int handoffs = conductor.IndexOf("## Structured specialist handoffs", StringComparison.Ordinal);
        int doctor = conductor.IndexOf("kyber-weave doctor --ensure", StringComparison.Ordinal);

        Assert.True(invariants >= 0, "'agents/conductor.md' must keep its 'Shared lifecycle invariants' section.");
        Assert.True(
            doctor > invariants && doctor < handoffs,
            "'agents/conductor.md' must state the run-start doctor step inside 'Shared lifecycle invariants'.");
    }

    // -------------------------------------------------------------------------------------
    // How the review runs: who owns its command, the single-gate option, and the status a
    // reviewer returns when it cannot start its seats.
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// The review command comes from the reviewer, not from a plan: every review command the skill names carries
    /// <c>--base</c>, at least one carries <c>--council</c>, and no plan-authoring surface prescribes a review command.
    /// </summary>
    /// <remarks>
    /// A plan is written before the run, so a command copied into it goes stale the moment the gate list or its flags
    /// move, and the reviewer then runs a command nobody validated. <c>--base</c> measures the gates against the change
    /// instead of the whole tree. <c>--council</c> is what admits the full-suite gates, which only the review council
    /// and CI may run.
    /// </remarks>
    [Fact]
    public void TheReviewSkillNamesItsOwnCommandWithBaseAndCouncilAndNoPlanPrescribesOne()
    {
        string[] prescribed = PlanSurfacePaths
            .SelectMany(path => ReviewCommandLines(ReadProductFile(path)).Select(line => $"{path}: {line}"))
            .ToArray();

        Assert.True(
            prescribed.Length == 0,
            "A plan must not prescribe a review command; the reviewer owns it. Found:" +
            Environment.NewLine + string.Join(Environment.NewLine, prescribed));

        string[] commands = ReviewCommandLines(ReadProductFile("skills/code-review/SKILL.md"));

        Assert.NotEmpty(commands);
        Assert.All(commands, command => Assert.Contains("--base", command, StringComparison.Ordinal));
        Assert.True(
            commands.Any(command => command.Contains("--council", StringComparison.Ordinal)),
            "'skills/code-review/SKILL.md' must name the review command with --council for the council's run. " +
            $"Commands found:{Environment.NewLine}{string.Join(Environment.NewLine, commands)}");
    }

    /// <summary>
    /// The reviewer's surfaces name the <c>--gate</c> option. That it is how the reviewer re-checks one gate
    /// instead of re-running the suite is checked by the prose rule catalog.
    /// </summary>
    [Fact]
    public void TheReviewerSurfacesNameTheSingleGateOption()
    {
        string[] mentions = ReviewerSurfacePaths
            .SelectMany(path => Paragraphs(ReadProductFile(path)).Select(block => $"{path}: {block}"))
            .Where(block => GateOptionMention.IsMatch(block))
            .ToArray();

        Assert.True(
            mentions.Length > 0,
            "The reviewer's instructions must name 'review gates --gate <id>' for a single-gate re-check. " +
            $"Read: {string.Join(", ", ReviewerSurfacePaths)}.");
    }

    /// <summary>
    /// The code reviewer names the <c>STATUS: COUNCIL_SEATS_NEEDED</c> marker. What a reviewer that cannot start
    /// its seats does is checked by the prose rule catalog.
    /// </summary>
    [Fact]
    public void TheCodeReviewerNamesTheCouncilSeatsNeededStatus()
    {
        string reviewer = ReadProductFile("agents/code-reviewer.md");

        Assert.True(
            reviewer.Contains("STATUS: COUNCIL_SEATS_NEEDED", StringComparison.Ordinal),
            "'agents/code-reviewer.md' must route a council that cannot start its seats with " +
            "'STATUS: COUNCIL_SEATS_NEEDED'.");
    }

    /// <summary>
    /// Every shipped agent file states the stop rule and the discovery order, so no role is left with the
    /// old instruction to carry on past a broken tool.
    /// </summary>
    /// <remarks>
    /// A single agent file that still says the run continues is enough to keep the failure alive, because
    /// that is the file the delegate reads. The discovery order travels with it: in a worktree, or when the
    /// MCP tool is not among the tools on offer, the shell runs <c>codegraph explore</c>, and a file is read
    /// directly only when that fails.
    /// </remarks>
    [Fact]
    public void EveryAgentFileStatesTheStopRuleAndTheDiscoveryOrder()
    {
        string[] agents = Directory.GetFiles(Path.Combine(ProductRoot, "agents"), "*.md");

        Assert.NotEmpty(agents);

        string[] offenders =
        [
            .. agents
                .Select(path => (Path: path, Text: File.ReadAllText(path)))
                .Where(file => !file.Text.Contains("codegraph explore", StringComparison.Ordinal))
                .Select(file => $"{Path.GetFileName(file.Path)}: no 'codegraph explore' discovery order")
        ];

        Assert.Empty(offenders);

        string[] stillWarn =
        [
            .. agents
                .Select(path => (Path: path, Text: File.ReadAllText(path)))
                .Where(file => file.Text.Contains("warn and continue", StringComparison.OrdinalIgnoreCase))
                .Select(file => Path.GetFileName(file.Path))
        ];

        Assert.Empty(stillWarn);
    }

    /// <summary>
    /// A tool failure is routed to the orchestrator and listed among the hard stops in autonomous mode.
    /// </summary>
    /// <remarks>
    /// A required tool that cannot be repaired is the one failure an autonomous run cannot resolve for
    /// itself: it needs an install the run may not perform. Listing it with the other hard stops is what
    /// stops the run from continuing to a worker that would answer from absent joins.
    /// </remarks>
    [Fact]
    public void ConductorAutonomyContractListsToolFailureAmongTheHardStops()
    {
        string autonomy = File.ReadAllText(
            Path.Combine(ProductRoot, "agents", "conductor", "references", "autonomy.md"));
        int hardStops = autonomy.IndexOf("## Hard stops", StringComparison.Ordinal);
        int toolFailure = autonomy.IndexOf("STATUS: TOOL_FAILURE", StringComparison.Ordinal);

        Assert.True(hardStops >= 0, "'autonomy.md' must keep its 'Hard stops' section.");
        Assert.True(
            toolFailure > hardStops,
            "'autonomy.md' must list 'STATUS: TOOL_FAILURE' as a hard stop in autonomous mode.");
    }

    /// <summary>
    /// The architect compares <c>repo=</c> rather than <c>root=</c>, so a worktree of this repository is not
    /// mistaken for a foreign one.
    /// </summary>
    /// <remarks>
    /// D25: every docs response now carries <c>repo=</c>. An architect that still compares <c>root=</c>
    /// abandons a correct corpus as "the wrong repository" whenever it plans from a worktree, which is the
    /// case the whole slice exists to make work.
    /// </remarks>
    [Theory]
    [InlineData("agents/architect.md")]
    [InlineData("agents/architect/references/plan-authoring.md")]
    public void ArchitectSurfacesCompareTheRepositoryRatherThanTheRoot(string relativePath)
    {
        string text = File.ReadAllText(
            Path.Combine(ProductRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));

        Assert.Contains("repo=", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The run-start step is one command, and every surface that describes it names that command. A
    /// reference that describes the step without naming the command leaves the agent to reconstruct it
    /// from prose, which is how the two entry points drifted apart in the first place.
    /// </summary>
    /// <param name="relativePath">A run-start surface, relative to the canonical Squad source root.</param>
    [Theory]
    [InlineData("agents/conductor.md")]
    [InlineData("skills/bug-crusher/SKILL.md")]
    [InlineData("agents/conductor/references/intake-path.md")]
    [InlineData("agents/conductor/references/plan-path.md")]
    public void RunStartSurfacesNameTheRunStartCommand(string relativePath)
    {
        string text = File.ReadAllText(Path.Combine(ProductRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));

        Assert.Matches(RunStartCommand, text);
    }

    /// <summary>
    /// The <c>run start</c> invocation the run-start surfaces must name, matched tolerantly because the
    /// prose around it is free to wrap or emphasise the command.
    /// </summary>
    private static readonly Regex RunStartCommand = new(
        @"kyber-weave\s+run\s+start\b",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static string ProductRoot =>
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");

    /// <summary>The shipped default response guidance: the one owner of the response shape.</summary>
    private static string ResponseGuidanceDefaultPath =>
        Path.Combine(ProductRoot, "response-guidance.default.md");

    /// <summary>
    /// Reads the shipped default guidance, asserting its absence first so a missing file reports the
    /// missing artifact rather than an I/O exception.
    /// </summary>
    private static string ReadResponseGuidanceDefault()
    {
        Assert.True(
            File.Exists(ResponseGuidanceDefaultPath),
            $"The shipped response-guidance default must live at '{ResponseGuidanceDefaultPath}'.");
        return File.ReadAllText(ResponseGuidanceDefaultPath);
    }

    /// <summary>
    /// Resolves an MSBuild item path relative to the project directory, expanding the
    /// <c>$(MSBuildThisFileDirectory)</c> property and normalising Windows separators.
    /// </summary>
    private static string ResolveProjectPath(string projectDirectory, string item)
    {
        return Path.Combine(
            projectDirectory,
            item.Replace("$(MSBuildThisFileDirectory)", string.Empty, StringComparison.Ordinal)
                .Replace('\\', Path.DirectorySeparatorChar));
    }

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
        int docs = discovery.IndexOf("docs_answer_question_from_repo_documentation", StringComparison.Ordinal);
        int directRead = discovery.IndexOf("Read or search a file directly", StringComparison.Ordinal);

        Assert.True(code >= 0 && docs >= 0 && directRead > code && directRead > docs,
            $"{agentName}: index discovery must precede direct-read authorization.");
        Assert.Contains("docs_find_documents_citing_code_symbol", discovery, StringComparison.Ordinal);
        Assert.Contains("docs_look_up_glossary_term_definition", discovery, StringComparison.Ordinal);
        if (agentName is "review-lens" or "review-triage")
        {
            Assert.Contains("Discovery: codegraph N, docs N, direct reads N", discovery, StringComparison.Ordinal);
            Assert.DoesNotContain("Discovery reporting is exempt", discovery, StringComparison.Ordinal);
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

    /// <summary>
    /// Reads a canonical product file, asserting its presence first so a moved or missing artifact
    /// reports the absent path rather than an I/O exception.
    /// </summary>
    private static string ReadProductFile(string relativePath)
    {
        string path = Path.Combine(ProductRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"Expected the canonical product file at '{path}'.");
        return File.ReadAllText(path);
    }

    /// <summary>The blank-line separated blocks of a document, so an assertion reads a whole instruction.</summary>
    private static string[] Paragraphs(string text)
    {
        return text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>The trimmed lines that name a <c>kyber-weave review gates</c> command.</summary>
    private static string[] ReviewCommandLines(string text)
    {
        return
        [
            .. text.Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.Contains("kyber-weave review gates", StringComparison.Ordinal))
        ];
    }

    /// <summary>
    /// The paragraph that opens with <paramref name="lead"/>, or empty when the document has none.
    /// </summary>
    private static string ParagraphStartingWith(string text, string lead)
    {
        string[] lines = text.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
        for (int i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith(lead, StringComparison.Ordinal))
            {
                continue;
            }

            List<string> paragraph = [lines[i].Trim()];
            for (int j = i + 1; j < lines.Length; j++)
            {
                if (lines[j].Length == 0)
                {
                    break;
                }

                paragraph.Add(lines[j].Trim());
            }

            return string.Join(' ', paragraph);
        }

        return string.Empty;
    }

    /// <summary>
    /// The list item that opens with <paramref name="lead"/>, including continuation lines, or
    /// null when the document has none.
    /// </summary>
    private static string? ListItemStartingWith(string guidance, string lead)
    {
        string[] lines = guidance.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
        for (int i = 0; i < lines.Length; i++)
        {
            if (!lines[i].TrimStart().StartsWith(lead, StringComparison.Ordinal))
            {
                continue;
            }

            List<string> item = [lines[i].Trim()];
            for (int j = i + 1; j < lines.Length; j++)
            {
                if (lines[j].Length == 0 || lines[j].TrimStart().StartsWith("- ", StringComparison.Ordinal))
                {
                    break;
                }

                item.Add(lines[j].Trim());
            }

            return string.Join(' ', item);
        }

        return null;
    }

    /// <summary>True when <paramref name="text"/> uses the shape's own vocabulary, ignoring emphasis.</summary>
    private static bool RestatesTheShape(string text)
    {
        return RestatedShapeRule.IsMatch(WithoutEmphasis(text));
    }

    /// <summary>
    /// Each place <paramref name="text"/> uses the shape's own vocabulary, as <c>path:line: "phrase"</c>.
    /// Emphasis markers are removed without touching a newline, so the line numbers still count the
    /// lines of the file as written.
    /// </summary>
    private static IEnumerable<string> RestatedShapeHits(string relativePath, string text)
    {
        string plain = WithoutEmphasis(text);
        foreach (Match match in RestatedShapeRule.Matches(plain))
        {
            int line = plain.AsSpan(0, match.Index).Count('\n') + 1;
            yield return $"{relativePath}:{line}: \"{match.Value}\"";
        }
    }

    /// <summary>
    /// <paramref name="text"/> with Markdown emphasis markers removed, so a bold or inline-code rendering
    /// of a sentence compares equal to the plain one. Only presentation is dropped: the words, their
    /// order, the case, and the closing full stop all still have to match.
    /// </summary>
    private static string WithoutEmphasis(string text)
    {
        return text.Replace("*", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("`", string.Empty, StringComparison.Ordinal);
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
}
