using System.Diagnostics;
using System.Text.Json;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.RunStart;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// <c>KW-ARB-RUN-001</c> and the Copilot conductor guard's copy of it: an orchestrator may not hand a
/// specialist that writes files any work until <c>run start</c> has put a branch in place. The evidence
/// is a real git repository and the run record <c>run start</c> writes.
/// </summary>
public sealed class ArbiterRunStartGuardTests : IDisposable
{
    private const string RuleId = "KW-ARB-RUN-001";
    private const string Branch = "copilot/feat/add-widget";

    private readonly TempDirectory _home = new();
    private readonly TempDirectory _repoParent = new();
    private readonly string _repo;

    public ArbiterRunStartGuardTests()
    {
        _repo = Path.Combine(_repoParent.Path, "repo");
        Directory.CreateDirectory(_repo);
        Git(_repo, "init", "-q", "-b", "main");
        Git(_repo, "config", "user.email", "arbiter@example.com");
        Git(_repo, "config", "user.name", "Arbiter");
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "one\n");
        Git(_repo, "add", "a.txt");
        Git(_repo, "commit", "-q", "-m", "initial");
    }

    public void Dispose()
    {
        _home.Dispose();
        _repoParent.Dispose();
    }

    [Fact]
    public void NoRunRecord_IsNotStarted()
    {
        Assert.False(RunStartEvidence.Started(_repo, _home.Path));
    }

    [Fact]
    public void ARunBranchedInPlace_IsStarted()
    {
        StartInPlace();

        Assert.True(RunStartEvidence.Started(_repo, _home.Path));
    }

    [Fact]
    public void ARunInAWorktree_IsStartedFromTheMainCheckoutToo()
    {
        string worktree = Path.Combine(_repoParent.Path, "wt");
        Git(_repo, "worktree", "add", "-q", "-b", Branch, worktree);
        Record(worktree);

        Assert.True(RunStartEvidence.Started(_repo, _home.Path));
        Assert.True(RunStartEvidence.Started(worktree, _home.Path));
    }

    [Fact]
    public void ARunWhoseBranchIsNoLongerCheckedOut_IsNotStarted()
    {
        StartInPlace();
        Git(_repo, "switch", "-q", "main");

        Assert.False(RunStartEvidence.Started(_repo, _home.Path));
    }

    [Fact]
    public void ARunWhoseWorktreeIsGone_IsNotStarted()
    {
        RunBaseState.Write(
            _home.Path, Branch, "origin/main",
            new RunStartPlace(CommonDir(), Path.Combine(_repoParent.Path, "gone"), DateTimeOffset.UtcNow));

        Assert.False(RunStartEvidence.Started(_repo, _home.Path));
    }

    [Fact]
    public void ARunOfAnotherRepository_DoesNotCount()
    {
        Git(_repo, "switch", "-q", "-c", Branch);
        RunBaseState.Write(
            _home.Path, Branch, "origin/main",
            new RunStartPlace(Path.Combine(_repoParent.Path, "other", ".git"), _repo, DateTimeOffset.UtcNow));

        Assert.False(RunStartEvidence.Started(_repo, _home.Path));
    }

    [Fact]
    public void ARecordFromBeforeThePlaceWasRecorded_IsSkipped()
    {
        Git(_repo, "switch", "-q", "-c", Branch);
        RunBaseState.Write(_home.Path, Branch, "origin/main");

        Assert.False(RunStartEvidence.Started(_repo, _home.Path));
    }

    [Fact]
    public void ADirectoryOutsideAnyRepository_IsUnknown()
    {
        Assert.Null(RunStartEvidence.Started(_home.Path, _home.Path));
    }

    [Fact]
    public void Rule_EscalatesAWriterDispatchFromTheConductorWithNoRun()
    {
        Assert.Equal("not-started", Answer("delegate", "conductor", "csharp-dev", "Add the widget.", started: false));
        Assert.Equal("escalate", Rule().Effects["not-started"]);
    }

    [Fact]
    public void Rule_AllowsOnceTheRunIsStarted()
    {
        Assert.Equal("started", Answer("delegate", "conductor", "csharp-dev", "Add the widget.", started: true));
    }

    [Fact]
    public void Rule_IsUndecidedWhenTheEvidenceCouldNotBeRead()
    {
        Assert.Equal(RuleEngine.Undecidable, Answer("delegate", "conductor", "csharp-dev", "Add the widget.", started: null));
    }

    [Theory]
    [InlineData("architect")]
    [InlineData("product-owner")]
    public void Rule_EscalatesAPlannerDispatchFromTheConductorWithNoRun(string target)
    {
        Assert.Contains(
            ArbiterConfig.ProductDefaults.Rules,
            rule => string.Equals(rule.Id, RuleId, StringComparison.Ordinal)
                && string.Equals(rule.Trigger, "delegate.planner", StringComparison.Ordinal));
        Assert.Equal("not-started", Answer("delegate.planner", "conductor", target, "PLAN: true\nPlan the widget.", started: false));
        Assert.Equal("started", Answer("delegate.planner", "conductor", target, "PLAN: true\nPlan the widget.", started: true));
    }

    [Theory]
    [InlineData("investigate", "conductor", "github-devops", "RUN-START: true\nkyber-weave run start --harness claude --type feat --slug a")]
    [InlineData("investigate", "conductor", "github-devops", "kyber-weave run start --harness claude --type feat --slug a")]
    [InlineData("investigate", "conductor", "research-agent", "Find the widget.")]
    [InlineData("investigate", "architect", "research-agent", "Find the widget.")]
    [InlineData("investigate", "unidentified", "csharp-dev", "Add the widget.")]
    [InlineData("investigate", "general-purpose", "csharp-dev", "Add the widget.")]
    public void Rule_DoesNotApplyOutsideAnOrchestratorHandingWorkToAWriter(string trigger, string caller, string target, string prompt)
    {
        Assert.Equal("not-applicable", Answer(trigger, caller, target, prompt, started: false));
    }

    [Fact]
    public void Rule_CoversTheBugCrusher()
    {
        Assert.Equal("not-started", Answer("investigate", "bug-crusher", "typescript-dev", "Fix the parser.", started: false));
    }

    [Fact]
    public void HookFacts_FillRunStartedFromTheRecord()
    {
        Assert.Equal(false, HookFact("csharp-dev"));

        StartInPlace();

        Assert.Equal(true, HookFact("csharp-dev"));
    }

    [Fact]
    public void HookFacts_LeaveRunStartedAbsentForASpecialistThatWritesNothing()
    {
        Assert.Null(HookFact("research-agent"));
    }

    [Fact]
    public void HookFacts_FillRunStartedForAPlanner()
    {
        Assert.Equal(false, HookFact("architect"));

        StartInPlace();

        Assert.Equal(true, HookFact("architect"));
    }

    [Fact]
    public void Guard_DeniesTheConductorsWriterDispatchWithNoRun()
    {
        string answer = Guard(Task("csharp-dev", "Add the widget."));

        Assert.Contains("deny", answer, StringComparison.Ordinal);
        Assert.Contains(RuleId, answer, StringComparison.Ordinal);
        Assert.Contains("kyber-weave run start --harness", answer, StringComparison.Ordinal);
        Assert.Contains("RUN-START: true", answer, StringComparison.Ordinal);
    }

    [Fact]
    public void Guard_AllowsTheWriterDispatchOnceTheRunIsStarted()
    {
        StartInPlace();

        Assert.Empty(Guard(Task("csharp-dev", "Add the widget.")));
    }

    [Theory]
    [InlineData("architect")]
    [InlineData("product-owner")]
    public void Guard_DeniesTheConductorsPlannerDispatchWithNoRun(string target)
    {
        string answer = Guard(Task(target, "Plan the widget."));

        Assert.Contains("deny", answer, StringComparison.Ordinal);
        Assert.Contains(RuleId, answer, StringComparison.Ordinal);

        StartInPlace();

        Assert.Empty(Guard(Task(target, "Plan the widget.")));
    }

    [Theory]
    [InlineData("github-devops", "kyber-weave run start --harness copilot --type feat --slug add-widget")]
    [InlineData("research-agent", "Find the widget.")]
    public void Guard_AllowsDispatchesThatWriteNoProjectFiles(string target, string prompt)
    {
        Assert.Empty(Guard(Task(target, prompt)));
    }

    [Fact]
    public void Guard_AllowsAndLogsWhenTheRepositoryCannotBeRead()
    {
        using StringWriter log = new();
        using StringWriter stdout = new();
        string json = JsonSerializer.Serialize(new
        {
            cwd = _home.Path,
            toolName = "task",
            toolArgs = new { agent_type = "csharp-dev", prompt = "Add the widget." },
        });

        ConductorGuard.Run(json, stdout, log, copilotHome: "/nowhere/.copilot", homeDirectory: _home.Path);

        Assert.Empty(stdout.ToString());
        Assert.Contains("could not read whether a run was started", log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void EscalationNext_NamesTheRunStartFix()
    {
        string next = ArbiterEscalationEnvelope.NextFor(
            "delegate",
            "conductor",
            1,
            [new ArbiterEnvelopeEntry(RuleId, "q", new ArbiterAnswerText("not-started (step 0)"), "e")]);

        Assert.Equal(RunStartEvidence.Remedy, next);
    }

    private static ArbiterRule Rule() =>
        ArbiterConfig.ProductDefaults.Rules.First(rule => string.Equals(rule.Id, RuleId, StringComparison.Ordinal));

    private static string Answer(string trigger, string caller, string target, string prompt, bool? started)
    {
        ArbiterFactSet facts = TriggerFactBuilder.Build(
            trigger, target, prompt, caller, ArbiterCallerSources.Harness, KyberWeaveConfig.ProductDefaults);
        if (started is not null)
        {
            facts = facts.With("run.started", started.Value, ArbiterFactLabel.Derived);
        }

        return RuleEngine.Decide(Rule(), facts);
    }

    private object? HookFact(string target)
    {
        ArbiterEvent ev = new()
        {
            Harness = "claude",
            Phase = "pre",
            Target = target,
            Prompt = "Add the widget.",
            HarnessCaller = "conductor",
            Cwd = _repo,
            IsDispatch = true,
        };
        TriggerClassification classification = TriggerClassifier.ClassifySingle(ev, target, ev.Prompt);
        ArbiterFactSet facts = TriggerFactBuilder.Build(classification, ev.Prompt, KyberWeaveConfig.ProductDefaults);
        facts = new HookGitFacts(_repo, _home.Path).Enrich(facts, classification, ev);
        return facts.TryGet("run.started", out ArbiterFact? fact) ? fact.Value : null;
    }

    private string Task(string target, string prompt) => JsonSerializer.Serialize(new
    {
        cwd = _repo,
        sessionId = "s1",
        toolName = "task",
        toolArgs = new { agent_type = target, prompt },
    });

    private string Guard(string json)
    {
        using StringWriter stdout = new();
        ConductorGuard.Run(json, stdout, TextWriter.Null, copilotHome: "/nowhere/.copilot", homeDirectory: _home.Path);
        return stdout.ToString();
    }

    private void StartInPlace()
    {
        Git(_repo, "switch", "-q", "-c", Branch);
        Record(_repo);
    }

    private void Record(string worktree) =>
        RunBaseState.Write(_home.Path, Branch, "origin/main", new RunStartPlace(CommonDir(), worktree, DateTimeOffset.UtcNow));

    private string CommonDir() => GitFacts.GetCommonDirectory(_repo) ?? throw new InvalidOperationException("no git dir");

    private static void Git(string workingDirectory, params string[] args)
    {
        using Process process = new();
        process.StartInfo.FileName = "git";
        process.StartInfo.WorkingDirectory = workingDirectory;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        foreach (string arg in args)
            process.StartInfo.ArgumentList.Add(arg);
        Assert.True(process.Start(), "git could not be started.");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} failed: {stdout} {stderr}");
    }
}
