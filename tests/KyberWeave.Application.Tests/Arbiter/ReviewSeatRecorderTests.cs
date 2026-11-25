using System.Text.Json;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.RunStart;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// A returned review lens dispatch is saved as its seat file by the hook, on the synchronous and the background
/// path, so a reviewer with no file-write tool still leaves the folder <c>review verdict</c> reads.
/// </summary>
public sealed class ReviewSeatRecorderTests : IDisposable
{
    private const string Reply = "NO FINDINGS: nothing to report\nREVIEW.md sections checked: all";

    private readonly InMemoryArbiterHost _host = InMemoryArbiterHost.Create();

    public void Dispose() => _host.Dispose();

    private sealed class PassThroughGitFacts : IArbiterGitFacts
    {
        public ArbiterFactSet Enrich(ArbiterFactSet facts, TriggerClassification classification, ArbiterEvent ev) => facts;
    }

    private sealed class PassThroughPlanReader : IArbiterPlanReader
    {
        public ArbiterFactSet Enrich(ArbiterFactSet facts, TriggerClassification classification) => facts;
    }

    private static readonly KyberWeaveConfig Config = new()
    {
        Arbiter = new ArbiterConfig { Enabled = true, Rules = [] },
        Ontology = OntologyConfig.ProductDefaults.WithDocsRoot("docs"),
    };

    private string Cycle(string? root = null) =>
        Path.Combine(root ?? _host.RepoRoot, ".agent_scratchpad", "feat", "review", "cycle-1");

    private string Seat(string lens = "correctness", string? root = null) => Path.Combine(Cycle(root), "seats", lens + ".md");

    private static string Prompt(string packet, string? lensHeader = "correctness") =>
        (lensHeader is null ? string.Empty : "KYBER-ARBITER: true\nLENS: " + lensHeader + "\nREVIEW_RUN: run-1\n\n")
        + "Packet: " + packet + "\n\nApply the lens.";

    private string Packet(string lens = "correctness", string? root = null) =>
        Path.Combine(Cycle(root), "packets", lens + ".md");

    private ArbiterHookDecisionEngine Engine() => _host.Engine(
        providerFactory: _ => FakeArbiterProvider.Answering("allow", 0.9),
        gitFactsFactory: _ => new PassThroughGitFacts(),
        planReaderFactory: _ => new PassThroughPlanReader(),
        homeDirectory: () => _host.RepoRoot);

    private (HookContext Context, StringWriter Log) Context()
    {
        StringWriter log = new();
        return (new HookContext(_host.RepoRoot, () => "decision-seat", log), log);
    }

    private static string CopilotResult(string text) => JsonSerializer.Serialize(new { textResultForLlm = text, resultType = "success" });

    private HookOutcome Sync(string prompt, string toolOutput, HookContext context) =>
        Engine().DecidePostDispatch("claude", "code-reviewer", "review-lens", prompt, toolOutput, Config, context);

    /// <summary>A synchronous return is saved unchanged as its lens's seat file.</summary>
    [Fact]
    public void ASyncReturnWritesTheSeatFile()
    {
        (HookContext context, _) = Context();

        _ = Sync(Prompt(Packet()), CopilotResult(Reply), context);

        Assert.Equal(Reply, _host.Files.ReadAllText(Seat()));
    }

    /// <summary>A Claude result's text blocks are the reply.</summary>
    [Fact]
    public void AClaudeContentResultWritesItsTextBlocks()
    {
        (HookContext context, _) = Context();
        string result = JsonSerializer.Serialize(new { status = "completed", content = new[] { new { type = "text", text = Reply } } });

        _ = Sync(Prompt(Packet(), lensHeader: null), result, context);

        Assert.Equal(Reply, _host.Files.ReadAllText(Seat()));
    }

    /// <summary>A background agent's turn is saved through the stored dispatch's prompt.</summary>
    [Fact]
    public void ABackgroundReturnWritesTheSeatFile()
    {
        (HookContext context, _) = Context();
        BackgroundDispatch dispatch = new("copilot-cli", "session-1", "agent-1", "code-reviewer", "review-lens", Prompt(Packet()));
        _host.Get<IBackgroundDispatchStore>().Save(_host.ArbiterDirectory, new BackgroundDispatchRecord(dispatch, "pre-1"));

        _ = Engine().DecideBackgroundReturn("copilot-cli", "session-1", "agent-1", Reply, Config, context);

        Assert.Equal(Reply, _host.Files.ReadAllText(Seat()));
    }

    /// <summary>A second reply for the same lens is appended after a blank line, not written over the first.</summary>
    [Fact]
    public void ASecondReplyAppendsAfterABlankLine()
    {
        (HookContext context, _) = Context();
        const string Second = "Discovery: codegraph=1 docs=0 direct-reads=2";

        _ = Sync(Prompt(Packet()), CopilotResult(Reply), context);
        _ = Sync(Prompt(Packet()), CopilotResult(Second), context);

        Assert.Equal(Reply + "\n\n" + Second, _host.Files.ReadAllText(Seat()));
    }

