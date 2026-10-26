using System.Text;
using System.Text.RegularExpressions;
using KyberWeave.Core.Agents.Model;
using KyberWeave.Core.Skills.Model;
using KyberWeave.Core.Skills.Review;
using KyberWeave.Core.Skills.Validation;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using KyberWeave.Core.Squad.Rendering;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the docs-dev harvest closeout contract (Ready plan T4 RED): every completed
/// change gets a <c>docs-dev</c> documentation-impact review plus plan/spec closeout,
/// scale and Design harvest are declared at authoring time, and no portable file carries
/// a literal <c>docs/</c> path. Every fact here fails on the unedited tree until T5-T7
/// lands, except the two description routing guards, which pass today and pin the floor
/// the rewritten description must not fall through.
/// </summary>
public sealed class SquadDocsHarvestContractTests
{
    private static readonly Regex LiteralDocsPathRegex =
        new(@"(?<![\w<.-])docs/", RegexOptions.Compiled);

    private static string ToolRoot => KyberWeaveTestPaths.ToolRoot;

    private static string ProductRoot =>
        Path.Combine(ToolRoot, "products", "kyber-squad");

    private static string ReadProduct(params string[] relative)
    {
        return File.ReadAllText(Path.Combine([ProductRoot, .. relative]));
    }

    private static string ReadRepo(params string[] relative)
    {
        return File.ReadAllText(Path.Combine([ToolRoot, .. relative]));
    }

