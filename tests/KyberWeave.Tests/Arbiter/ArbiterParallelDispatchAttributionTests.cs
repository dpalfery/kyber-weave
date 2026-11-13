using System.Diagnostics;
using KyberWeave.Core.Processes;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.RunStart;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// <c>KW-ARB-DIFF-001</c> with workers dispatched in parallel into one working tree. A returning worker's
/// since-dispatch diff holds its siblings' files too, so the paths a dispatch in flight during its window declared
/// are subtracted before the rule reads them. What cannot be attributed allows. The evidence is a real repository,
/// the hook's own snapshots, the ledger and a model that would call any stranger's file outside the task.
/// </summary>
public sealed class ArbiterParallelDispatchAttributionTests : IDisposable
{
    private const string RuleId = "KW-ARB-DIFF-001";

    private const string PlanText =
        """
        # Plan

        ## Tasks

        ### T1 — Add the widget model
        - **Files:** `src/Widget.cs`

        ### T2 — Add the widget store
        - **Files:** `src/WidgetStore.cs`

        ### T3 — Wire the widget into startup
        """;

    private readonly TempDirectory _home = new();
    private readonly TempDirectory _repo = new();
    private readonly string _arbiter;

    public ArbiterParallelDispatchAttributionTests()
    {
        _arbiter = Path.Combine(_repo.Path, "artifacts", "arbiter");
        Git("init", "-q", "-b", "main");
        Git("config", "user.email", "arbiter@example.com");
        Git("config", "user.name", "Arbiter");
        File.WriteAllText(Path.Combine(_repo.Path, ".gitignore"), "artifacts/\n");
        Directory.CreateDirectory(Path.Combine(_repo.Path, "src"));
        Directory.CreateDirectory(Path.Combine(_repo.Path, "docs", "plans"));
        File.WriteAllText(Path.Combine(_repo.Path, "src", "Program.cs"), "class Program {}\n");
        File.WriteAllText(Path.Combine(_repo.Path, "docs", "plans", "widget.md"), PlanText);
        Git("add", ".");
        Git("commit", "-q", "-m", "initial");

        // A run branched in place, so the writer dispatches are not refused for want of one (KW-ARB-RUN-001).
        Git("switch", "-q", "-c", "claude/feat/widget");
        RunBaseState.Write(
            _home.Path,
            "claude/feat/widget",
            "main",
            new RunStartPlace(
                GitFacts.GetCommonDirectory(_repo.Path) ?? throw new InvalidOperationException("no git dir"),
                _repo.Path,
                DateTimeOffset.UtcNow));
    }

    public void Dispose()
    {
        _home.Dispose();
        _repo.Dispose();
    }

    [Fact]
    public async Task ASiblingsDeclaredFile_IsNotBlamedOnTheWorkerThatReturnsFirst()
    {
        ArbiterEvaluator evaluator = Evaluator();
        await Dispatch(evaluator, "pre", "T1", "call-1");
        await Dispatch(evaluator, "pre", "T2", "call-2");
        Write("src/Widget.cs");
        Write("src/WidgetStore.cs");

        ArbiterEvaluationResult result = await Dispatch(evaluator, "post", "T1", "call-1");

        Assert.Equal(RuleEffects.Allow, result.Outcome);
        Assert.Equal("inside-task", LastAnswer());
    }

    [Fact]
    public async Task ASiblingThatReturnedDuringTheWindow_StillCounts()
    {
        ArbiterEvaluator evaluator = Evaluator();
        await Dispatch(evaluator, "pre", "T1", "call-1");
        await Dispatch(evaluator, "pre", "T2", "call-2");
        Write("src/WidgetStore.cs");
        await Dispatch(evaluator, "post", "T2", "call-2");
        Write("src/Widget.cs");

        ArbiterEvaluationResult result = await Dispatch(evaluator, "post", "T1", "call-1");

        Assert.Equal(RuleEffects.Allow, result.Outcome);
        Assert.Equal("inside-task", LastAnswer());
    }

    [Fact]
    public async Task ASiblingWithNoFileList_LeavesAStrangerUnattributed()
    {
        ArbiterEvaluator evaluator = Evaluator();
        await Dispatch(evaluator, "pre", "T1", "call-1");
        await Dispatch(evaluator, "pre", "T3", "call-3");
        Write("src/Widget.cs");
        Write("src/Startup.cs");

        ArbiterEvaluationResult result = await Dispatch(evaluator, "post", "T1", "call-1");

        Assert.Equal(RuleEffects.Allow, result.Outcome);
        Assert.Equal("unattributed", LastAnswer());
    }

