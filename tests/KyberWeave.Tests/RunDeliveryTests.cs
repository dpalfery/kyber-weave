using System.Diagnostics;
using System.Reflection;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Delivery;
using KyberWeave.Core.Processes;
using KyberWeave.Core.Review;
using KyberWeave.Core.RunStart;
using KyberWeave.Mcp;
using ModelContextProtocol.Server;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The deliver step: the pull request description it assembles from what a run recorded, the refusals that leave
/// everything as it was, and the one create-or-update it makes. Git runs for real in a scratch repository whose push
/// goes to a local bare repository; every gh call is answered by a script, so nothing reaches GitHub.
/// </summary>
public sealed class RunDeliveryTests : IDisposable
{
    private const string Branch = "claude/feat/widget-export";
    private const string ActivePlan = "docs/plans/2026-11-13-widget-export.md";
    private const string ArchivedPlan = "docs/archive/plans/2026-11-13-widget-export.md";
    private const string PrUrl = "https://github.com/acme/widgets/pull/42";

    private const string PlanText = """
        ---
        id: plans/widget-export
        title: Export widgets as CSV from the list page
        doc-type: plan
        status: archived
        component: Widget API
        autonomy: autonomous
        ---

        # Export widgets as CSV from the list page

        ## Decisions

        | ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
        |---|---|---|---|---|
        | D1 | Which delimiter? | A comma, with RFC 4180 quoting. Semicolons were considered. | Spreadsheet default. | ANSWERED: owner requirement |
        | D2 | Cap the export size? | Yes, at 50,000 rows, with a message past it. | No source names a limit. | autonomous: low-confidence, no authoritative source |
        | D3 | Stream or buffer? | Stream the rows. | Memory stays flat. | autonomous: recommendation accepted (2026-11-13) |
        | AP1 | Approve and execute. | Approved. | Conductor. | autonomous: recommendation accepted (2026-11-13) |

        ## Tasks

        ### T1: RED for the CSV writer
        ### T2: GREEN for the CSV writer
        ### T3: Export button on the list page

        ## Completion summary

        - NOT VERIFIED: the export in a real browser — no browser in the run environment.
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "kw-deliver-" + Guid.NewGuid().ToString("N"));
    private readonly string _work;
    private readonly string _remote;
    private readonly string _home;

    public RunDeliveryTests()
    {
        _work = Path.Combine(_root, "work");
        _remote = Path.Combine(_root, "remote.git");
        _home = Path.Combine(_root, "home");
        Directory.CreateDirectory(_work);
        Directory.CreateDirectory(_home);
    }

    public void Dispose()
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // ---------------------------------------------------------------- the description

    [Fact]
    public void TheEmbeddedTemplateIsTheSkillsTemplateByteForByte()
    {
        string product = File.ReadAllText(Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad", "skills",
            "create-pull-request", "assets", "pull-request-description.md"));

        Assert.Equal(product, PullRequestDescription.Template);
    }

    [Fact]
    public void WithNoHostTemplateTheStandardSectionsAppearInOrderAndEmptyOptionalOnesAreLeftOut()
    {
        string body = PullRequestDescription.Render(new PullRequestContent
        {
            Summary = "Lists could not be exported. Now they can.",
            Type = "Feature",
            Changes = ["CSV writer", "Export button"],
            Validation = ["Checks per task: 2 tasks, all passed (build, test)."],
        });

        Assert.Equal(
            ["Summary", "Type of change", "Changes", "Validation", "Not verified", "Documentation impact"],
            Headings(body));
        Assert.Contains("Nothing: every check listed above ran.", body, StringComparison.Ordinal);
        Assert.Contains("## Documentation impact\n\nNot recorded by the run.", body, StringComparison.Ordinal);
        Assert.DoesNotContain("{{", body, StringComparison.Ordinal);
        Assert.DoesNotContain("<!--", body, StringComparison.Ordinal);
    }

    [Fact]
    public void UnverifiedItemsDecisionsAndOpenFindingsGetTheirOwnSections()
    {
        string body = PullRequestDescription.Render(new PullRequestContent
        {
            Summary = "S.",
            NotVerified = ["the export in a real browser"],
            Decisions = ["Cap the export size: Yes, at 50,000 rows."],
            PendingReview = ["Minor: name is unclear (src/a.cs:3)."],
        });

        Assert.Contains("## Not verified\n\n- the export in a real browser", body, StringComparison.Ordinal);
        Assert.Contains("## Decisions made for you to check\n\n- Cap the export size", body, StringComparison.Ordinal);
        Assert.Contains("## Pending human review\n\n- Minor: name is unclear", body, StringComparison.Ordinal);
    }

    [Fact]
    public void AHostTemplateDecidesTheSectionsAndTheReviewerOnlySectionsFollowIt()
    {
        const string host = """
            ## Summary

            <!-- What changed and why -->

            ## Documentation impact