    /// <summary>The same reply seen twice is not appended again, so a finding id is never repeated.</summary>
    [Fact]
    public void AnIdenticalReplyIsNotAppendedTwice()
    {
        (HookContext context, _) = Context();

        _ = Sync(Prompt(Packet()), CopilotResult(Reply), context);
        _ = Sync(Prompt(Packet()), CopilotResult(Reply), context);

        Assert.Equal(Reply, _host.Files.ReadAllText(Seat()));
    }

    /// <summary>A reply that is only a substring of an entry the file holds is a different reply and is appended.</summary>
    [Fact]
    public void AReplyThatIsASubstringOfTheFileIsAppended()
    {
        (HookContext context, _) = Context();
        const string Substring = "NO FINDINGS";

        _ = Sync(Prompt(Packet()), CopilotResult(Reply), context);
        _ = Sync(Prompt(Packet()), CopilotResult(Substring), context);

        Assert.Equal(Reply + "\n\n" + Substring, _host.Files.ReadAllText(Seat()));
    }

    /// <summary>A tool result whose JSON is not a string or an object carries no reply, so no seat file is written.</summary>
    [Theory]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("null")]
    public void ANonObjectJsonResultWritesNothing(string toolOutput)
    {
        (HookContext context, _) = Context();

        _ = Sync(Prompt(Packet()), toolOutput, context);

        Assert.False(_host.Files.FileExists(Seat()));
    }

    /// <summary>
    /// An oversize host payload is refused rather than parsed and appended to a seat file. The
    /// payload is a JSON result so removing the bound would parse it and write the file.
    /// </summary>
    [Fact]
    public void AnOversizeResultWritesNothing()
    {
        (HookContext context, _) = Context();

        _ = Sync(Prompt(Packet()), CopilotResult(new string('x', 262_145)), context);

        Assert.False(_host.Files.FileExists(Seat()));
    }

    /// <summary>
    /// A reply that follows a single newline in an earlier reply is a different reply: only a
    /// blank-line-separated entry counts as already held.
    /// </summary>
    [Fact]
    public void AReplyAfterASingleNewlineInAnEarlierReplyIsAppended()
    {
        (HookContext context, _) = Context();
        const string First = "line1\nline2";
        const string Second = "line2";

        _ = Sync(Prompt(Packet()), CopilotResult(First), context);
        _ = Sync(Prompt(Packet()), CopilotResult(Second), context);

        Assert.Equal(First + "\n\n" + Second, _host.Files.ReadAllText(Seat()));
    }

    /// <summary>An empty reply writes nothing.</summary>
    [Fact]
    public void AnEmptyReplyWritesNothing()
    {
        (HookContext context, _) = Context();

        _ = Sync(Prompt(Packet()), CopilotResult("  \n"), context);

        Assert.False(_host.Files.FileExists(Seat()));
    }

    /// <summary>A packet outside the repository and its worktrees is refused and logged.</summary>
    [Fact]
    public void APacketOutsideTheRepositoryIsRefused()
    {
        (HookContext context, StringWriter log) = Context();
        string outside = ArbiterTestRoots.Under("elsewhere");

        _ = Sync(Prompt(Packet(root: outside)), CopilotResult(Reply), context);

        Assert.False(_host.Files.FileExists(Seat(root: outside)));
        Assert.Contains("outside the repository", log.ToString(), StringComparison.Ordinal);
    }

    /// <summary>A packet path that climbs out of the repository with <c>..</c> is refused.</summary>
    [Fact]
    public void ATraversingPacketPathIsRefused()
    {
        (HookContext context, StringWriter log) = Context();
        string outside = ArbiterTestRoots.Under("elsewhere");
        string traversing = Path.Combine(_host.RepoRoot, "..", Path.GetFileName(outside), ".agent_scratchpad", "feat", "review", "cycle-1", "packets", "correctness.md");

        _ = Sync(Prompt(traversing), CopilotResult(Reply), context);

        Assert.False(_host.Files.FileExists(Seat(root: outside)));
        Assert.Contains("outside the repository", log.ToString(), StringComparison.Ordinal);
    }

    /// <summary>A packet in another worktree of the run's repository is accepted.</summary>
    [Fact]
    public void APacketInAnotherWorktreeOfTheRepositoryIsAccepted()
    {
        string other = ArbiterTestRoots.Under("worktree-b");
        StringWriter log = new();
        HookRunContext run = new(
            _host.RepoRoot, null, null, _host.RepoRoot, [_host.RepoRoot, other], null, null, null, false,
            HookRunContextSource.PayloadCwd, []);
        HookContext context = new(_host.RepoRoot, () => "decision-seat", log, RunContext: run);

        _ = Sync(Prompt(Packet(root: other)), CopilotResult(Reply), context);

        Assert.Equal(Reply, _host.Files.ReadAllText(Seat(root: other)));
    }

    /// <summary>A LENS header that differs from the packet's lens is refused and logged.</summary>
    [Fact]
    public void ALensHeaderThatDisagreesWithThePacketIsRefused()
    {
        (HookContext context, StringWriter log) = Context();

        _ = Sync(Prompt(Packet("security"), lensHeader: "correctness"), CopilotResult(Reply), context);

        Assert.False(_host.Files.FileExists(Seat("security")));
        Assert.False(_host.Files.FileExists(Seat("correctness")));
        Assert.Contains("differs from its packet", log.ToString(), StringComparison.Ordinal);
    }

    /// <summary>A dispatch with no review packet writes nothing.</summary>
    [Fact]
    public void ANonReviewDispatchWritesNothing()
    {
        (HookContext context, _) = Context();
        string workerPacket = Path.Combine(_host.RepoRoot, ".agent_scratchpad", "feat", "packets", "T1.md");

        _ = Sync("Packet: " + workerPacket + "\n\nDo the task.", CopilotResult(Reply), context);

        Assert.Empty(_host.Files.EnumerateFiles(_host.RepoRoot, "*.md", recursive: true));
    }

    /// <summary>A write that fails is logged and the dispatch is still allowed.</summary>
    [Fact]
    public void AFailedWriteIsLoggedAndTheDispatchIsStillAllowed()
    {
        (HookContext context, StringWriter log) = Context();
        _host.Files.CreateDirectory(Cycle());
        _host.Files.WriteAllText(Path.Combine(Cycle(), "seats"), "a file where the seats folder belongs");

        HookOutcome outcome = Sync(Prompt(Packet()), CopilotResult(Reply), context);

        Assert.Equal(HookOutcomeKind.Allow, outcome.Kind);
        Assert.Contains("review seat was not saved", log.ToString(), StringComparison.Ordinal);
    }

    private const string Wrapper =
        "This agent's report was delivered to you as a message from \"a52bf17cfe6305406\" (its SubagentHandback call). " +
        "Read it there; it is not repeated here.";

    /// <summary>A reply that is only Claude Code's delivery notice is not a report, so no seat file is written.</summary>
    [Fact]
    public void AWrapperOnlyReplyIsRefused()
    {
        (HookContext context, StringWriter log) = Context();

        _ = Sync(Prompt(Packet()), CopilotResult(Wrapper), context);

        Assert.False(_host.Files.FileExists(Seat()));
        Assert.Contains("delivery notice", log.ToString(), StringComparison.Ordinal);
    }

    /// <summary>A seat file that holds only the notice is replaced by the real reply, not appended to.</summary>
    [Fact]
    public void ASeatFileHoldingOnlyAWrapperIsReplacedByTheRealReply()
    {
        (HookContext context, _) = Context();
        _host.Files.CreateDirectory(Path.GetDirectoryName(Seat())!);
        _host.Files.WriteAllText(Seat(), Wrapper + "\n");

        _ = Sync(Prompt(Packet()), CopilotResult(Reply), context);

        Assert.Equal(Reply, _host.Files.ReadAllText(Seat()));
    }

    /// <summary>A report that opens with the notice's words but carries findings is a report, and is saved.</summary>
    [Fact]
    public void AReplyThatOpensLikeTheNoticeButCarriesFindingsIsSaved()
    {
        (HookContext context, _) = Context();
        string report = "This agent's report was delivered to you as a message from the lens; findings follow.\n"
            + "F1 [major] src/A.cs:12 the seat holds the wrapper.";

        _ = Sync(Prompt(Packet()), CopilotResult(report), context);

        Assert.Equal(report, _host.Files.ReadAllText(Seat()));
    }

    /// <summary>
    /// A relative packet path with no active run would resolve against whatever checkout the hook process started in,
    /// so it is not saved, and the log says why.
    /// </summary>
    [Fact]
    public void ARelativePacketPathWithNoActiveRunIsNotSaved()
    {
        (HookContext context, StringWriter log) = Context();

        _ = Sync(Prompt(".agent_scratchpad/feat/review/cycle-1/packets/correctness.md"), CopilotResult(Reply), context);

        Assert.False(_host.Files.FileExists(Seat()));
        Assert.Single(
            log.ToString().Split('\n'),
            line => line.Contains("relative packet path", StringComparison.Ordinal));
    }

    /// <summary>A relative packet path with an active run is saved under the run's worktree.</summary>
    [Fact]
    public void ARelativePacketPathWithAnActiveRunIsSavedUnderTheRunWorktree()
    {
        string worktree = ArbiterTestRoots.Under("run-worktree");
        StringWriter log = new();
        HookRunContext run = new(
            _host.RepoRoot, null, new RunRecord(_host.RepoRoot, "branch", worktree), worktree,
            [_host.RepoRoot, worktree], null, null, null, false, HookRunContextSource.ActiveRun, []);
        HookContext context = new(_host.RepoRoot, () => "decision-seat", log, RunContext: run);

        _ = Sync(Prompt(".agent_scratchpad/feat/review/cycle-1/packets/correctness.md"), CopilotResult(Reply), context);

        Assert.Equal(Reply, _host.Files.ReadAllText(Seat(root: worktree)));
    }
}
