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
    private const string MigrationSchema = "kyber-squad.migration/v1";
    private const string MigrationSourceCommit = "677c3a876ba9c62f1083608596b238c9deaff167";
    private const string DefaultOrchestrationTrigger = "default entry point";

    /// <summary>
    /// The shared worker execution contract, relative to the canonical product root. It is a canonical
    /// skill rather than a conductor reference so a fallback target receives it, and so bug-crusher can
    /// load it without linking out of its own directory.
    /// </summary>
    private const string DeliveryExecutionSkillRelativePath = "skills/delivery-execution/SKILL.md";

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

    /// <summary>
    /// The no-label rule: a negation, a labelling verb, and "format", all in one sentence on one line.
    /// </summary>
    /// <remarks>
    /// The confinement is the assertion, not a convenience. With a line break inside the window a
    /// negation in one bullet and the word "format" in the next match each other, so the pin would pass
    /// on prose that forbids nothing. The windows are sized to the skill's own sentence — "A response
    /// never labels, names, mentions, or refers to the format it follows." — rather than left wide
    /// enough to reach a neighbouring one.
    /// </remarks>
    private static readonly Regex NoFormatLabelRule = new(
        @"\b(?:never|must not|do not|does not)\b[^.!?\n]{0,60}\b(?:labels?|names?|mentions?|tags?|announces?|refers?)\b[^.!?\n]{0,30}\bformat\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

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

    /// <summary>Wording that says a single gate is checked again, rather than the whole suite being re-run.</summary>
    private static readonly Regex RecheckWording = new(
        @"re-?check|re-?run|single gate|one gate|only that gate",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// The <c>--gate</c> option as a whole token. A plain substring search also matches <c>--gates</c>, which is a
    /// different option on <c>review verdict</c>.
    /// </summary>
    private static readonly Regex GateOptionMention = new(
        @"(?<![\w-])--gate(?![\w-])",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Wording that says the review is the last thing to run and runs alone.</summary>
    private static readonly Regex AloneWording = new(
        @"\b(alone|last|nothing else)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Wording that says the waiting session takes the seats and comes back to the reviewer.</summary>
    private static readonly Regex ReinvokeWording = new(
        @"re-?invok|invoke (it|the reviewer) again|main session",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// The opening of the one bullet that tells an agent to set the session title, verbatim and
    /// including the Markdown emphasis, so the requirement below is read from the instruction it governs.
    /// </summary>
    private const string SessionTitleBulletLead = "- **Name the session after the work.**";

    /// <summary>The session-title bullet must tell the agent to set the title. The title is required.</summary>
    private const string SessionTitleSetInstruction = "Set the session title to name the work being done";

    /// <summary>Once a pull request exists, the session title is that pull request's number and title.</summary>
    private const string PrTitleSetInstruction =
        "Once a PR is submitted, set the session title to the PR title prefixed by the Pull Request number";

    /// <summary>The form the pull-request bullet has to show: the number, a space, then the title.</summary>
    private const string PrTitleExample = "#123 Add the widget";

    /// <summary>A later retitle has to move the session title with it.</summary>
    private const string PrTitleRetitleInstruction =
        "When the pull request title changes, update the session title to match";

    /// <summary>
    /// The architecture matrix's submit sentence. The product bullet words that moment
    /// differently, so the matrix pin cannot reuse <see cref="PrTitleSetInstruction"/>.
    /// </summary>
    private const string SessionTitleMatrixSubmitInstruction = "Once a pull request is submitted";

    /// <summary>
    /// Factory has no setter. The sentence that tells the agent to set or update a title names that
    /// limit; a following sentence does not constrain the instruction the agent just read.
    /// </summary>
    private const string FactorySessionTitleException = "on every harness except Factory";

    /// <summary>
    /// How Factory gets a title. A general skip is not this clause, and this clause is not a reason
    /// to skip the title on any other harness.
    /// </summary>
    private const string FactorySessionTitleMechanism =
        "derives the session title from the conversation's first message and has no setter";

    /// <summary>
    /// Phrases that tell an agent not to set a session title. The owner removed them: a shipped
    /// default that carries one is instructing the agent to skip the title.
    /// </summary>
    private static readonly string[] SessionTitleHedges =
    [
        "never fabricate a title",
        "leave it alone",
        "only where the harness exposes a confirmed way to set it"
    ];

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
    /// A specialist digest relayed to the human is presented in the response format. Either order
    /// of the two clauses in a sentence satisfies it.
    /// </summary>
    private static readonly Regex RelayRule = new(
        @"\brelay\w*\b[^.]{0,240}<response-preferences>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// The plan path taken because the assessment is off is a routing decision the user is entitled
    /// to see and to redirect. A conductor that routes silently leaves no room for the user to ask
    /// for a specification instead.
    /// </summary>
    private const string PlanPathAnnouncementSentence =
        "When you take the plan path because the assessment is off, say so in one line and tell the user they can ask for a specification instead; never route silently.";

    /// <summary>
    /// The response format shapes the presentation of a relayed digest, never its content: every
    /// marker, id, path, count, gap and open question the specialist returned survives the relay.
    /// </summary>
    private const string VerbatimRelaySentence =
        "Change the presentation only: keep every status marker, id, path, count, gap, open question and evidence value exactly as the specialist returned it, and never drop, merge or paraphrase an item.";

    /// <summary>
    /// A diagnosed defect with no stated artifact choice reaches the architect's intake recommendation
    /// only when the repository opted in. The diagnosis route is a second door into the same assessment,
    /// so it carries the same gate as the assessment itself.
    /// </summary>
    private const string GatedDiagnosisRecommendationSentence =
        "If choice is UNSTATED and the repository publishes <intake-assessment>, send the diagnosis to architect for an intake recommendation, then obtain the user's artifact choice.";

    /// <summary>Without the opt-in, the same request takes the plan path with its diagnosis attached.</summary>
    private const string DefaultedDiagnosisRouteSentence =
        "If choice is UNSTATED and that property is absent, skip the intake recommendation and proceed to Promotion with the plan choice and the diagnosis attached, saying so as the Assessment section directs.";

    /// <summary>An absent intake property skips the assessment and takes the plan path directly.</summary>
    private static readonly Regex IntakeAbsentRule = new(
        @"\b(?:absent|not present|missing|omitted)\b[^.]{0,240}\b(?:skip|no assessment|without an assessment|plan path)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>A present intake property runs the assessment.</summary>
    private static readonly Regex IntakePresentRule = new(
        @"\b(?:present|enabled|published)\b[^.]{0,240}\b(?:run|perform|apply|execute|conduct)\w*\b[^.]{0,80}\bassessment\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// The assessment is off by default: it runs only when the resolved registry publishes the
    /// property, so a repository that never set the flag keeps the behaviour it had.
    /// </summary>
    private static readonly Regex DefaultOffRule = new(
        @"(?:\bdefault[-\s]off\b)|(?:\bskipped\s+unless\b)|(?:\babsent\b[^.]{0,160}\bskip\w*\b)|(?:\bunless\b[^.]{0,160}\bpresent\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

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
    /// The product README is the human-readable skill roster. It must not list the retired skill in its tree or
    /// its canonical-skill counts, and it must state the count the tree actually holds: 29, the 28 left after
    /// the retired <c>response-format</c> skill plus <c>delivery-execution</c>.
    /// </summary>
    [Fact]
    public void ProductReadmeRosterLacksSecondBrain()
    {
        string readme = File.ReadAllText(Path.Combine(ProductRoot, "README.md"));

        Assert.DoesNotContain("second-brain", readme, StringComparison.Ordinal);
        Assert.Contains("29 canonical skill", readme, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("28 canonical skill", readme, StringComparison.OrdinalIgnoreCase);
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
    /// skill that no longer deploys. The conductor's migration record keeps one historical note about it, so
    /// that file is the only one exempt.
    /// </summary>
    [Fact]
    public void NoProductFileNamesTheRetiredResponseFormatSkill()
    {
        string historicalNote = Path.GetFullPath(Path.Combine(ProductRoot, "migration", "conductor.md"));
        string[] offenders = Directory
            .EnumerateFiles(ProductRoot, "*", SearchOption.AllDirectories)
            .Where(path => !string.Equals(Path.GetFullPath(path), historicalNote, StringComparison.Ordinal))
            .Where(path => File.ReadAllText(path).Contains("response-format", StringComparison.Ordinal))
            .Select(RelativeSourcePath)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "These product files still name the retired 'response-format' skill: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// The order of the three steps is the format. The shipped default owns it, so a default that opens with a
    /// bottom line, or ends without its open decisions, is a different contract. The sequence is pinned rather
    /// than the presence of the three steps anywhere in the file.
    /// </summary>
    /// <remarks>
    /// Each step is asserted as the bold list item the default states. Verified by mutation: swapping the title
    /// step and the open-decisions step must fail this test, and so must removing either.
    /// </remarks>
    [Fact]
    public void ResponseGuidanceOrdersTitleThenBottomLineThenOpenDecisions()
    {
        string guidance = ReadResponseGuidanceDefault();

        const string TitleMarker = "**Title first.**";
        const string BottomLineMarker = "**Then the bottom line, in at most three sentences**";
        const string OpenDecisionsMarker = "**Then what needs a decision**";
        int title = guidance.IndexOf(TitleMarker, StringComparison.Ordinal);
        int bottomLine = guidance.IndexOf(BottomLineMarker, StringComparison.Ordinal);
        int openDecisions = guidance.IndexOf(OpenDecisionsMarker, StringComparison.Ordinal);

        Assert.True(
            title >= 0,
            $"The shipped default must open its ordered steps with '{TitleMarker}'.");
        Assert.True(
            bottomLine >= 0,
            $"The shipped default must carry the '{BottomLineMarker}' step, which caps the bottom line at three sentences.");
        Assert.True(
            openDecisions >= 0,
            $"The shipped default must end its ordered steps with '{OpenDecisionsMarker}'.");
        Assert.True(
            bottomLine > title,
            $"The shipped default must order '{BottomLineMarker}' after '{TitleMarker}'.");
        Assert.True(
            openDecisions > bottomLine,
            $"The shipped default must order '{OpenDecisionsMarker}' after '{BottomLineMarker}'.");
    }

    /// <summary>
    /// The format is invisible: a reader should see a well-shaped answer, never an agent telling them which
    /// format it is using. The shipped default states this rule, and every deployed entry receives it.
    /// </summary>
    [Fact]
    public void TheShippedDefaultForbidsLabellingAResponseWithItsFormat()
    {
        string guidance = ReadResponseGuidanceDefault();

        Assert.True(
            NoFormatLabelRule.IsMatch(guidance),
            "The shipped default must state, in one sentence, that a response never labels, names, or refers to " +
            "the format it is following.");
    }

    /// <summary>
    /// The no-label rule is only a rule if it stays inside one sentence. Prose that forbids nothing —
    /// a negation in one bullet and the word "format" in the next — must not satisfy it, or the pin
    /// passes on a skill whose guidance never says the format is invisible.
    /// </summary>
    /// <remarks>
    /// Both controls below are ones a window that admits a line break or an exclamation mark accepts:
    /// bullets need no full stop, and a sentence may end in "!". They are the shapes that made the
    /// earlier rule match two unrelated statements.
    /// </remarks>
    [Fact]
    public void NoFormatLabelRuleDoesNotCrossASentenceOrALineBreak()
    {
        Assert.False(
            NoFormatLabelRule.IsMatch("- **Never label the answer**\n- **The format speaks for itself**\n"),
            "A negation in one bullet and the word 'format' in the next must not satisfy the rule, " +
            "even though neither bullet ends with a full stop.");
        Assert.False(
            NoFormatLabelRule.IsMatch("Never label the answer! The format speaks for itself.\n"),
            "An exclamation mark ends the sentence as surely as a full stop, so the rule must not " +
            "reach the next one.");
        Assert.True(
            NoFormatLabelRule.IsMatch(
                "A response never labels, names, mentions, or refers to the format it follows.\n"),
            "The rule must still match the skill's own sentence, which states it in one line.");
    }

    /// <summary>
    /// The owner requires a session title. The shipped default must tell the agent to set one, and
    /// must not say to leave it alone, to skip a harness that might not expose a setter, or to avoid
    /// fabricating a title. Factory is the one harness with no setter: each instruction sentence
    /// names that limit and how Factory derives the title.
    /// </summary>
    [Fact]
    public void TheShippedDefaultRequiresTheSessionTitleToBeSet()
    {
        string guidance = ReadResponseGuidanceDefault();

        Assert.True(
            RequiresSessionTitle(guidance),
            "The session-title bullet in the shipped default must tell the agent to set the session title " +
            $"('{SessionTitleSetInstruction}').");
        AssertShippedDefaultOmitsSessionTitleHedges(guidance);
        AssertShippedDefaultStatesTheFactorySessionTitleLimit(guidance);
    }

    /// <summary>
    /// "Leave it alone" in some other bullet is not a session-title hedge. The pin reads the
    /// session-title bullets, so an unrelated use of those words must not fail it.
    /// </summary>
    [Fact]
    public void AnUnrelatedBulletThatSaysLeaveItAloneDoesNotFailTheSessionTitlePin()
    {
        string guidance =
            $"{SessionTitleBulletLead} {SessionTitleSetInstruction} {FactorySessionTitleException}, " +
            $"which {FactorySessionTitleMechanism}.\n" +
            $"- **PR Title.** {PrTitleSetInstruction}: `{PrTitleExample}`. " +
            $"{PrTitleRetitleInstruction} {FactorySessionTitleException}, which {FactorySessionTitleMechanism}.\n" +
            "- **Close the note.** When the note is finished, leave it alone.\n";

        Assert.True(
            SessionTitleBulletsOmitHedges(guidance, out string? offending),
            "An unrelated 'leave it alone' must not fail the session-title pin " +
            $"(the pin reported '{offending}').");
    }

    /// <summary>
    /// A session-title bullet that tells the agent to skip the title in general still fails. An
    /// unrelated bullet that uses the same words does not make that skip acceptable.
    /// </summary>
    [Fact]
    public void ASessionTitleBulletThatSaysToLeaveTheTitleAloneStillFailsThePin()
    {
        string guidance =
            $"{SessionTitleBulletLead} {SessionTitleSetInstruction}. Where no agent can set one, leave it alone.\n" +
            "- **Close the note.** When the note is finished, leave it alone.\n";

        Assert.False(
            SessionTitleBulletsOmitHedges(guidance, out string? offending),
            "A session-title bullet that says to leave the title alone must fail the pin.");
        Assert.Equal("leave it alone", offending);
    }

    /// <summary>
    /// The architecture matrix states the same rule as the shipped default. The set, submit, and
    /// retitle sentences each name Factory. Dropping the limit from the submit sentence does not.
    /// </summary>
    [Fact]
    public void TheSessionTitleMatrixStatesHowFactoryGetsItsTitle()
    {
        string path = Path.Combine(
            KyberWeaveTestPaths.ToolRoot, "docs", "kyber-squad", "architecture.md");
        string matrix = ParagraphStartingWith(
            File.ReadAllText(path),
            "**Session-title matrix (reference).**");

        Assert.True(
            matrix.Length > 0,
            "docs/kyber-squad/architecture.md must carry the session-title matrix.");
        AssertSentenceCarriesTheFactoryLimit(matrix, "name the work being done");
        AssertSentenceCarriesTheFactoryLimit(matrix, SessionTitleMatrixSubmitInstruction);
        AssertSentenceCarriesTheFactoryLimit(matrix, PrTitleRetitleInstruction);
        Assert.Contains("#123 Add the widget", matrix, StringComparison.Ordinal);
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
    /// The shipped default states the session-title rule itself: name the session after the work
    /// by setting the session title. The rule lives in the default now that the skill that used
    /// to carry it is being removed.
    /// </summary>
    [Fact]
    public void TheShippedDefaultStatesTheSessionTitleRule()
    {
        string guidance = ReadResponseGuidanceDefault();
        string bullet = SessionTitleBullet(guidance) ?? string.Empty;

        Assert.True(
            bullet.Length > 0,
            $"The shipped default must state the session-title rule as the bullet '{SessionTitleBulletLead}'.");
        Assert.True(
            bullet.Contains("title", StringComparison.OrdinalIgnoreCase) &&
            bullet.Contains("work", StringComparison.OrdinalIgnoreCase),
            "The session-title bullet must say the title reflects the work.");
        Assert.Contains(SessionTitleSetInstruction, bullet, StringComparison.Ordinal);
    }

    /// <summary>
    /// The requirement sits on the session-title bullet. A neighbouring bullet that says to set a
    /// title, or a line that is not the emphasised bullet, does not satisfy it, and a bullet that
    /// also says to leave the title alone does not either.
    /// </summary>
    [Fact]
    public void TheSessionTitleRequirementSitsOnTheSessionTitleBullet()
    {
        string requiredElsewhere =
            "- **Set the session title to name the work being done.** Say what happened instead.\n" +
            $"{SessionTitleBulletLead} where it helps the reader.\n";
        string required =
            $"{SessionTitleBulletLead} {SessionTitleSetInstruction}\n";
        string hedged =
            $"{SessionTitleBulletLead} {SessionTitleSetInstruction} Where no agent can set one, leave it alone.\n";

        Assert.False(
            RequiresSessionTitle(requiredElsewhere),
            "An instruction to set the title in another bullet must not satisfy a session-title bullet that omits it.");
        Assert.True(
            RequiresSessionTitle(required),
            "A session-title bullet that tells the agent to set the title must satisfy the pin.");
        Assert.False(
            RequiresSessionTitle(hedged),
            "A session-title bullet that says to leave the title alone must not satisfy the pin.");
        Assert.False(
            RequiresSessionTitle($"- Name the session after the work. {SessionTitleSetInstruction}\n"),
            "A line that is not the emphasised session-title bullet must not satisfy the pin.");
    }

    /// <summary>
    /// The pull-request title bullet tells an agent to set the session title after submit, in the
    /// form <c>#&lt;number&gt; &lt;PR title&gt;</c>, and to move that title when the pull request
    /// is renamed. A retitle sentence that tells the agent to skip or not to fabricate does not
    /// satisfy the pin: the owner requires the title to be set.
    /// </summary>
    [Fact]
    public void ThePrTitleBulletSetsTheSessionTitleAndTracksRetitles()
    {
        string guidance = ReadResponseGuidanceDefault();

        Assert.True(
            PrTitleBulletRequiresTheSessionTitle(guidance),
            "The shipped default must set the session title from the submitted pull request " +
            $"('{PrTitleSetInstruction}'), show '{PrTitleExample}', and update it when the pull request title changes.");
    }

    /// <summary>
    /// A hedge on the retitle sentence alone must fail. A whole-bullet search that only looks for
    /// the retitle phrase would stay green while that sentence tells the agent not to set the title.
    /// </summary>
    [Fact]
    public void ARetitleSentenceThatTellsTheAgentNotToSetTheTitleDoesNotSatisfyThePin()
    {
        string hedged =
            "- **PR Title.** Once a PR is submitted, set the session title to the PR title " +
            "prefixed by the Pull Request number: `#<number>` immediately followed by a space and " +
            "the PR title, as in `#123 Add the widget` when the PR title is `Add the widget` and " +
            "the PR number is 123. When the pull request title changes, update the session title " +
            "to match only where the harness exposes a confirmed way to set it, and never fabricate a title.\n";

        Assert.False(
            PrTitleBulletRequiresTheSessionTitle(hedged),
            "A retitle sentence that says to skip the title or not to fabricate one must not satisfy the pin.");
    }

    /// <summary>
    /// A specialist digest relayed to the human is presented in the shape the response-preferences block sets.
    /// Relay is the boundary where a worker's digest becomes a response, so the conductor contract has to say so.
    /// </summary>
    [Fact]
    public void ConductorRelaysSpecialistDigestsInTheResponsePreferences()
    {
        string contract = ReadAgentContract("conductor").Replace("`", string.Empty, StringComparison.Ordinal);

        Assert.True(
            RelayRule.IsMatch(contract),
            "The conductor contract must state that a specialist digest surfaced to the human is presented in " +
            "the shape set by '<response-preferences>'.");
    }

    /// <summary>
    /// The intake assessment is opt-in. It runs when configuration publishes the
    /// <c>&lt;intake-assessment&gt;</c> property; a repository that never set the flag keeps the plan
    /// path it always had. Defect-first precedence is unchanged, because a concrete failure still
    /// outranks the question of which artifact to write.
    /// </summary>
    [Fact]
    public void ConductorResolvesTheIntakeAssessmentOnlyWhenConfigPublishesIt()
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
        Assert.True(
            IntakeAbsentRule.IsMatch(conductor),
            "The '<intake-assessment>' resolution in 'agents/conductor.md' must state that an absent " +
            "property skips the assessment and takes the plan path directly.");
        Assert.True(
            IntakePresentRule.IsMatch(conductor),
            "The '<intake-assessment>' resolution in 'agents/conductor.md' must state that a present " +
            "property runs the assessment.");
    }

    /// <summary>
    /// The plan path taken because the assessment is off is still a routing decision, and the user
    /// is entitled to see it and to redirect it. A conductor that routes silently leaves no room for
    /// the user to ask for a specification instead.
    /// </summary>
    [Fact]
    public void ConductorAnnouncesThePlanPathItTakesBecauseTheAssessmentIsOff()
    {
        string conductor = File.ReadAllText(Path.Combine(ProductRoot, "agents", "conductor.md"));
        string resolution = BlockContaining(conductor, "skip the assessment and take the plan path directly");

        Assert.True(
            resolution.Length > 0,
            "'agents/conductor.md' must resolve the absent '<intake-assessment>' property in one block, " +
            "so the announcement can be pinned to the resolution that takes the plan path.");
        Assert.True(
            SentenceLine(resolution, PlanPathAnnouncementSentence) is not null,
            "'agents/conductor.md' must announce, next to the intake resolution, that a plan path " +
            "taken because the assessment is off is said out loud and offers a specification instead: " +
            $"\"{PlanPathAnnouncementSentence}\"");
    }

    /// <summary>
    /// The intake path is where the plan path is defined, so the announcement belongs there too: two
    /// files that describe the same default-off routing must not disagree about telling the user.
    /// </summary>
    [Fact]
    public void IntakePathAnnouncesThePlanPathItTakesBecauseTheAssessmentIsOff()
    {
        string intakePath = File.ReadAllText(
            Path.Combine(ProductRoot, "agents", "conductor", "references", "intake-path.md"));
        string assessment = BlockContaining(intakePath, "The assessment is default-off.");

        Assert.True(
            assessment.Length > 0,
            "'agents/conductor/references/intake-path.md' must record the default-off assessment in one " +
            "block, so the announcement can be pinned to the paragraph that states the default.");
        Assert.True(
            SentenceLine(assessment, PlanPathAnnouncementSentence) is not null,
            "'agents/conductor/references/intake-path.md' must announce a plan path taken because the " +
            "assessment is off, so it does not disagree with 'agents/conductor.md': " +
            $"\"{PlanPathAnnouncementSentence}\"");
    }

    /// <summary>
    /// The intake path states the default-off assessment twice: once as the rule, and once on the route
    /// a diagnosed defect takes. The second must not send an unstated choice to the architect's intake
    /// recommendation unconditionally, or a repository that never opted in gets the assessment anyway.
    /// </summary>
    [Fact]
    public void DefectDiagnosisSendsAnUnstatedChoiceToTheAssessmentOnlyWhenTheRepositoryOptedIn()
    {
        string intakePath = File.ReadAllText(
            Path.Combine(ProductRoot, "agents", "conductor", "references", "intake-path.md"));
        string route = BlockContaining(intakePath, "Without existing Ready authority");

        Assert.True(
            route.Length > 0,
            "'agents/conductor/references/intake-path.md' must state the route a diagnosed defect takes " +
            "without Ready authority in one block, so the opt-in gate can be pinned to that route.");
        Assert.True(
            SentenceLine(route, GatedDiagnosisRecommendationSentence) is not null,
            "'agents/conductor/references/intake-path.md' must send an unstated choice to the architect's " +
            "intake recommendation only when the repository publishes the property: " +
            $"\"{GatedDiagnosisRecommendationSentence}\"");
        Assert.True(
            SentenceLine(route, DefaultedDiagnosisRouteSentence) is not null,
            "'agents/conductor/references/intake-path.md' must say what an unstated choice does when the " +
            $"property is absent: \"{DefaultedDiagnosisRouteSentence}\"");
        Assert.DoesNotContain(
            "If choice is UNSTATED, send the diagnosis",
            WithoutEmphasis(route),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The gate pins are worth having only if they can fail. The ungated route they replaced, a gate
    /// sentence wrapped across two lines, and a gate sentence carried outside the route all have to
    /// leave them red.
    /// </summary>
    [Fact]
    public void TheDiagnosisRoutePinsRejectTheUngatedWrappedOrMisplacedRoute()
    {
        Assert.True(
            SentenceLine(
                "If choice is UNSTATED, send the diagnosis to architect for an intake recommendation, then obtain the user's artifact choice.\n",
                GatedDiagnosisRecommendationSentence) is null,
            "The route without the opt-in gate must not satisfy the gated pin.");
        Assert.True(
            SentenceLine(
                "If choice is UNSTATED and the repository publishes <intake-assessment>, send the diagnosis to architect\n" +
                "for an intake recommendation, then obtain the user's artifact choice.\n",
                GatedDiagnosisRecommendationSentence) is null,
            "A gate sentence wrapped across two lines is not the instruction the pin looks for.");
        Assert.True(
            SentenceLine(
                BlockContaining(
                    "## Elsewhere\n\n" + GatedDiagnosisRecommendationSentence + "\n",
                    "Without existing Ready authority"),
                GatedDiagnosisRecommendationSentence) is null,
            "A gate sentence outside the route a diagnosed defect takes must not satisfy the pin.");
        Assert.True(
            SentenceLine(GatedDiagnosisRecommendationSentence + "\n", GatedDiagnosisRecommendationSentence) is not null,
            "The pin must still accept the gate sentence exactly as the intake path states it.");
        Assert.True(
            SentenceLine(DefaultedDiagnosisRouteSentence + "\n", DefaultedDiagnosisRouteSentence) is not null,
            "The pin must still accept the default sentence exactly as the intake path states it.");
    }

    /// <summary>
    /// The response format shapes the presentation of a relayed digest, never its content. Every
    /// marker, id, path, count, gap and open question the specialist returned has to survive the
    /// relay to the human.
    /// </summary>
    [Fact]
    public void ConductorRelaysSpecialistDigestsWithoutChangingTheirContent()
    {
        string conductor = File.ReadAllText(Path.Combine(ProductRoot, "agents", "conductor.md"));
        string relay = BlockContaining(conductor, "Relay every specialist digest you surface to the human");

        Assert.True(
            relay.Length > 0,
            "'agents/conductor.md' must state the digest relay rule in one block, so the verbatim " +
            "constraint can be pinned to the rule that governs it.");
        Assert.True(
            SentenceLine(relay, VerbatimRelaySentence) is not null,
            "'agents/conductor.md' must state, in the relay rule, that only the presentation changes " +
            "and every relayed item survives: " +
            $"\"{VerbatimRelaySentence}\"");
    }

    /// <summary>
    /// The conductor pins are worth having only if they can fail. A sentence wrapped across two
    /// lines, a sentence that dropped an item, and a sentence carried in a different part of the
    /// file must each leave the pin red.
    /// </summary>
    [Fact]
    public void TheConductorPinsRejectAWrappedRewordedOrMisplacedSentence()
    {
        Assert.True(
            SentenceLine(
                "When you take the plan path because the assessment is off, say so in one line and tell\n" +
                "the user they can ask for a specification instead; never route silently.\n",
                PlanPathAnnouncementSentence) is null,
            "An announcement split across two lines is not the instruction the pin looks for.");
        Assert.True(
            SentenceLine(
                "When you take the plan path because the assessment is off, say so in one line; never " +
                "route silently.\n",
                PlanPathAnnouncementSentence) is null,
            "An announcement that dropped the specification offer must not satisfy the pin.");
        Assert.True(
            SentenceLine(
                "Change the presentation only: keep every status marker, id, path and count exactly as " +
                "the specialist returned it.\n",
                VerbatimRelaySentence) is null,
            "A relay rule that dropped the gap, open question and evidence clauses must not satisfy the pin.");
        Assert.True(
            SentenceLine(
                BlockContaining(
                    "## Elsewhere\n\n" + VerbatimRelaySentence + "\n",
                    "Relay every specialist digest you surface to the human"),
                VerbatimRelaySentence) is null,
            "A relay constraint outside the relay rule must not satisfy the pin.");
        Assert.True(
            SentenceLine(VerbatimRelaySentence + "\n", VerbatimRelaySentence) is not null,
            "The pin must still accept the sentence exactly as the conductor states it.");
    }

    /// <summary>
    /// The default is recorded where the intake path is defined, so a reader cannot conclude the
    /// assessment is something the conductor always performs.
    /// </summary>
    [Fact]
    public void IntakePathRecordsThatTheAssessmentIsOffByDefault()
    {
        string intakePath = File.ReadAllText(
            Path.Combine(ProductRoot, "agents", "conductor", "references", "intake-path.md"));

        Assert.True(
            intakePath.Contains("<intake-assessment>", StringComparison.Ordinal),
            "'agents/conductor/references/intake-path.md' must record the '<intake-assessment>' gate " +
            "the plan path depends on.");
        Assert.True(
            DefaultOffRule.IsMatch(intakePath),
            "'agents/conductor/references/intake-path.md' must record the default-off semantics: the " +
            "assessment is skipped unless the '<intake-assessment>' property is present.");
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
        string defaultGuidance = ReadResponseGuidanceDefault();
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

        Assert.True(
            RestatesTheShape(defaultGuidance),
            "The shipped default guidance must be the one owner of the response shape, so it must carry the shape's vocabulary.");
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
    /// The restatement pin is worth having only if it can fail. It has to see the shape in the skill that
    /// owns it, in each way the bug-crusher skill once spelled it out, and across a line wrap, and it has
    /// to let through a surface that only binds the skill by name.
    /// </summary>
    [Fact]
    public void TheRestatedShapePinSeesEveryWayTheShapeIsSpelledOut()
    {
        Assert.True(
            RestatesTheShape(ReadResponseGuidanceDefault()),
            "The pin must see the shape in the shipped default that owns it; a detector blind to its owner is blind everywhere.");
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

    /// <summary>
    /// The conductor's iteration circuit-breaker: the failure-cluster key and tally, the two-dispatch
    /// retry limit, oscillation detection, and the closed <c>CIRCUIT_BREAKER_TRIGGER</c> vocabulary.
    /// The rules live in the shared execution contract both entry points load, so that skill is the
    /// surface asserted here rather than the conductor's own body.
    /// </summary>
    [Fact]
    public void LoadConductorDefinesIterationCircuitBreakerAndOscillationDetection()
    {
        string contract = ReadProductFile(DeliveryExecutionSkillRelativePath);

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
    /// agent, so the conductor records and resumes exactly one session per task. The session rules
    /// live in the shared execution contract, so that skill is the surface asserted here.
    /// </summary>
    [Fact]
    public void ConductorRetainsOnlyWorkerSessionsForTaskRework()
    {
        string contract = ReadProductFile(DeliveryExecutionSkillRelativePath);

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
        Assert.Contains("2-cycle cap", skill, StringComparison.Ordinal);
        Assert.Contains("ITERATION_CAP_EXCEEDED", skill, StringComparison.Ordinal);
        Assert.Contains("task-gate/<gate-id>", skill, StringComparison.Ordinal);
        Assert.Contains("TASK_GATE: PASS | FAIL | NONE_DECLARED | NONE_APPLICABLE | UNAVAILABLE", skill,
            StringComparison.Ordinal);
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

    /// <summary>
    /// The architect writes each worker's prompt, and the conductor pastes it unchanged. The
    /// authoring contract is therefore the only place a prompt's quality is decided, so it has
    /// to ask for the three things this programme kept shipping prompts without: a file named
    /// by path plus symbol rather than a guess at a path, the fully qualified test class the
    /// filter must match, and the exact command to run. A filter naming a class that does not
    /// exist runs zero tests and exits success.
    /// </summary>
    [Fact]
    public void PlanAuthoringRequiresPathPlusSymbolTestClassNamesAndExactCommands()
    {
        string planAuthoring = ReadProductFile("agents/architect/references/plan-authoring.md");

        Assert.Matches(@"(?i)\bpath\s+(?:plus|and)\s+(?:a\s+)?symbol\b", planAuthoring);
        Assert.Matches(@"(?i)\bfull(?:y\s+qualified)?\s+test\s+class\s+names?\b", planAuthoring);
        Assert.Matches(@"(?i)\bexact\s+commands?\b", planAuthoring);
    }

    /// <summary>
    /// Changing one assertion took a fresh worker eleven minutes because it had to rebuild the
    /// context an earlier worker already had. A `session` group is how the architect says that a
    /// follow-on fix belongs in the session that did the work — never a RED folded into its own
    /// GREEN, which stays two workers.
    /// </summary>
    [Fact]
    public void PlanAuthoringRequiresASessionGroupForFollowOnWork()
    {
        string planAuthoring = ReadProductFile("agents/architect/references/plan-authoring.md");

        Assert.Matches(@"(?i)\bsession\s+group\b", planAuthoring);
    }

    /// <summary>
    /// The task file is written at finalization, so the conductor learns it exists from the
    /// marker that ends the architect's turn. The instruction has to say both halves: save the
    /// file, and return the path the conductor will validate.
    /// </summary>
    [Fact]
    public void ArchitectFinalizationSavesTheTaskFileAndReturnsItsPath()
    {
        string[] surfaces =
        [
            "agents/architect.md",
            "agents/architect/references/plan-authoring.md"
        ];
        string[] paragraphs = [.. surfaces.SelectMany(surface => Paragraphs(ReadProductFile(surface)))];

        string? saving = paragraphs.FirstOrDefault(paragraph =>
            paragraph.Contains(".tasks.json", StringComparison.Ordinal)
            && Regex.IsMatch(paragraph, @"(?i)\bsav(?:e|es|ed|ing)\b")
            && Regex.IsMatch(paragraph, @"(?i)\b(?:return|report|hand)s?\b")
            && Regex.IsMatch(paragraph, @"(?i)\bpath\b"));

        Assert.True(
            saving is not null,
            "the architect's finalization text must say it saves <plan>.tasks.json and returns its path, " +
            "so the conductor can validate the file before dispatching from it. Read: " +
            string.Join(" / ", surfaces));
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
        Assert.Contains("Run nothing else", commands, StringComparison.Ordinal);
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
    // How the review runs: who owns its command, one gate at a time, alone and last, and
    // what it does when it cannot start its seats.
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
    /// The reviewer re-checks one gate at a time through <c>--gate</c>, instead of running the whole declared suite
    /// again to confirm a single fix.
    /// </summary>
    /// <remarks>
    /// Re-running every declared gate pays the whole suite's cost for one answer, which is the cost this slice exists
    /// to remove. The reviewer's own surfaces must name the option and say what it is for, or the next editor
    /// re-derives the expensive shape.
    /// </remarks>
    [Fact]
    public void TheReviewerRechecksASingleGateThroughTheGateOption()
    {
        string[] mentions = ReviewerSurfacePaths
            .SelectMany(path => Paragraphs(ReadProductFile(path)).Select(block => $"{path}: {block}"))
            .Where(block => GateOptionMention.IsMatch(block))
            .ToArray();

        Assert.True(
            mentions.Length > 0,
            "The reviewer's instructions must name 'review gates --gate <id>' for a single-gate re-check. " +
            $"Read: {string.Join(", ", ReviewerSurfacePaths)}.");

        Assert.True(
            mentions.Any(RecheckWording.IsMatch),
            "A paragraph naming --gate must say it re-checks one gate. Found:" +
            Environment.NewLine + string.Join($"{Environment.NewLine}---{Environment.NewLine}", mentions));
    }

    /// <summary>
    /// The final council runs alone and last, in the foreground: no docs work and no edits happen in the reviewed tree
    /// while it runs.
    /// </summary>
    /// <remarks>
    /// A review that shares the tree with concurrent docs work or edits is reviewing something that keeps moving, and
    /// a finding it raises may be against a line already changed. The reviewed tree is also the tree those other jobs
    /// write to, so a review that runs in the foreground is what makes its evidence current.
    /// </remarks>
    [Fact]
    public void TheFinalCouncilRunsAloneLastInTheForegroundWithNoDocsWorkOrEdits()
    {
        string contract = ReadProductFile(DeliveryExecutionSkillRelativePath);
        string[] blocks = Paragraphs(contract)
            .Where(block => block.Contains("foreground", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.True(
            blocks.Length > 0,
            $"'{DeliveryExecutionSkillRelativePath}' must state that the review runs last, in the foreground.");

        Assert.True(
            blocks.Any(block => AloneWording.IsMatch(block)
                                && block.Contains("docs", StringComparison.OrdinalIgnoreCase)
                                && block.Contains("edit", StringComparison.OrdinalIgnoreCase)),
            "The paragraph that says the review runs in the foreground must also say that no docs work and no edits " +
            "happen in the reviewed tree while it runs. Found:" +
            Environment.NewLine + string.Join($"{Environment.NewLine}---{Environment.NewLine}", blocks));
    }

    /// <summary>
    /// A reviewer that cannot start its seats stops after its gates with <c>STATUS: COUNCIL_SEATS_NEEDED</c>, listing
    /// the lenses and the scope, and the waiting session runs the seats and re-invokes it.
    /// </summary>
    /// <remarks>
    /// Harnesses differ in whether a subagent may start another one. Where it may not, silently applying every lens
    /// in-process hides that the run had less independent judgement than a verdict implies; dropping the council
    /// hides it entirely. Returning the routing facts lets the session that can start seats do so, and the reviewer
    /// then judges what they returned.
    /// </remarks>
    [Fact]
    public void ACouncilThatCannotStartItsSeatsStopsAfterTheGatesWithCouncilSeatsNeeded()
    {
        string reviewer = ReadProductFile("agents/code-reviewer.md");
        string[] blocks = Paragraphs(reviewer)
            .Where(block => block.Contains("STATUS: COUNCIL_SEATS_NEEDED", StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            blocks.Length > 0,
            "'agents/code-reviewer.md' must route a council that cannot start its seats with " +
            "'STATUS: COUNCIL_SEATS_NEEDED'.");

        Assert.True(
            blocks.Any(block => block.Contains("lens", StringComparison.OrdinalIgnoreCase)
                                && block.Contains("scope", StringComparison.OrdinalIgnoreCase)
                                && block.Contains("gate", StringComparison.OrdinalIgnoreCase)
                                && ReinvokeWording.IsMatch(block)),
            "The COUNCIL_SEATS_NEEDED paragraph must list the lenses and the scope, say it stops after its gates, and " +
            "say the session that can start the seats re-invokes the reviewer. Found:" +
            Environment.NewLine + string.Join($"{Environment.NewLine}---{Environment.NewLine}", blocks));
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
        Assert.Contains("worktree", text, StringComparison.OrdinalIgnoreCase);
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
    /// The session-title bullet, including continuation lines, or null when the document carries
    /// none. Continuation lines belong to the bullet: the requirement is read from the instruction
    /// it governs, not from a neighbouring bullet.
    /// </summary>
    private static string? SessionTitleBullet(string guidance)
    {
        return ListItemStartingWith(guidance, SessionTitleBulletLead);
    }

    /// <summary>
    /// True when the session-title bullet tells the agent to set the title and does not tell the
    /// agent to skip it. Scoped to that bullet so a requirement in some other bullet cannot satisfy
    /// a session-title instruction that omits it or hedges it.
    /// </summary>
    private static bool RequiresSessionTitle(string guidance)
    {
        string? bullet = SessionTitleBullet(guidance);
        return bullet is not null
               && bullet.Contains(SessionTitleSetInstruction, StringComparison.Ordinal)
               && !ContainsSessionTitleHedge(bullet);
    }

    /// <summary>
    /// The pull-request title list item, including its continuation lines, or null when the
    /// document has none.
    /// </summary>
    private static string? PrTitleBullet(string guidance)
    {
        return ListItemStartingWith(guidance, "- **PR Title.**");
    }

    /// <summary>
    /// True when the pull-request bullet sets the session title from the submitted pull request,
    /// shows the <c>#&lt;number&gt; &lt;title&gt;</c> example, and the sentence that tracks a
    /// retitle does not tell the agent to skip the title.
    /// </summary>
    private static bool PrTitleBulletRequiresTheSessionTitle(string guidance)
    {
        string? bullet = PrTitleBullet(guidance);
        if (bullet is null
            || !bullet.Contains(PrTitleSetInstruction, StringComparison.Ordinal)
            || !bullet.Contains(PrTitleExample, StringComparison.Ordinal)
            || ContainsSessionTitleHedge(bullet))
        {
            return false;
        }

        foreach (string sentence in bullet.Split('.',
                     StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!sentence.Contains(PrTitleRetitleInstruction, StringComparison.Ordinal))
            {
                continue;
            }

            return !ContainsSessionTitleHedge(sentence);
        }

        return false;
    }

    /// <summary>
    /// Fails when a session-title bullet tells an agent not to set a session title. Other bullets
    /// are out of scope: the same words there are not this instruction.
    /// </summary>
    private static void AssertShippedDefaultOmitsSessionTitleHedges(string guidance)
    {
        Assert.True(
            SessionTitleBulletsOmitHedges(guidance, out string? offending),
            $"The session-title guidance must not tell an agent '{offending}'.");
    }

    /// <summary>
    /// True when neither the session-title bullet nor the pull-request bullet carries a general
    /// skip. A missing bullet is not a hedge; the presence pins cover that.
    /// </summary>
    private static bool SessionTitleBulletsOmitHedges(string guidance, out string? offending)
    {
        offending = null;
        foreach (string? bullet in new[] { SessionTitleBullet(guidance), PrTitleBullet(guidance) })
        {
            if (bullet is null)
            {
                continue;
            }

            foreach (string hedge in SessionTitleHedges)
            {
                if (bullet.Contains(hedge, StringComparison.Ordinal))
                {
                    offending = hedge;
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// The shipped default's set, submit, and retitle sentences each name Factory and how it
    /// derives the title. A later sentence does not stand in for that.
    /// </summary>
    private static void AssertShippedDefaultStatesTheFactorySessionTitleLimit(string guidance)
    {
        AssertSentenceCarriesTheFactoryLimit(
            SessionTitleBullet(guidance) ?? string.Empty,
            SessionTitleSetInstruction);
        AssertSentenceCarriesTheFactoryLimit(
            PrTitleBullet(guidance) ?? string.Empty,
            PrTitleSetInstruction);
        AssertSentenceCarriesTheFactoryLimit(
            PrTitleBullet(guidance) ?? string.Empty,
            PrTitleRetitleInstruction);
    }

    /// <summary>
    /// The sentence containing <paramref name="instruction"/> also states the Factory limit.
    /// </summary>
    private static void AssertSentenceCarriesTheFactoryLimit(string text, string instruction)
    {
        string sentence = SentenceContaining(text, instruction) ?? string.Empty;
        Assert.Contains(instruction, sentence, StringComparison.Ordinal);
        Assert.Contains(FactorySessionTitleException, sentence, StringComparison.Ordinal);
        Assert.Contains(FactorySessionTitleMechanism, sentence, StringComparison.Ordinal);
    }

    /// <summary>The sentence in <paramref name="text"/> that contains <paramref name="instruction"/>.</summary>
    private static string? SentenceContaining(string text, string instruction)
    {
        foreach (string sentence in text.Split('.',
                     StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (sentence.Contains(instruction, StringComparison.Ordinal))
            {
                return sentence;
            }
        }

        return null;
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

    private static bool ContainsSessionTitleHedge(string text)
    {
        foreach (string hedge in SessionTitleHedges)
        {
            if (text.Contains(hedge, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
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
    /// The line of <paramref name="text"/> that states <paramref name="sentence"/> in full, or null
    /// when no single line does.
    /// </summary>
    /// <remarks>
    /// One line is the assertion. A sentence wrapped across two lines is not the instruction, and a
    /// check that tolerated the wrap would also tolerate a sentence cut in half by an edit. The
    /// comparison is ordinal, so a lowercased or reworded sentence fails it too.
    /// </remarks>
    private static string? SentenceLine(string text, string sentence)
    {
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r').Trim();
            string plain = WithoutEmphasis(line);
            plain = Regex.Replace(plain, @"^\s*(?:[-*+]|\d+\.)\s+", string.Empty, RegexOptions.CultureInvariant);
            if (plain.Equals(sentence, StringComparison.Ordinal))
            {
                return line;
            }
        }

        return null;
    }

    /// <summary>
    /// The block of <paramref name="text"/> that states <paramref name="anchor"/>, or an empty string
    /// when no block states it.
    /// </summary>
    /// <remarks>
    /// Blocks are the paragraphs and list items a Markdown reader sees: runs of consecutive
    /// non-blank lines. Scoping a pin to the block that owns the rule is what keeps a sentence that
    /// merely repeats the rule elsewhere in the file from satisfying it.
    /// </remarks>
    private static string BlockContaining(string text, string anchor)
    {
        string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        foreach (string block in normalized.Split("\n\n", StringSplitOptions.None))
        {
            if (block.Replace("\n", " ", StringComparison.Ordinal).Contains(anchor, StringComparison.Ordinal))
            {
                return block;
            }
        }

        return string.Empty;
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