            <!-- Scale -->

            ## Test plan

            - [ ] `dotnet build`

            ## Checklist

            - [ ] No secrets or credentials in the diff
            """;

        string body = PullRequestDescription.Render(new PullRequestContent
        {
            Summary = "Lists can be exported.",
            Validation = ["Review: approved, no open findings after 1 round."],
            NotVerified = ["the export in a real browser"],
            Decisions = ["Cap the export size: Yes."],
            DocumentationImpact = "DOCS_REVIEW: update — the list page guide gained an Export section.",
            Notes = "Follow-up: add XLSX.",
        }, host);

        Assert.Equal(
            ["Summary", "Documentation impact", "Test plan", "Checklist", "Not verified", "Decisions made for you to check", "Notes"],
            Headings(body));
        Assert.Contains("## Test plan\n\n- Review: approved", body, StringComparison.Ordinal);
        Assert.Contains("## Checklist\n\n- [ ] No secrets or credentials in the diff", body, StringComparison.Ordinal);
        Assert.DoesNotContain("What changed and why", body, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet build", body, StringComparison.Ordinal);
    }

    [Fact]
    public void APlanGivesTheChangesTheLowConfidenceDecisionsAndTheUnverifiedItems()
    {
        PlanFacts facts = RunEvidence.ReadPlan(PlanText);

        Assert.Equal("Export widgets as CSV from the list page", facts.Title);
        Assert.Equal("Widget API", facts.Component);
        Assert.Equal(["The CSV writer", "Export button on the list page"], facts.Changes);
        Assert.Equal(["Cap the export size: Yes, at 50,000 rows, with a message past it."], facts.LowConfidenceDecisions);
        Assert.Equal(1, facts.AcceptedRecommendations);
        Assert.Equal(["the export in a real browser — no browser in the run environment"], facts.NotVerified);
    }

    [Fact]
    public void TaskGatesAndTheReviewBecomeShortReadableLines()
    {
        GateReport passed = new(GateReport.CurrentSchema, [new GateResult("build", true, 0, "passed"), new GateResult("test", true, 0, "passed")]);
        GateReport timedOut = new(GateReport.CurrentSchema, [new GateResult("build", true, 0, "passed"), new GateResult("test", true, -1, "timed out", Outcome: GateOutcome.TimedOut)]);

        (string? line, IReadOnlyList<string> notVerified) = RunEvidence.SummarizeTaskGates([passed, timedOut]);
        (string review, IReadOnlyList<string> pending) = RunEvidence.SummarizeReview(null);

        Assert.Equal("Checks per task: 2 tasks, all passed (build, test).", line);
        Assert.Equal(["The test check did not run to the end in 1 task (it ran past its time limit)."], notVerified);
        Assert.Equal("Review: no review result was recorded for this branch.", review);
        Assert.Empty(pending);
    }

    /// <summary>An approval can leave a major open after its one fix round, so the line counts by severity.</summary>
    [Fact]
    public void AnApprovalThatLeavesAMajorOpenSaysSo()
    {
        ReviewFinding major = new("model-placement/dto", "model-placement", ReviewSeverity.Major, 9, "src/A.cs", 1, "x", "Misplaced.", "e", "f");
        ReviewOutcome approved = new(ReviewVerdict.Approve, RiskGrade.Low, [major, major with { Id = "naming/a", Severity = ReviewSeverity.Minor }], [], []);

        (string line, IReadOnlyList<string> pending) = RunEvidence.SummarizeReview(new KyberWeave.Core.Docs.Delivery.ReviewEvidence(2, approved, []));

        Assert.Equal("Review: approved after 2 rounds; 1 major and 1 minor findings left for you, listed below.", line);
        Assert.Equal(2, pending.Count);
    }

    [Theory]
    [InlineData("feat(widget-api): export widgets as CSV [claude]", true)]
    [InlineData("feat: export widgets [claude]", true)]
    [InlineData("feat(api): export widgets", false)]
    [InlineData("feat(api): export widgets [claude] [codex]", false)]
    [InlineData("Export widgets [claude]", false)]
    public void TheTitleConventionIsCheckedMechanically(string title, bool follows)
    {
        Assert.Equal(follows, RunDelivery.TitleFollowsConvention(title, "claude"));
    }

    // ---------------------------------------------------------------- create, update and refusals

    [Fact]
    public void CreatesADraftPullRequestCommittingThePendingArchiveAndWritingNoFile()
    {
        GitRunRepository(archivePending: true);
        HashSet<string> before = Snapshot();
        ScriptedGh gh = new();

        DeliveryOutcome outcome = Deliver(gh, new DeliveryRequest("Lists could not be exported. Now they can be.",
            DocumentationImpact: "DOCS_REVIEW: update — the list page guide gained an Export section."));

        Assert.True(outcome.Delivered, Findings(outcome));
        Assert.True(outcome.Created);
        Assert.Equal(PrUrl, outcome.Link);
        Assert.True(outcome.IsDraft);
        Assert.Equal("main", outcome.Base);
        Assert.Equal("1 CI checks reported: 0 passed, 0 failed, 1 pending", outcome.Checks);
        Assert.Equal("feat(widget-api): export widgets as CSV from the list page [claude]", outcome.Title);

        string[] create = gh.Call("pr", "create");
        Assert.Equal(["--base", "main"], After(create, "--base"));
        Assert.Equal(["--head", Branch], After(create, "--head"));
        Assert.Contains("--draft", create);
        Assert.Equal(["--body-file", "-"], After(create, "--body-file"));
        Assert.Equal(["--assignee", "@me"], After(create, "--assignee"));
        Assert.DoesNotContain(gh.Calls, call => call.Contains("ready") || call.Contains("merge") || call.Contains("--reviewer"));
        string body = gh.Bodies.Single();
        Assert.Contains("- Checks per task: 1 task, all passed (build, format, test).", body, StringComparison.Ordinal);
        Assert.Contains("- Review: approved after 1 round; its 2 checks passed; 1 minor finding left for you, listed below.", body, StringComparison.Ordinal);
        Assert.Contains("## Pending human review\n\n- Minor: The file name does not say it writes CSV (src/Export.cs:1).", body, StringComparison.Ordinal);
        Assert.Contains("## Decisions made for you to check", body, StringComparison.Ordinal);

        Assert.Equal(string.Empty, Git(_work, "status", "--porcelain").Trim());
        Assert.Contains("docs(plan): archive widget-export plan", Git(_work, "log", "-1", "--format=%s"), StringComparison.Ordinal);
        Assert.Equal(Git(_work, "rev-parse", "HEAD").Trim(), Git(_remote, "rev-parse", $"refs/heads/{Branch}").Trim());
        Assert.Subset(before.Union([ArchivedPlan]).ToHashSet(StringComparer.Ordinal), Snapshot());
    }

    [Fact]
    public void UpdatesTheOpenPullRequestInsteadOfCreatingASecond()
    {
        GitRunRepository(archivePending: false);
        ScriptedGh gh = new() { OpenPullRequest = """[{"number":42,"url":"https://github.com/acme/widgets/pull/42","baseRefName":"main"}]""" };

        DeliveryOutcome outcome = Deliver(gh, new DeliveryRequest("Lists can be exported."));

        Assert.True(outcome.Delivered, Findings(outcome));
        Assert.False(outcome.Created);
        Assert.Equal(["pr", "edit", "42"], gh.Call("pr", "edit")[..3]);
        Assert.DoesNotContain(gh.Calls, call => call.Contains("create") || call.Contains("--draft"));
    }

    [Fact]
    public void AnOpenPullRequestIntoAnotherBaseIsLeftAloneForAPerson()
    {
        GitRunRepository(archivePending: false);
        ScriptedGh gh = new() { OpenPullRequest = """[{"number":42,"url":"u","baseRefName":"release"}]""" };

        DeliveryOutcome outcome = Deliver(gh, new DeliveryRequest("S."));

        Assert.False(outcome.Delivered);
        Assert.Contains(outcome.Report.Items, item => item.Code == RunDelivery.PullRequestFailed);
        Assert.DoesNotContain(gh.Calls, call => call.Contains("edit") || call.Contains("create"));
    }

    [Theory]
    [InlineData("""[{"url":"x","baseRefName":"main"}]""")]
    [InlineData("""[{"number":"1","baseRefName":"main"}]""")]
    [InlineData("""[{"number":99999999999,"baseRefName":"main"}]""")]
    [InlineData("""["not an object"]""")]
    [InlineData("""[{"number":1}]""")]
    [InlineData("""[{"number":1,"baseRefName":""}]""")]
    [InlineData("""[{"number":1,"baseRefName":"   "}]""")]
    [InlineData("""[{"number":1,"baseRefName":5}]""")]
    [InlineData("""[{"number":1,"baseRefName":null}]""")]
    [InlineData("""[{"number":1.5,"baseRefName":"main"}]""")]
    public void AMalformedPullRequestListIsARefusalThatNamesThePushedBranch(string list)
    {
        GitRunRepository(archivePending: false);
        ScriptedGh gh = new() { OpenPullRequest = list };

        DeliveryOutcome outcome = Deliver(gh, new DeliveryRequest("S."));

        Assert.False(outcome.Delivered);
        var refusal = Assert.Single(outcome.Report.Items, item => item.Code == RunDelivery.PullRequestFailed);
        Assert.Contains($"Branch '{Branch}' is already pushed", refusal.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"url":"u","isDraft":true,"baseRefName":"main","headRefName":"h"}""")]
    [InlineData("""{"number":"7","url":"u","isDraft":true,"baseRefName":"main","headRefName":"h"}""")]
    [InlineData("""{"number":7,"url":"u","isDraft":"yes","baseRefName":"main","headRefName":"h"}""")]
    [InlineData("""{"number":7,"url":5,"isDraft":true,"baseRefName":"main","headRefName":"h"}""")]
    [InlineData("""{"number":7,"url":"","isDraft":true,"baseRefName":"main","headRefName":"h","title":"t","body":""}""")]
    [InlineData("""{"number":7,"url":"  ","isDraft":true,"baseRefName":"main","headRefName":"h","title":"t","body":""}""")]
    [InlineData("""{"number":7,"url":"u","isDraft":true,"baseRefName":"","headRefName":"h","title":"t","body":""}""")]
    [InlineData("""{"number":7,"url":"u","isDraft":true,"baseRefName":"main","headRefName":" \t","title":"t","body":""}""")]
    [InlineData("""{"number":7,"url":"","isDraft":true,"baseRefName":"","headRefName":"","title":"t","body":""}""")]
    [InlineData("""{"number":7,"url":"u","isDraft":true,"baseRefName":"main","title":"t","body":""}""")]
    [InlineData("""{"number":7,"url":"u","isDraft":true,"baseRefName":"main","headRefName":"h","body":""}""")]
    [InlineData("""{"number":7,"url":"u","isDraft":true,"baseRefName":"main","headRefName":"h","title":"","body":""}""")]
    [InlineData("""{"number":7,"url":"u","isDraft":true,"baseRefName":"main","headRefName":"h","title":"t"}""")]
    [InlineData("""{"number":7,"url":"u","isDraft":true,"baseRefName":"main","headRefName":"h","title":"t","body":null}""")]
    [InlineData("""{"number":7,"url":"u","isDraft":null,"baseRefName":"main","headRefName":"h","title":"t","body":""}""")]
    [InlineData("[]")]
    public void AMalformedPullRequestViewIsARefusalThatNamesThePushedBranch(string view)
    {
        GitRunRepository(archivePending: false);
        ScriptedGh gh = new() { ViewResponse = view };

        DeliveryOutcome outcome = Deliver(gh, new DeliveryRequest("S."));

        Assert.False(outcome.Delivered);
        var refusal = Assert.Single(outcome.Report.Items, item => item.Code == RunDelivery.PullRequestFailed);
        Assert.Contains($"Branch '{Branch}' is already pushed", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOpenPullRequestWithNoUsableBaseSaysSoInsteadOfNamingAnEmptyBase()
    {
        GitRunRepository(archivePending: false);
        ScriptedGh gh = new() { OpenPullRequest = """[{"number":42,"baseRefName":""}]""" };

        DeliveryOutcome outcome = Deliver(gh, new DeliveryRequest("S."));

        var refusal = Assert.Single(outcome.Report.Items, item => item.Code == RunDelivery.PullRequestFailed);
        Assert.Contains("'baseRefName' is empty", refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("open into ''", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesWithoutASummary()
    {
        GitRunRepository(archivePending: false);
        ScriptedGh gh = new();

        AssertRefusedBeforeAnythingLeaves(Deliver(gh, new DeliveryRequest("  ")), gh, RunDelivery.InvalidRequest);
    }

    [Fact]
    public void RefusesOnTheBaseBranch()
    {
        GitRunRepository(archivePending: false);
        Git(_work, "checkout", "-q", "main");
        ScriptedGh gh = new();

        AssertRefusedBeforeAnythingLeaves(Deliver(gh, new DeliveryRequest("S.")), gh, RunDelivery.NotARunBranch);
    }

    /// <summary>
    /// The follow-up filing writes todos after the review and nobody commits them: the deliver step commits the todo
    /// folder, and the pull request lists each todo the branch added under Follow-up work filed.
    /// </summary>
    [Fact]
    public void CommitsTheFiledTodosAndListsThemUnderFollowUpWorkFiled()
    {
        GitRunRepository(archivePending: false);
        Write("docs/todo/README.md", "# Todos\n\n- [Move the shared DTOs](move-shared-dtos.md)\n");
        Write("docs/todo/move-shared-dtos.md",
            "---\nid: todo/move-shared-dtos\ntitle: Move the shared DTOs out of the Domain project\ndoc-type: todo\n---\n\n# Move the shared DTOs\n");
        ScriptedGh gh = new();

        DeliveryOutcome outcome = Deliver(gh, new DeliveryRequest("S."));

        Assert.True(outcome.Delivered, Findings(outcome));
        Assert.Equal(string.Empty, Git(_work, "status", "--porcelain").Trim());
        Assert.Contains("docs(todo): file the follow-up work this run found", Git(_work, "log", "-1", "--format=%s"), StringComparison.Ordinal);
        Assert.Contains(
            "## Follow-up work filed\n\n- Move the shared DTOs out of the Domain project (`docs/todo/move-shared-dtos.md`)",
            gh.Bodies.Single(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void AFollowUpSectionFollowsAHostTemplateThatDoesNotAskForIt()
    {
        string body = PullRequestDescription.Render(
            new PullRequestContent { Summary = "S.", FollowUps = ["Move the shared DTOs (`docs/todo/move-shared-dtos.md`)"] },
            "## Summary\n\n<!-- what -->\n");

        Assert.Equal(["Summary", "Not verified", "Follow-up work filed"], Headings(body));
    }

    [Fact]
    public void RefusesChangesItDoesNotOwn()
    {
        GitRunRepository(archivePending: true);
        File.WriteAllText(Path.Combine(_work, "src.cs"), "// a worker's uncommitted edit");
        ScriptedGh gh = new();

        DeliveryOutcome outcome = Deliver(gh, new DeliveryRequest("S."));

        AssertRefusedBeforeAnythingLeaves(outcome, gh, RunDelivery.UncommittedChanges);
        Assert.Contains("src.cs", Findings(outcome), StringComparison.Ordinal);
        Assert.NotEqual(string.Empty, Git(_work, "diff", "--cached", "--name-only").Trim());
    }

    [Fact]
    public void RefusesAPlanThatIsNotArchived()
    {
        GitRunRepository(archivePending: false, archived: false);
        ScriptedGh gh = new();

        AssertRefusedBeforeAnythingLeaves(Deliver(gh, new DeliveryRequest("S.")), gh, RunDelivery.PlanNotDeliverable);
    }

    /// <summary>The archived plan is found by git and read a moment later; if it is gone by then the run refuses and says so.</summary>
    [Fact]
    public void RefusesAPlanThatVanishesBetweenBeingFoundAndBeingRead()
    {
        GitRunRepository(archivePending: false);
        ScriptedGh gh = new();

        DeliveryOutcome outcome = Deliver(gh, new DeliveryRequest("S."), afterPlanFound: File.Delete);

        AssertRefusedBeforeAnythingLeaves(outcome, gh, RunDelivery.PlanNotDeliverable);
        Assert.Contains("is no longer there", Findings(outcome), StringComparison.Ordinal);
    }

    /// <summary>A plan that is there but cannot be read (a folder in its place) is the same refusal, with its reason.</summary>
    [Fact]
    public void RefusesAPlanThatCannotBeReadAndSaysWhy()
    {
        GitRunRepository(archivePending: false);
        ScriptedGh gh = new();

        DeliveryOutcome outcome = Deliver(gh, new DeliveryRequest("S."), afterPlanFound: path =>
        {
            File.Delete(path);
            Directory.CreateDirectory(path);
        });

        AssertRefusedBeforeAnythingLeaves(outcome, gh, RunDelivery.PlanNotDeliverable);
        Assert.Contains("could not be read", Findings(outcome), StringComparison.Ordinal);
    }

    /// <summary>A todo that is there but cannot be read is still listed, by its file name; the delivery goes on.</summary>
    [Fact]
    public void ATodoThatCannotBeReadIsListedByFileNameAndDoesNotFailTheDelivery()
    {
        GitRunRepository(archivePending: false);
        const string Unreadable = "docs/todo/unreadable-follow-up.md";
        Write("docs/todo/README.md", "# Todos\n");
        Write(Unreadable, "---\nid: todo/unreadable-follow-up\ntitle: Cannot read this\ndoc-type: todo\n---\n\n# X\n");
        int listings = 0;
        ScriptedGh gh = new()
        {
            AfterGit = arguments =>
            {
                if (arguments.Contains("--diff-filter=A") && ++listings == 2)
                {
                    string path = Path.Combine(_work, "docs", "todo", "unreadable-follow-up.md");
                    File.Delete(path);
                    Directory.CreateDirectory(path);
                }
            }
        };

        DeliveryOutcome outcome = Deliver(gh, new DeliveryRequest("S."));

        Assert.True(outcome.Delivered, Findings(outcome));
        Assert.Contains($"unreadable-follow-up (`{Unreadable}`)", gh.Bodies.Single(), StringComparison.Ordinal);
    }

    /// <summary>A host template that cannot be read falls back to the standard description rather than throwing.</summary>
    [Fact]
    public void AHostTemplateThatCannotBeReadIsNotUsedAndDoesNotThrow()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
            return;

        string repo = Path.Combine(_root, "template-repo");
        Directory.CreateDirectory(repo);
        string template = Path.Combine(repo, "pull_request_template.md");
        File.WriteAllText(template, "## Summary\n");
        File.SetUnixFileMode(template, UnixFileMode.None);
        try
        {
            Assert.Null(PullRequestDescription.ReadHostTemplate(repo));
        }
        finally
        {
            File.SetUnixFileMode(template, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    /// <summary>
    /// A todo the branch added is listed by git and read a moment later. One that is gone by then is left out of the
    /// follow-up list, and the delivery goes on with the todos that are still there.
    /// </summary>
    [Fact]
    public void ATodoThatVanishesWhileTheFollowUpsAreListedIsLeftOutAndTheRestAreListed()
    {
        GitRunRepository(archivePending: false);
        const string Kept = "docs/todo/kept-follow-up.md";
        const string Vanishing = "docs/todo/vanishing-follow-up.md";
        Write("docs/todo/README.md", "# Todos\n");
        Write(Kept, "---\nid: todo/kept-follow-up\ntitle: Keep this follow-up\ndoc-type: todo\n---\n\n# Keep\n");
        Write(Vanishing, "---\nid: todo/vanishing-follow-up\ntitle: Lose this follow-up\ndoc-type: todo\n---\n\n# Lose\n");
        int listings = 0;
        ScriptedGh gh = new()
        {
            AfterGit = arguments =>
            {
                // The second listing of added files is the follow-up one; the first finds the archived plan.
                if (arguments.Contains("--diff-filter=A") && ++listings == 2)
                    File.Delete(Path.Combine(_work, "docs", "todo", "vanishing-follow-up.md"));
            }
        };

        DeliveryOutcome outcome = Deliver(gh, new DeliveryRequest("S."));

        Assert.True(outcome.Delivered, Findings(outcome));
        Assert.Equal(2, listings);
        string body = gh.Bodies.Single();
        Assert.Contains($"Keep this follow-up (`{Kept}`)", body, StringComparison.Ordinal);
        Assert.DoesNotContain("vanishing-follow-up", body, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAzureDevOpsRemoteSendsTheConductorToTheDeliveryAgent()
    {
        GitRunRepository(archivePending: false);
        Git(_work, "remote", "set-url", "origin", "https://dev.azure.com/acme/widgets/_git/widgets");
        ScriptedGh gh = new();

        DeliveryOutcome outcome = Deliver(gh, new DeliveryRequest("S."));

        AssertRefusedBeforeAnythingLeaves(outcome, gh, RunDelivery.ProviderUnavailable);
        Assert.True(outcome.NeedsDeliveryAgent);
        Assert.Contains("Azure DevOps", Findings(outcome), StringComparison.Ordinal);
    }

    [Fact]
    public void ASignedOutGitHubCliSendsTheConductorToTheDeliveryAgent()
    {
        GitRunRepository(archivePending: true);
        ScriptedGh gh = new() { SignedIn = false };

        DeliveryOutcome outcome = Deliver(gh, new DeliveryRequest("S."));

        AssertRefusedBeforeAnythingLeaves(outcome, gh, RunDelivery.ProviderUnavailable);
        Assert.True(outcome.NeedsDeliveryAgent);
        Assert.NotEqual(string.Empty, Git(_work, "status", "--porcelain").Trim());
    }

    [Fact]
    public void AMissingGitHubCliSendsTheConductorToTheDeliveryAgent()
    {
        GitRunRepository(archivePending: false);
        ScriptedGh gh = new() { Installed = false };

        DeliveryOutcome outcome = Deliver(gh, new DeliveryRequest("S."));

        Assert.False(outcome.Delivered);
        Assert.True(outcome.NeedsDeliveryAgent);
        Assert.Equal(string.Empty, Git(_remote, "branch", "--list", Branch).Trim());
    }

    [Fact]
    public void AFailingTestFirstRunIsNotReadAsAFailedCheck()
    {
        GitRunRepository(archivePending: false);
        Write(".agent_scratchpad/T3-red/task-gate.json", ReviewJson.Write(new GateReport(GateReport.CurrentSchema,
            [new GateResult("test", true, 1, "failed")])));
        Write(".agent_scratchpad/T3/task-gate-red.json", ReviewJson.Write(new GateReport(GateReport.CurrentSchema,
            [new GateResult("test", true, 1, "failed")])));
        ScriptedGh gh = new();

        Assert.True(Deliver(gh, new DeliveryRequest("S.")).Delivered);

        Assert.Contains("- Checks per task: 1 task, all passed (build, format, test).", gh.Bodies.Single(), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- the MCP tool

    [Fact]
    public void TheDeliverToolReachesOutsideTheMachineAndRequiresAProjectPath()
    {
        MethodInfo method = typeof(DocsTools).GetMethods()
            .Single(candidate => candidate.GetCustomAttribute<McpServerToolAttribute>()?.Name == DocsTools.DeliverToolName);
        McpServerToolAttribute attribute = method.GetCustomAttribute<McpServerToolAttribute>()!;

        Assert.False(attribute.ReadOnly);
        Assert.False(attribute.Destructive);
        Assert.True(attribute.OpenWorld);
        ParameterInfo projectPath = method.GetParameters()[0];
        Assert.Equal("projectPath", projectPath.Name);
        Assert.False(projectPath.HasDefaultValue);
    }

    [Fact]
    public void TheDeliverToolReturnsTheLinkDraftStateAndChecks()
    {
        GitRunRepository(archivePending: false);
        File.WriteAllText(Path.Combine(_work, ".kyber-weave", "kyber-weave.yml"), Config);
        Git(_work, "add", "-A");
        Git(_work, "commit", "-q", "-m", "chore: config");
        ScriptedGh gh = new();
        DocsTools tools = DocsRootRegistry.CreateTools(_work, codeGraphRefreshEnabled: false,
            new DocsRootRegistry(_work, path => DocsRootRegistry.CreateTools(path, codeGraphRefreshEnabled: false, registry: null)));
        tools.DeliveryExecutor = gh;
        tools.DeliveryHome = _home;

        string response = tools.DeliverRun(_work, "Lists can be exported.");

        Assert.True(response.Contains("result: created", StringComparison.Ordinal), response);
        Assert.Contains($"pull request: {PrUrl}", response, StringComparison.Ordinal);
        Assert.Contains("draft: yes", response, StringComparison.Ordinal);
        Assert.Contains("checks: 1 CI checks reported", response, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- fixture

    private const string Config = "ontology:\n  docs-root: docs\n";

    private DeliveryOutcome Deliver(ScriptedGh gh, DeliveryRequest request, Action<string>? afterPlanFound = null) =>
        new RunDelivery(_work, KyberWeaveConfigLoader.LoadFromYaml(Config), gh, _home) { AfterPlanFound = afterPlanFound }.Run(request);

    private void AssertRefusedBeforeAnythingLeaves(DeliveryOutcome outcome, ScriptedGh gh, string code)
    {
        Assert.False(outcome.Delivered);
        Assert.Contains(outcome.Report.Items, item => item.Code == code);
        Assert.DoesNotContain(gh.Calls, call => call.Contains("pr"));
        Assert.Equal(string.Empty, Git(_remote, "branch", "--list", Branch).Trim());
    }

    /// <summary>
    /// A repository on the run branch with one commit of work, its base on <c>origin/main</c>, a run record, and the
    /// plan either archived and committed, archived and only staged (as the archive tool leaves it), or still active.
    /// </summary>
    private void GitRunRepository(bool archivePending, bool archived = true)
    {
        Git(_root, "init", "-q", "--bare", _remote);
        Git(_work, "init", "-q", "-b", "main");
        Git(_work, "config", "user.name", "t");
        Git(_work, "config", "user.email", "t@example.com");
        Write(".kyber-weave/.keep", string.Empty);
        Write("docs/plans/README.md", "# Plans\n");
        Write("README.md", "# Widgets\n");
        Git(_work, "add", "-A");
        Git(_work, "commit", "-q", "-m", "init");
        Git(_work, "remote", "add", "origin", "https://github.com/acme/widgets.git");
        Git(_work, "remote", "set-url", "--push", "origin", _remote);
        Git(_work, "update-ref", "refs/remotes/origin/main", "HEAD");
        Git(_work, "symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/main");
        Git(_work, "checkout", "-q", "-b", Branch);
        Write("src/Export.cs", "// export\n");
        Write(ActivePlan, PlanText);
        Git(_work, "add", "-A");
        Git(_work, "commit", "-q", "-m", "feat: export");
        WriteRunEvidence();
        if (archived)
        {
            Directory.CreateDirectory(Path.Combine(_work, "docs", "archive", "plans"));
            Git(_work, "mv", ActivePlan, ArchivedPlan);
            if (!archivePending)
                Git(_work, "commit", "-q", "-m", "docs(plan): archive");
        }

        string common = Path.GetFullPath(Path.Combine(_work, Git(_work, "rev-parse", "--git-common-dir").Trim()));
        RunBaseState.Write(_home, Branch, "origin/main", new RunStartPlace(common, _work, DateTimeOffset.UtcNow.AddMinutes(-5)));
    }

    /// <summary>One task's passing gate report and an approving review cycle with one minor finding, as the run leaves them.</summary>
    private void WriteRunEvidence()
    {
        Write(".gitignore", ".agent_scratchpad/\n");
        Git(_work, "add", ".gitignore");
        Git(_work, "commit", "-q", "-m", "chore: ignore scratch");
        Write(".agent_scratchpad/T3/task-gate.json", ReviewJson.Write(new GateReport(GateReport.CurrentSchema,
            [new GateResult("build", true, 0, "passed"), new GateResult("test", true, 0, "passed"), new GateResult("format", true, 0, "passed")])));
        string cycle = Path.Combine(_work, ".agent_scratchpad", "review", "review", "cycle-1");
        Directory.CreateDirectory(cycle);
        File.WriteAllText(Path.Combine(cycle, "gates.json"), ReviewJson.Write(new GateReport(GateReport.CurrentSchema,
            [new GateResult("build", true, 0, "passed"), new GateResult("test", true, 0, "passed")])));
        File.WriteAllText(Path.Combine(cycle, "findings.json"), ReviewJson.Write(new FindingsReport(FindingsReport.CurrentSchema,
            [new ReviewFinding("naming/vague", "naming", ReviewSeverity.Minor, 8, "src/Export.cs", 1, "// export",
                "The file name does not say it writes CSV.", "Read the file.", "A reader looks for the CSV writer elsewhere.", "Rename it CsvExport.cs.")],
            ["src/Export.cs"], 1)));
        string head = Git(_work, "rev-parse", "HEAD").Trim();
        ReviewCycles.WriteIdentity(cycle, new ReviewCycleIdentity(Branch, Git(_work, "merge-base", "origin/main", "HEAD").Trim(), head));
    }

    private void Write(string relative, string text)
    {
        string path = Path.Combine(_work, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private HashSet<string> Snapshot() =>
        Directory.EnumerateFiles(_work, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(_work, path).Replace('\\', '/'))
            .Where(path => !path.StartsWith(".git/", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

    private static string[] Headings(string body) =>
        [.. body.Split('\n').Where(line => line.StartsWith("## ", StringComparison.Ordinal)).Select(line => line[3..])];

    private static string[] After(string[] call, string flag)
    {
        int index = Array.IndexOf(call, flag);
        return index < 0 || index + 1 >= call.Length ? [] : [call[index], call[index + 1]];
    }

    private static string Findings(DeliveryOutcome outcome) =>
        string.Join(" | ", outcome.Report.Items.Select(item => $"{item.Code}: {item.Message}"));

    private static string Git(string directory, params string[] arguments)
    {
        ProcessResult result = RealGit(directory, arguments, string.Empty);
        Assert.True(result.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {result.StandardError}");
        return result.StandardOutput;
    }

    private static ProcessResult RealGit(string directory, IEnumerable<string> arguments, string standardInput)
    {
        ProcessStartInfo startInfo = new("git")
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        startInfo.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(directory, ".no-global-gitconfig");
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);
        return ProcessRunner.Run(startInfo, standardInput, TimeSpan.FromSeconds(60));
    }

    /// <summary>Runs git for real in the scratch repository, and answers every gh call from a script, recording each.</summary>
    private sealed class ScriptedGh : IProcessExecutor
    {
        public bool SignedIn { get; init; } = true;

        public bool Installed { get; init; } = true;

        public string OpenPullRequest { get; init; } = "[]";

        public string? ViewResponse { get; init; }

        /// <summary>Called with the arguments of each git command after it ran, to change the checkout between two steps.</summary>
        public Action<string[]>? AfterGit { get; init; }

        public List<string[]> Calls { get; } = [];

        public List<string> Bodies { get; } = [];

        public string[] Call(string first, string second) =>
            Calls.Single(call => call.Length > 1 && call[0] == first && call[1] == second);

        public ProcessResult Run(ProcessStartInfo startInfo, string standardInput)
        {
            string[] arguments = [.. startInfo.ArgumentList];
            if (startInfo.FileName == "git")
            {
                ProcessResult result = RealGit(startInfo.WorkingDirectory, arguments, standardInput);
                AfterGit?.Invoke(arguments);
                return result;
            }

            Calls.Add(arguments);
            if (!Installed)
                throw new System.ComponentModel.Win32Exception(2, "No such file or directory");
            if (arguments.Contains("--body-file"))
                Bodies.Add(standardInput);
            return arguments switch
            {
                ["--version"] => new ProcessResult(0, "gh version 2.80.0", string.Empty),
                ["auth", "status", ..] => SignedIn ? new ProcessResult(0, "Logged in", string.Empty) : new ProcessResult(1, string.Empty, "You are not logged into any GitHub hosts."),
                ["pr", "list", ..] => new ProcessResult(0, OpenPullRequest, string.Empty),
                ["pr", "create", ..] => new ProcessResult(0, PrUrl + "\n", string.Empty),
                ["pr", "edit", ..] => new ProcessResult(0, PrUrl + "\n", string.Empty),
                ["pr", "view", ..] => new ProcessResult(0, ViewResponse ?? View(), string.Empty),
                _ => new ProcessResult(1, string.Empty, "unexpected gh call"),
            };
        }

        private string View()
        {
            string[] create = Calls.LastOrDefault(call => call is ["pr", "create", ..] or ["pr", "edit", ..]) ?? [];
            int title = Array.IndexOf(create, "--title");
            string sentTitle = title >= 0 ? create[title + 1] : string.Empty;
            string body = Bodies.LastOrDefault() ?? string.Empty;
            return System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["number"] = 42,
                ["url"] = PrUrl,
                ["isDraft"] = true,
                ["baseRefName"] = "main",
                ["headRefName"] = Branch,
                ["title"] = sentTitle,
                ["body"] = body,
                ["statusCheckRollup"] = new[] { new Dictionary<string, string> { ["status"] = "IN_PROGRESS", ["conclusion"] = "" } },
            });
        }
    }
}