    private static string DocsDevDescription()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadAgent agent = Assert.Single(source.Agents, candidate => candidate.Name == "docs-dev");
        return agent.Description;
    }

    private static DescriptionScore ScoreDescription(string description)
    {
        return DescriptionScorer.Score(new Skill
        {
            SkillFilePath = "/tmp/docs-dev/SKILL.md",
            DirectoryPath = "/tmp/docs-dev",
            Frontmatter = new SkillFrontmatter
            {
                Name = "docs-dev",
                Description = description
            },
            RawFrontmatter = $"name: docs-dev\ndescription: {description}",
            InstructionsBody = "# Instructions\nWrite documentation."
        });
    }

    private static void AssertAll(string text, params string[] phrases)
    {
        foreach (string phrase in phrases)
        {
            Assert.Contains(phrase, text, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The docs-dev description routes orchestration and names the authoring contract:
    /// trigger and boundary phrasing plus design, decision, documentation-impact review,
    /// README, ADR, runbook, product-owner, and closeout vocabulary, with closeout stated
    /// before the README audience it serves.
    /// </summary>
    [Fact]
    public void DocsDevDescriptionNamesRoutingTriggersAndAuthoringContract()
    {
        string description = DocsDevDescription();

        AssertAll(
            description,
            "Use when",
            "Do not use when",
            "design",
            "decision",
            "documentation-impact review",
            "README",
            "ADR",
            "runbook",
            "product-owner",
            "closeout");

        int closeoutIndex = description.IndexOf("closeout", StringComparison.OrdinalIgnoreCase);
        int readmeIndex = description.IndexOf("README", StringComparison.Ordinal);
        Assert.True(
            closeoutIndex >= 0 && closeoutIndex < readmeIndex,
            $"Expected 'closeout' before 'README' in: {description}");
    }

    /// <summary>
    /// Regression guard: the docs-dev description already scores as healthy routing
    /// metadata, and the T5-T7 rewrite must keep it at or above the routing threshold.
    /// This fact passes on the unedited tree by design.
    /// </summary>
    [Fact]
    public void DocsDevDescriptionRoutingScoreStaysHealthy()
    {
        DescriptionScore score = ScoreDescription(DocsDevDescription());

        Assert.True(
            score.Total >= 70,
            $"Expected description routing score >= 70, was {score.Total}: " +
            string.Join("; ", score.Components.Select(c => $"{c.Name} {c.Points}/{c.MaxPoints}")));
    }

    /// <summary>
    /// Regression guard: the docs-dev description already carries trigger phrasing and a
    /// healthy routing score, so neither agent lint may fire against it. The T5-T7 rewrite
    /// must preserve both. This fact passes on the unedited tree by design.
    /// </summary>
    [Fact]
    public void DocsDevDescriptionCarriesNoRoutingLintFindings()
    {
        string description = DocsDevDescription();

        Assert.True(
            ScoreDescription(description).Total >= 50,
            "A score below 50 would raise KW-AGENT-LINT-001.");

        AgentModel agent = new()
        {
            RoleName = "docs-dev",
            Harness = HarnessKind.Claude,
            FilePath = "/tmp/Claude/agents/docs-dev.md",
            DirectoryPath = "/tmp/Claude/agents",
            Description = description,
            InstructionsBody = "Documentation instructions."
        };
        SkillReviewExportResult result =
            SkillReviewExchange.ExportCandidates(agents: new AgentSet([agent]));

        SkillReviewCandidate candidate = Assert.Single(result.Bundle.Candidates);
        Assert.DoesNotContain("KW-AGENT-LINT-002", candidate.HeuristicFlags);
    }

    /// <summary>
    /// The docs-dev scope leads with harvested stewardship: its first owned items name
    /// closeout and the Design harvest that feeds canonical documentation.
    /// </summary>
    [Fact]
    public void DocsDevScopeLeadsWithCloseoutAndDesignHarvest()
    {
        string text = ReadProduct("agents", "docs-dev.md");
        int scopeStart = text.IndexOf("## Scope", StringComparison.Ordinal);
        Assert.True(scopeStart >= 0, "docs-dev.md must keep its ## Scope section.");
        string scope = text[scopeStart..];

        AssertAll(scope, "closeout", "Design harvest");
    }

    /// <summary>
    /// The documentation-impact review section exists and states its full contract: the
    /// DOCS_REVIEW rows and RECOMMENDATIONS it returns, that it reviews what it is sent,
    /// the run record it keeps, routing by component and doc type beside the component's
    /// existing docs, and the high-confidence bar where a human approves, vocabulary is
    /// never added, the best existing home wins, and a todo is filed only if the owner
    /// accepts it.
    /// </summary>
    [Fact]
    public void DocsDevDocumentationImpactReviewSectionStatesItsContract()
    {
        string text = ReadProduct("agents", "docs-dev.md");

        AssertAll(
            text,
            "documentation-impact review",
            "DOCS_REVIEW",
            "RECOMMENDATIONS",
            "reviews what it is sent",
            "run record",
            "component",
            "doc type",
            "existing docs",
            "high confidence",
            "human approves",
            "never adds",
            "vocabulary",
            "best existing home",
            "todo only if");
    }

    /// <summary>
    /// Closeout work ends in machine-readable digests, not prose: a CLOSEOUT_COMPLETE
    /// success row and a BLOCKED row naming what stopped the archive.
    /// </summary>
    [Fact]
    public void DocsDevCloseoutEndsInMachineReadableDigests()
    {
        string text = ReadProduct("agents", "docs-dev.md");

        AssertAll(text, "CLOSEOUT_COMPLETE", "BLOCKED");
    }

    /// <summary>
    /// Plan closeout keeps its mapping table and both waiver categories, and gains the
    /// Design harvest: a Closeout mapping with documentation-impact, impact-components,
    /// last-reviewed, and an archived status row.
    /// </summary>
    [Fact]
    public void PlanCloseoutKeepsWaiversAndAddsDesignHarvest()
    {
        string text = ReadProduct("agents", "docs-dev.md");

        AssertAll(
            text,
            "Plan closeout",
            "mapping table",
            "ADR waiver",
            "Documentation waiver",
            "do not archive",
            "Design harvest",
            "Closeout mapping",
            "documentation-impact",
            "impact-components",
            "last-reviewed",
            "status archived",
            "Status: Archived");
    }

    /// <summary>
    /// Specification closeout migrates durable content before archiving, and names the
    /// Design harvest, documentation-impact, and archived status alongside it.
    /// </summary>
    [Fact]
    public void SpecCloseoutMigratesAndArchivesWithDesignHarvest()
    {
        string text = ReadProduct("agents", "docs-dev.md");

        AssertAll(
            text,
            "Specification closeout",
            "Design harvest",
            "documentation-impact",
            "Status: Archived");
    }

    /// <summary>
    /// The conductor no longer limits docs-dev to plans and specifications: every
    /// completed change gets a docs-dev review, and closeout completes the objective.
    /// </summary>
    [Fact]
    public void ConductorRequiresDocsDevReviewAndCloseoutForEveryCompletedChange()
    {
        string text = ReadProduct("agents", "conductor.md");

        AssertAll(text, "every completed change", "docs-dev review", "closeout");
    }

    /// <summary>
    /// The execution contract records the closeout run: a run record, the DOCS_REVIEW
    /// rows, the Q2 CLOSEOUT_COMPLETE marker, and the PR description that carries them.
    /// </summary>
    [Fact]
    public void ExecutionAndReviewCloseoutRecordsRunRecordAndDocsReview()
    {
        string text = ReadProduct("agents", "conductor", "references", "execution-and-review.md");

        AssertAll(text, "run record", "DOCS_REVIEW", "CLOSEOUT_COMPLETE", "PR description");
    }

    /// <summary>
    /// Autonomous delivery is gated on the documentation contract: it requires
    /// DOCS_REVIEW and CLOSEOUT_COMPLETE evidence, and the draft PR carries both.
    /// </summary>
    [Fact]
    public void AutonomyDeliveryRequiresDocsReviewAndCloseoutComplete()
    {
        string text = ReadProduct("agents", "conductor", "references", "autonomy.md");

        AssertAll(text, "DOCS_REVIEW", "CLOSEOUT_COMPLETE", "PR carries");
    }

    /// <summary>
    /// The one-defect path is not exempt from the documentation contract: bug-crusher
    /// keeps a run record, routes the fix through a docs-dev review, and reports the
    /// outcome in the report and the pull request.
    /// </summary>
    [Fact]
    public void BugCrusherRecordsRunRecordAndDocsDevReview()
    {
        string text = ReadProduct("skills", "bug-crusher", "SKILL.md");

        AssertAll(text, "run record", "docs-dev review", "outcome");
    }

    /// <summary>
    /// Bug-crusher can reach the documentation specialist: docs-dev joins its
    /// delegation roster instead of investigation owning the only edge.
    /// </summary>
    [Fact]
    public void BugCrusherDelegatesToDocsDev()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadAgent agent = Assert.Single(source.Agents, candidate => candidate.Name == "bug-crusher");

        Assert.Contains("docs-dev", agent.DelegatesTo);
    }

    /// <summary>
    /// The Copilot projection matches the widened roster: bug-crusher renders both the
    /// investigator and docs-dev as reachable agents.
    /// </summary>
    [Fact]
    public async Task CopilotRenderForBugCrusherListsDocsDevAlongsideInvestigator()
    {
        string frontmatter = await RenderFrontmatterAsync("bug-crusher");

        string? actual = frontmatter
            .Split('\n')
            .FirstOrDefault(line => line.StartsWith("agents:", StringComparison.Ordinal))
            ?.Trim();

        Assert.Equal("agents: ['bug-crusher-investigator', 'docs-dev']", actual);
    }

    /// <summary>
    /// The pull-request description contract states its Documentation impact verbatim,
    /// including the heading fallback when the host supplies no template, and requires
    /// closeout before delivery rather than after merge.
    /// </summary>
    [Fact]
    public void CreatePullRequestDocumentationImpactRequiresCloseoutBeforeDelivery()
    {
        string text = ReadProduct("skills", "create-pull-request", "SKILL.md");

        AssertAll(text, "Documentation impact", "heading fallback", "closeout before delivery");
    }

    /// <summary>
    /// Plan authoring declares scale and the Design harvest up front: governed
    /// frontmatter values, impact-components, a Design harvest table keyed by target
    /// component and target doc type, and the owner-confirms-scale gate at high
    /// confidence.
    /// </summary>
    [Fact]
    public void PlanAuthoringDeclaresScaleAndDesignHarvest()
    {
        string text = ReadProduct("agents", "architect", "references", "plan-authoring.md");

        AssertAll(
            text,
            "development-mode",
            "impact-components",
            "Design harvest",
            "Target component",
            "Target doc type",
            "owner confirms scale",
            "high confidence");
    }

    /// <summary>
    /// The design phase keeps its five-column Decisions table intact and adds scale
    /// plus the Design harvest requirement-group it must satisfy.
    /// </summary>
    [Fact]
    public void DesignPhaseKeepsDecisionsTableAndNamesDesignHarvest()
    {
        string text = ReadProduct("skills", "product-owner", "references", "design-phase.md");

        AssertAll(
            text,
            "| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |",
            "Design harvest",
            "requirement-group",
            "scale");
    }

    /// <summary>
    /// A plan is Ready only when scale and the Design harvest are verified alongside
    /// the mode contract and documentation checks.
    /// </summary>
    [Fact]
    public void PlanReadyVerifiesScaleAndDesignHarvest()
    {
        string text = ReadProduct("agents", "architect", "references", "plan-authoring.md");

        AssertAll(text, "PLAN_READY", "scale", "Design harvest");
    }

    /// <summary>
    /// A specification is Ready only when scale and the Design harvest are verified,
    /// and approve-and-execute restates the scale and components under approval.
    /// </summary>
    [Fact]
    public void SpecReadyVerifiesScaleAndDesignHarvest()
    {
        string text = ReadProduct("agents", "conductor", "references", "spec-path.md");

        AssertAll(text, "SPEC_READY", "scale", "Design harvest", "components");
    }

    /// <summary>
    /// The tasks-phase closeout names the Design harvest and the scale it was
    /// declared at, so the final task carries the authoring-time decisions.
    /// </summary>
    [Fact]
    public void TasksPhaseCloseoutNamesDesignHarvestAndScale()
    {
        string text = ReadProduct("skills", "product-owner", "references", "tasks-phase.md");

        AssertAll(text, "Design harvest", "scale");
    }

    /// <summary>
    /// The specification closeout digest reports the documentation migration in
    /// machine-readable rows: what mapped where, the impact, and the review verdict.
    /// </summary>
    [Fact]
    public void CloseoutPhaseDigestGainsMappedImpactAndDocsReview()
    {
        string text = ReadProduct("skills", "product-owner", "references", "closeout-phase.md");

        AssertAll(text, "MAPPED:", "IMPACT:", "DOCS_REVIEW:");
    }

    /// <summary>
    /// The intent-alignment lens pins the archived-specification contract: the D22
    /// documentation items, and a critical finding for a missing or false PR
    /// documentation section or an unjustified no-doc-change claim.
    /// </summary>
    [Fact]
    public void IntentAlignmentPinsArchivedSpecDocsReviewContract()
    {
        string text = ReadProduct("skills", "code-review", "references", "lenses", "intent-alignment.md");

        AssertAll(
            text,
            "critical",
            "archived specification",
            "D22",
            "no doc change",
            "PR description");
    }

    /// <summary>
    /// The plan inventory policy section — not just the active-plan row — requires the
    /// Design harvest and documentation-impact mapping at authoring time.
    /// </summary>
    [Fact]
    public void PlanIndexPolicySectionNamesDesignHarvest()
    {
        string text = ReadRepo("docs", "plans", "README.md");
        int policyStart = text.IndexOf("### Plan Decisions and Closeout Policy", StringComparison.Ordinal);
        Assert.True(policyStart >= 0, "plans README must keep its closeout policy section.");
        int activeStart = text.IndexOf("## Active Plans", policyStart, StringComparison.Ordinal);
        string policy = activeStart < 0 ? text[policyStart..] : text[policyStart..activeStart];

        AssertAll(policy, "Design harvest", "documentation-impact");
    }

    /// <summary>
    /// The specification index carries the same authoring-time policy: scale declared,
    /// Design harvest named, documentation-impact mapped before archival.
    /// </summary>
    [Fact]
    public void SpecIndexPolicySectionNamesDesignHarvest()
    {
        string text = ReadRepo("docs", "specs", "README.md");

        AssertAll(text, "Design harvest");
    }

    /// <summary>
    /// The host pull-request template carries a Documentation impact section, so the
    /// create-pull-request skill has a host heading to fill rather than inventing one.
    /// </summary>
    [Fact]
    public void PullRequestTemplateCarriesDocumentationImpact()
    {
        string text = ReadRepo(".github", "PULL_REQUEST_TEMPLATE.md");

        Assert.Contains("Documentation impact", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Portable contract files never embed a literal docs/ path: every documentation
    /// location resolves through Config Reg properties such as &lt;docs-root&gt;, so a
    /// host that mounts documentation elsewhere keeps working. docs-dev.md still carries
    /// literal paths today, so this fails until T5-T7 rewrites them.
    /// </summary>
    [Fact]
    public void NoLiteralDocsPathsInPortableHarvestFiles()
    {
        string[] portableFiles =
        [
            Path.Combine("agents", "docs-dev.md"),
            Path.Combine("agents", "conductor.md"),
            Path.Combine("agents", "conductor", "references", "execution-and-review.md"),
            Path.Combine("agents", "conductor", "references", "autonomy.md"),
            Path.Combine("agents", "conductor", "references", "plan-path.md"),
            Path.Combine("agents", "conductor", "references", "spec-path.md"),
            Path.Combine("agents", "bug-crusher.md"),
            Path.Combine("agents", "architect", "references", "plan-authoring.md"),
            Path.Combine("skills", "bug-crusher", "SKILL.md"),
            Path.Combine("skills", "create-pull-request", "SKILL.md"),
            Path.Combine("skills", "product-owner", "references", "design-phase.md"),
            Path.Combine("skills", "product-owner", "references", "tasks-phase.md"),
            Path.Combine("skills", "product-owner", "references", "closeout-phase.md"),
            Path.Combine("skills", "code-review", "references", "lenses", "intent-alignment.md")
        ];

        foreach (string relative in portableFiles)
        {
            string text = File.ReadAllText(Path.Combine(ProductRoot, relative));
            Assert.False(
                LiteralDocsPathRegex.IsMatch(text),
                $"Portable file '{relative}' carries a literal docs/ path; resolve it through Config Reg.");
        }
    }

    private static async Task<string> RenderFrontmatterAsync(string agentName)
    {
        SquadRendererRegistry registry = new([new CopilotRenderer()]);
        SquadRenderRequest request = new(
            ProductRoot,
            [SquadTarget.Copilot],
            SquadDeploymentScope.Project);

        SquadRenderResult result = await registry.RenderAsync(request);
        Assert.True(result.Success, string.Join("; ", result.Errors));

        SquadDeploymentFile file = Assert.Single(
            result.Files,
            candidate => candidate.RelativePath == $".github/agents/{agentName}.agent.md");

        return ExtractFrontmatterText(Encoding.UTF8.GetString(file.Content.Span));
    }

    private static string ExtractFrontmatterText(string markdown)
    {
        const string Delimiter = "---\n";
        Assert.StartsWith(Delimiter, markdown, StringComparison.Ordinal);
        int end = markdown.IndexOf("\n---\n", Delimiter.Length, StringComparison.Ordinal);
        Assert.True(end > 0, "Expected closing frontmatter delimiter.");
        return markdown[Delimiter.Length..end];
    }
}