    [Fact]
    public async Task AWorkerAloneThatWroteOutsideItsTask_StillEscalates()
    {
        ArbiterEvaluator evaluator = Evaluator();
        await Dispatch(evaluator, "pre", "T1", "call-1");
        Write("src/Widget.cs");
        Write("src/Billing.cs");

        ArbiterEvaluationResult result = await Dispatch(evaluator, "post", "T1", "call-1");

        Assert.Equal(RuleEffects.Escalate, result.Outcome);
        Assert.Equal("outside-task", LastAnswer());
    }

    [Fact]
    public async Task ASiblingDispatchedAfterTheReturn_IsNotSubtracted()
    {
        ArbiterEvaluator evaluator = Evaluator();
        await Dispatch(evaluator, "pre", "T1", "call-1");
        Write("src/Widget.cs");
        Write("src/WidgetStore.cs");
        ArbiterEvaluationResult result = await Dispatch(evaluator, "post", "T1", "call-1");
        await Dispatch(evaluator, "pre", "T2", "call-2");

        Assert.Equal(RuleEffects.Escalate, result.Outcome);
        Assert.Equal("outside-task", LastAnswer());
    }

    private ArbiterEvaluator Evaluator() => new(
        new OutsideTaskProvider(),
        _ => null,
        new InFlightLedger(_arbiter),
        new DecisionLog(_arbiter),
        new HookGitFacts(_repo.Path, _home.Path, _arbiter),
        new RealPlanReader(_repo.Path),
        TimeProvider.System);

    private Task<ArbiterEvaluationResult> Dispatch(ArbiterEvaluator evaluator, string phase, string task, string callId) =>
        evaluator.EvaluateAsync(
            new ArbiterEvent
            {
                Harness = "claude",
                Phase = phase,
                Target = "csharp-dev",
                Prompt = $"KYBER-ARBITER: true\nPLAN_FILE: docs/plans/widget.md\nTASK: {task}\n\nDo the task.",
                HarnessCaller = "conductor",
                Session = "s1",
                ToolCallId = callId,
                Cwd = _repo.Path,
                IsDispatch = true,
                ToolOutput = phase == "post" ? "Done." : null,
            },
            ArbiterEvaluatorFixtures.ConfigWith(ArbiterConfig.ProductDefaults.Rules),
            null,
            TestContext.Current.CancellationToken);

    private string LastAnswer() =>
        new DecisionLog(_arbiter).ReadAll()
            .Where(record => record.Trigger == "delegate.returned")
            .SelectMany(record => record.Rules)
            .Last(rule => rule.Id == RuleId)
            .Answer;

    private void Write(string relativePath) =>
        File.WriteAllText(Path.Combine(_repo.Path, relativePath), $"// {relativePath}\n");

    private void Git(params string[] arguments)
    {
        ProcessStartInfo startInfo = new("git")
        {
            WorkingDirectory = _repo.Path,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);

        ProcessResult result = ProcessRunner.Run(startInfo, string.Empty, TimeSpan.FromSeconds(60));
        Assert.True(result.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {result.StandardError}");
    }

    private sealed class RealPlanReader(string root) : IArbiterPlanReader
    {
        public ArbiterFactSet Enrich(ArbiterFactSet facts, TriggerClassification classification) =>
            Core.Arbiter.Plans.PlanFacts.Enrich(facts, classification, root);
    }

    /// <summary>The live misfire: the model confidently calls any file the task does not name outside it.</summary>
    private sealed class OutsideTaskProvider : IArbiterProvider
    {
        public Task<ArbiterStep1BatchResult> AskStep1Async(
            IReadOnlyList<ArbiterRule> rules,
            ArbiterFactSet facts,
            IReadOnlyDictionary<string, string>? step0Answers = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ArbiterStep1BatchResult(
                rules.ToDictionary(
                    rule => rule.Id,
                    rule => rule.Id == RuleId ? "outside-task" : ArbiterStep1BatchResult.NotEvaluated,
                    StringComparer.Ordinal),
                "jev-1.14.0",
                null,
                true,
                rules.ToDictionary(
                    rule => rule.Id,
                    rule => new ArbiterStep1Score(null, 0.96, rule.Ask?.ConfidenceAtLeast),
                    StringComparer.Ordinal)));
    }
}
