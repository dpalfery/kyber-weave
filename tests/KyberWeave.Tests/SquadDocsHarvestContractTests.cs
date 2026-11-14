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
/// Checks the docs-dev harvest closeout contract's code-computed and structural parts: the
/// docs-dev description's routing vocabulary, score and lint, the digest and frontmatter tokens,
/// the bug-crusher roster and render, the Decisions table, docs-dev's shell boundary and
/// capability profile, and that no portable file carries a literal <c>docs/</c> path. The prose
/// rules (documentation-impact review, plan and spec closeout, authoring-time scale and Design
/// harvest) are graded from the prose rule catalog (<c>Fixtures/prose-rules/catalog.yml</c>).
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
    /// The docs-dev description carries the trigger and negative-boundary clauses the description
    /// scorer looks for.
    /// </summary>
    [Fact]
    public void DocsDevDescriptionNamesRoutingTriggers()
    {
        AssertAll(DocsDevDescription(), "Use when", "Do not use when");
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
    /// The docs-dev scope names the Design harvest section the archive closeout validator reads.
    /// </summary>
    [Fact]
    public void DocsDevScopeNamesTheDesignHarvestSection()
    {
        string text = ReadProduct("agents", "docs-dev.md");
        int scopeStart = text.IndexOf("## Scope", StringComparison.Ordinal);
        Assert.True(scopeStart >= 0, "docs-dev.md must keep its ## Scope section.");
        string scope = text[scopeStart..];

        Assert.Contains("Design harvest", scope, StringComparison.Ordinal);
    }

    /// <summary>
    /// The documentation-impact review returns DOCS_REVIEW rows and RECOMMENDATIONS.
    /// </summary>
    [Fact]
    public void DocsDevNamesTheDocumentationImpactReviewDigestRows()
    {
        AssertAll(ReadProduct("agents", "docs-dev.md"), "DOCS_REVIEW", "RECOMMENDATIONS");
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
    /// Plan closeout names the Design harvest frontmatter keys (documentation-impact,
    /// impact-components, last-reviewed) and the archived status row.
    /// </summary>
    [Fact]
    public void PlanCloseoutNamesTheHarvestKeysAndArchivedStatus()
    {
        AssertAll(
            ReadProduct("agents", "docs-dev.md"),
            "documentation-impact",
            "impact-components",
            "last-reviewed",
            "Status: Archived");
    }

    /// <summary>
    /// Specification closeout names documentation-impact and the archived status row.
    /// </summary>
    [Fact]
    public void SpecCloseoutNamesDocumentationImpactAndArchivedStatus()
    {
        AssertAll(ReadProduct("agents", "docs-dev.md"), "documentation-impact", "Status: Archived");
    }

    /// <summary>
    /// The shared execution skill both entry points load names the DOCS_REVIEW rows and the
    /// Q2 CLOSEOUT_COMPLETE marker.
    /// </summary>
    [Fact]
    public void ExecutionContractNamesDocsReviewAndCloseoutComplete()
    {
        AssertAll(ReadProduct("skills", "delivery-execution", "SKILL.md"), "DOCS_REVIEW", "CLOSEOUT_COMPLETE");
    }

    /// <summary>
    /// Autonomous delivery names the DOCS_REVIEW and CLOSEOUT_COMPLETE evidence it requires.
    /// </summary>
    [Fact]
    public void AutonomyNamesDocsReviewAndCloseoutCompleteEvidence()
    {
        AssertAll(ReadProduct("agents", "conductor", "references", "autonomy.md"), "DOCS_REVIEW", "CLOSEOUT_COMPLETE");
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

        Assert.Equal(
            "agents: ['architect', 'bug-crusher-investigator', 'code-reviewer', 'csharp-dev', 'dal-dev', " +
            "'docs-dev', 'github-devops', 'go-dev', 'maui-dev', 'pulumi-dev', 'python-dev', 'react-dev', " +
            "'review-lens', 'review-triage', 'rust-dev', 'sql-database-architect', 'tauri-dev', 'test-dev', " +
            "'typescript-dev']",
            actual);
    }

    /// <summary>
    /// Plan authoring names the scale frontmatter keys.
    /// </summary>
    [Fact]
    public void PlanAuthoringNamesTheScaleKeys()
    {
        AssertAll(
            ReadProduct("agents", "architect", "references", "plan-authoring.md"),
            "development-mode",
            "impact-components");
    }

    /// <summary>
    /// The design phase keeps its five-column Decisions table intact.
    /// </summary>
    [Fact]
    public void DesignPhaseKeepsTheDecisionsTable()
    {
        Assert.Contains(
            "| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |",
            ReadProduct("skills", "product-owner", "references", "design-phase.md"),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Plan authoring names the PLAN_READY marker.
    /// </summary>
    [Fact]
    public void PlanAuthoringNamesPlanReady()
    {
        Assert.Contains("PLAN_READY", ReadProduct("agents", "architect", "references", "plan-authoring.md"), StringComparison.Ordinal);
    }

    /// <summary>
    /// The spec path names the SPEC_READY marker.
    /// </summary>
    [Fact]
    public void SpecPathNamesSpecReady()
    {
        Assert.Contains("SPEC_READY", ReadProduct("agents", "conductor", "references", "spec-path.md"), StringComparison.Ordinal);
    }

    /// <summary>
    /// The tasks-phase closeout names the Design harvest section the archive closeout validator reads.
    /// </summary>
    [Fact]
    public void TasksPhaseCloseoutNamesDesignHarvest()
    {
        string text = ReadProduct("skills", "product-owner", "references", "tasks-phase.md");

        Assert.Contains("Design harvest", text, StringComparison.Ordinal);
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
    /// The intent-alignment lens names the D22 documentation items of an archived specification.
    /// </summary>
    [Fact]
    public void IntentAlignmentLensNamesTheD22DocumentationItems()
    {
        Assert.Contains(
            "D22",
            ReadProduct("skills", "code-review", "references", "lenses", "intent-alignment.md"),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The plan inventory policy section — not just the active-plan row — names the
    /// documentation-impact mapping.
    /// </summary>
    [Fact]
    public void PlanIndexPolicySectionNamesDocumentationImpact()
    {
        string text = ReadRepo("docs", "plans", "README.md");
        int policyStart = text.IndexOf("### Plan Decisions and Closeout Policy", StringComparison.Ordinal);
        Assert.True(policyStart >= 0, "plans README must keep its closeout policy section.");
        int activeStart = text.IndexOf("## Active Plans", policyStart, StringComparison.Ordinal);
        string policy = activeStart < 0 ? text[policyStart..] : text[policyStart..activeStart];

        Assert.Contains("documentation-impact", policy, StringComparison.Ordinal);
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
            Path.Combine("skills", "delivery-execution", "SKILL.md"),
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

    /// <summary>
    /// docs-dev holds a shell only for the nine command classes its prompt names: the plan
    /// closeout command, a specification's archival <c>git mv</c>, its task file's <c>git rm</c>, the two documentation checks, rewriting a task file it made stale,
    /// targeted doc-pinning tests, and committing its own documentation edits, so a later edit cannot widen the instruction-scoped boundary
    /// silently. The never-push and only-archiver rules are graded from the prose rule
    /// catalog (<c>docs-dev-shell-commands</c>, <c>docs-dev-only-archiver</c>).
    /// </summary>
    [Fact]
    public void DocsDevBodyNamesItsOnlyShellCommands()
    {
        string text = ReadProduct("agents", "docs-dev.md");

        AssertAll(
            text,
            "## Commands you run",
            "git mv",
            "docs validate . --merge-ready",
            "docs drift .");

        // Negative check: the section must name exactly the allowed command classes and no others.
        string section = ExtractSection(text, "## Commands you run");

        string[] bullets = section
            .Split('\n')
            .Where(line => line.StartsWith("- ", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(9, bullets.Length);

        // Every backticked span with a space is a command. "dotnet run" is the invocation-form
        // selector the validate and drift bullets name ("or the same `dotnet run` form"), not a
        // separate command, so it is part of the allowed set.
        // Pair every backtick first, then keep the spans that contain a space. A path span such as
        // `<docs-root>/archive/plans/` has no space, so it cannot shift the pairing of the spans after it.
        string[] namedCommands = Regex.Matches(section, "`([^`]*)`")
            .Select(match => match.Groups[1].Value)
            .Where(span => span.Contains(' ', StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        string[] allowedCommands =
        [
            "dotnet run",
            "dotnet run --project <the CLI project> -- docs validate .",
            "git add <your own documentation paths>",
            "git commit",
            "git mv",
            "git rm",
            "kyber-weave docs closeout <plan>",
            "kyber-weave docs closeout <plan> --archive",
            "kyber-weave docs drift .",
            "kyber-weave docs packet <task id>",
            "kyber-weave docs tasks <plan> --write",
            "kyber-weave docs validate .",
        ];

        Assert.Equal(
            allowedCommands.Order(StringComparer.Ordinal).ToArray(),
            namedCommands);
    }

    private static string ExtractSection(string markdown, string heading)
    {
        string normalized = markdown.Replace("\r\n", "\n", StringComparison.Ordinal);
        int start = normalized.IndexOf(heading, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing section '{heading}'.");

        int bodyStart = start + heading.Length;
        int next = normalized.IndexOf("\n## ", bodyStart, StringComparison.Ordinal);
        return next < 0 ? normalized[bodyStart..] : normalized[bodyStart..next];
    }

    /// <summary>
    /// The <c>documentation</c> capability profile has exactly one consumer, docs-dev, and
    /// grants <c>process.execute</c>. Its grant is instruction-scoped (the lattice has no
    /// command scoping), so a second consumer must be a visible, reviewed change that
    /// updates this guard rather than a silent inheritance of the shell grant.
    /// </summary>
    [Fact]
    public void DocumentationProfileIsConsumedOnlyByDocsDevAndGrantsExecute()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        string[] expectedConsumers = ["docs-dev"];
        string[] consumers = source.Agents
            .Where(agent => agent.CapabilityProfile == "documentation")
            .Select(agent => agent.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expectedConsumers, consumers);

        Assert.True(
            source.CapabilityProfiles.Profiles.TryGetValue("documentation", out SquadCapabilityProfile? profile),
            "Capability profile 'documentation' is missing from products/kyber-squad/profiles/capabilities.yml.");
        Assert.NotNull(profile);
        Assert.True(
            profile.Permissions.TryGetValue("process.execute", out SquadPermissionDecision execDecision),
            "Capability profile 'documentation' must declare process.execute.");
        Assert.Equal(SquadPermissionDecision.Allow, execDecision);
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
