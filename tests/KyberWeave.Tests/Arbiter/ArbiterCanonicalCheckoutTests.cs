using System.Diagnostics;
using System.Text.Json;
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
/// <c>KW-ARB-CHECKOUT-001</c>: while a run works in a worktree of its own, a Squad subagent's write into the
/// repository's canonical checkout is refused where the harness hooks the write (the worker guard), and found on
/// return everywhere else (the canonical checkout's diff since dispatch). The evidence is a real repository, a
/// linked worktree and the run record <c>run start</c> writes.
/// </summary>
public sealed class ArbiterCanonicalCheckoutTests : IDisposable
{
    private const string RuleId = "KW-ARB-CHECKOUT-001";
    private const string Branch = "copilot/feat/add-widget";

    private readonly TempDirectory _home = new();
    private readonly TempDirectory _parent = new();
    private readonly string _repo;
    private readonly string _worktree;

    public ArbiterCanonicalCheckoutTests()
    {
        _repo = Path.Combine(_parent.Path, "repo");
        _worktree = Path.Combine(_parent.Path, "repo.worktrees", "add-widget");
        Directory.CreateDirectory(_repo);
        Git(_repo, "init", "-q", "-b", "main");
        Git(_repo, "config", "user.email", "arbiter@example.com");
        Git(_repo, "config", "user.name", "Arbiter");
        Directory.CreateDirectory(Path.Combine(_repo, "src"));
        File.WriteAllText(Path.Combine(_repo, "src", "Job.cs"), "class Job {}\n");
        Git(_repo, "add", ".");
        Git(_repo, "commit", "-q", "-m", "initial");
    }

    public void Dispose()
    {
        _home.Dispose();
        _parent.Dispose();
    }

    [Fact]
    public void Refusal_NamesTheRunWorktreeForAWriteInTheCanonicalCheckout()
    {
        StartInWorktree();

        string? refusal = RunCheckout.StrayWriteRefusal(Path.Combine(_repo, "src", "Job.cs"), _repo, _home.Path);

        Assert.NotNull(refusal);
        Assert.StartsWith(RuleId, refusal, StringComparison.Ordinal);
        Assert.Contains(Path.Combine("add-widget", "src", "Job.cs"), refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void Refusal_ResolvesARelativePathAgainstTheWorkingDirectory()
    {
        StartInWorktree();

        Assert.NotNull(RunCheckout.StrayWriteRefusal("src/New.cs", _repo, _home.Path));
        Assert.Null(RunCheckout.StrayWriteRefusal("src/New.cs", _worktree, _home.Path));
    }

    [Fact]
    public void Refusal_AllowsTheRunWorktreeAndPathsOutsideTheRepository()
    {
        StartInWorktree();

        Assert.Null(RunCheckout.StrayWriteRefusal(Path.Combine(_worktree, "src", "Job.cs"), _repo, _home.Path));
        Assert.Null(RunCheckout.StrayWriteRefusal(Path.Combine(_parent.Path, "scratch.txt"), _repo, _home.Path));
        Assert.Null(RunCheckout.StrayWriteRefusal(Path.Combine(_home.Path, ".config", "x.yml"), _repo, _home.Path));
    }

    [Fact]
    public void Refusal_AllowsWhenNoRunIsLive()
    {
        Assert.Null(RunCheckout.StrayWriteRefusal(Path.Combine(_repo, "src", "Job.cs"), _repo, _home.Path));

        StartInWorktree();
        Git(_worktree, "switch", "-q", "--detach");

        Assert.Null(RunCheckout.StrayWriteRefusal(Path.Combine(_repo, "src", "Job.cs"), _repo, _home.Path));
    }

    [Fact]
    public void Refusal_AllowsARunBranchedInPlace()
    {
        Git(_repo, "switch", "-q", "-c", Branch);
        Record(_repo);

        Assert.Null(RunCheckout.StrayWriteRefusal(Path.Combine(_repo, "src", "Job.cs"), _repo, _home.Path));
    }

    [Fact]
    public void Refusal_AllowsAWorktreeNestedInsideTheCanonicalCheckout()
    {
        string nested = Path.Combine(_repo, ".worktrees", "add-widget");
        Git(_repo, "worktree", "add", "-q", "-b", Branch, nested);
        Record(nested);

        Assert.Null(RunCheckout.StrayWriteRefusal(Path.Combine(nested, "src", "Job.cs"), _repo, _home.Path));
        Assert.NotNull(RunCheckout.StrayWriteRefusal(Path.Combine(_repo, "src", "Job.cs"), _repo, _home.Path));
    }

    [Fact]
    public void CopilotCliGuard_DeniesASubagentEditInTheCanonicalCheckout()
    {
        StartInWorktree();

        string answer = Guard("copilot-cli", CopilotEdit("dal-dev", Path.Combine(_repo, "src", "Job.cs"), subagent: true));

        using JsonDocument document = JsonDocument.Parse(answer);
        Assert.Equal("deny", document.RootElement.GetProperty("permissionDecision").GetString());
        Assert.Contains(RuleId, document.RootElement.GetProperty("permissionDecisionReason").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void CopilotCliGuard_AllowsARootCallAndAnAgentOutsideTheSquad()
    {
        StartInWorktree();
        string target = Path.Combine(_repo, "src", "Job.cs");

        Assert.Empty(Guard("copilot-cli", CopilotEdit("dal-dev", target, subagent: false)));
        Assert.Empty(Guard("copilot-cli", CopilotEdit("general-purpose", target, subagent: true)));
        Assert.Empty(Guard("copilot-cli", CopilotEdit("dal-dev", Path.Combine(_worktree, "src", "Job.cs"), subagent: true)));
    }

    [Fact]
    public void ClaudeGuard_DeniesASubagentWriteInTheCanonicalCheckout()
    {
        StartInWorktree();
        string json = JsonSerializer.Serialize(new
        {
            cwd = _repo,
            hook_event_name = "PreToolUse",
            agent_id = "a1",
            agent_type = "csharp-dev",
            tool_name = "Write",
            tool_input = new { file_path = Path.Combine(_repo, "src", "Dto.cs"), content = "x" },
        });

        string answer = Guard("claude", json);

        using JsonDocument document = JsonDocument.Parse(answer);
        Assert.Equal("deny", document.RootElement.GetProperty("hookSpecificOutput").GetProperty("permissionDecision").GetString());
    }

    [Fact]
    public void Guard_AllowsAnUnreadableEvent()
    {
        Assert.Empty(Guard("copilot-cli", "not json"));
    }

    [Fact]
    public void Return_ListsThePathsADispatchLeftInTheCanonicalCheckout()
    {
        StartInWorktree();
        HookGitFacts facts = new(_repo, _home.Path);
        Enrich(facts, "pre");

        File.WriteAllText(Path.Combine(_repo, "src", "Job.cs"), "class Job { int Id; }\n");
        File.WriteAllText(Path.Combine(_repo, "src", "JobDto.cs"), "class JobDto {}\n");
        File.WriteAllText(Path.Combine(_worktree, "src", "Job.cs"), "class Job { int Id; }\n");

        ArbiterFactSet returned = Enrich(facts, "post");

        Assert.True(returned.TryGet("checkout.stray-paths", out ArbiterFact? stray));
        Assert.Equal(["src/Job.cs", "src/JobDto.cs"], Assert.IsAssignableFrom<IEnumerable<string>>(stray.Value));
        Assert.Equal("stray", RuleEngine.Decide(Rule(), returned));
        Assert.Equal("escalate", Rule().Effects["stray"]);
    }

    [Fact]
    public void Return_IsCleanWhenTheDispatchWroteOnlyInTheRunWorktree()
    {
        StartInWorktree();
        File.WriteAllText(Path.Combine(_repo, "notes.txt"), "a person's edit before the dispatch\n");
        HookGitFacts facts = new(_repo, _home.Path);
        Enrich(facts, "pre");

        File.WriteAllText(Path.Combine(_worktree, "src", "Job.cs"), "class Job { int Id; }\n");

        ArbiterFactSet returned = Enrich(facts, "post");

        Assert.Equal("clean", RuleEngine.Decide(Rule(), returned));
    }

    [Fact]
    public void Return_LeavesTheFactAbsentWithNoRunInAWorktreeOfItsOwn()
    {
        HookGitFacts facts = new(_repo, _home.Path);
        Enrich(facts, "pre");
        File.WriteAllText(Path.Combine(_repo, "src", "Job.cs"), "class Job { int Id; }\n");

        ArbiterFactSet returned = Enrich(facts, "post");

        Assert.False(returned.TryGet("checkout.stray-paths", out ArbiterFact? stray) && stray.Value is not null);
        Assert.Equal(RuleEngine.Undecidable, RuleEngine.Decide(Rule(), returned));
    }

    [Fact]
    public void Return_ACleanUpThatRestoresTheStrayFilesIsClean()
    {
        StartInWorktree();
        File.WriteAllText(Path.Combine(_repo, "src", "Job.cs"), "class Job { int Stray; }\n");
        HookGitFacts facts = new(_repo, _home.Path);
        Enrich(facts, "pre");

        Git(_repo, "restore", "src/Job.cs");

        ArbiterFactSet returned = Enrich(facts, "post");

        Assert.Equal("clean", RuleEngine.Decide(Rule(), returned));
    }

    [Fact]
    public void Return_ACopilotRootConductorsUnnamedDispatchIsCheckedToo()
    {
        StartInWorktree();
        HookGitFacts facts = new(_repo, _home.Path);
        ArbiterFactSet pre = Enrich(facts, "pre", harness: "copilot-cli", caller: null);
        Assert.True(pre.TryGet("delegation.target-profile", out _));

        File.WriteAllText(Path.Combine(_repo, "src", "JobDto.cs"), "class JobDto {}\n");

        ArbiterEvent ev = Event("post", "copilot-cli", caller: null);
        TriggerClassification classification = TriggerClassifier.ClassifySingle(ev, ev.Target, ev.Prompt);
        Assert.Equal("investigate.returned", classification.Trigger);
        ArbiterFactSet returned = facts.Enrich(
            TriggerFactBuilder.Build(classification, ev.Prompt, KyberWeaveConfig.ProductDefaults), classification, ev);

        Assert.Equal("stray", RuleEngine.Decide(Rule(), returned));
        Assert.Contains(
            ArbiterConfig.ProductDefaults.Rules,
            rule => rule.Id == RuleId && rule.Trigger == "investigate.returned");
    }

    [Fact]
    public async Task Evaluator_TheEscalationEvidenceListsTheStrayPaths()
    {
        StartInWorktree();
        string arbiterDirectory = Path.Combine(_parent.Path, "arbiter");
        ArbiterEvaluator evaluator = new(
            new NoneProvider(),
            _ => null,
            new InFlightLedger(arbiterDirectory),
            new DecisionLog(arbiterDirectory),
            new HookGitFacts(_repo, _home.Path, arbiterDirectory),
            new ArbiterEvaluatorFixtures.PlanFacts(),
            TimeProvider.System);
        KyberWeaveConfig config = ArbiterEvaluatorFixtures.ConfigWith(ArbiterConfig.ProductDefaults.Rules);

        await evaluator.EvaluateAsync(Event("pre", "claude", "conductor"), config, null, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(_repo, "src", "JobDto.cs"), "class JobDto {}\n", TestContext.Current.CancellationToken);
        ArbiterEvaluationResult result = await evaluator.EvaluateAsync(
            Event("post", "claude", "conductor") with { ToolOutput = "Done." }, config, null, TestContext.Current.CancellationToken);

        Assert.Equal(RuleEffects.Escalate, result.Outcome);
        ArbiterDecisionRule rule = new DecisionLog(arbiterDirectory).ReadAll()
            .SelectMany(record => record.Rules)
            .Single(candidate => candidate.Id == RuleId);
        Assert.Equal("stray", rule.Answer);
        Assert.Contains("src/JobDto.cs", rule.Evidence, StringComparison.Ordinal);
        Assert.Contains("run worktree", rule.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void Guard_AllowsTheWriteWhenTheProjectTurnsTheArbiterOff()
    {
        StartInWorktree();
        Directory.CreateDirectory(Path.Combine(_repo, ".kyber-weave"));
        File.WriteAllText(Path.Combine(_repo, ".kyber-weave", "kyber-weave.yml"), "arbiter:\n  enabled: false\n");

        Assert.Empty(Guard("copilot-cli", CopilotEdit("dal-dev", Path.Combine(_repo, "src", "Job.cs"), subagent: true)));
    }

    [Fact]
    public void EscalationNext_AsksForTheCleanUpNotTheArchitect()
    {
        string next = ArbiterEscalationEnvelope.NextFor(
            "delegate.returned",
            "conductor",
            1,
            [new ArbiterEnvelopeEntry(RuleId, "q", new ArbiterAnswerText("stray (step 0)"), "e")]);

        Assert.Contains("run worktree", next, StringComparison.Ordinal);
        Assert.Contains("do not dispatch architect", next, StringComparison.Ordinal);
    }

    private ArbiterEvent Event(string phase, string harness, string? caller) => new()
    {
        Harness = harness,
        Phase = phase,
        Target = "dal-dev",
        Prompt = "Add the job endpoint.",
        HarnessCaller = caller,
        ToolCallId = "call-1",
        Cwd = _repo,
        IsDispatch = true,
    };

    private ArbiterFactSet Enrich(HookGitFacts git, string phase, string harness = "claude", string? caller = "conductor")
    {
        ArbiterEvent ev = Event(phase, harness, caller);
        TriggerClassification classification = TriggerClassifier.ClassifySingle(ev, ev.Target, ev.Prompt);
        ArbiterFactSet facts = TriggerFactBuilder.Build(classification, ev.Prompt, KyberWeaveConfig.ProductDefaults);
        return git.Enrich(facts, classification, ev);
    }

    private static ArbiterRule Rule() =>
        ArbiterConfig.ProductDefaults.Rules.First(rule => string.Equals(rule.Id, RuleId, StringComparison.Ordinal));

    private string CopilotEdit(string agentType, string path, bool subagent) => subagent
        ? JsonSerializer.Serialize(new
        {
            cwd = _repo,
            agentId = "agent-7",
            parentAgentId = "root",
            agentType,
            toolName = "edit",
            toolArgs = JsonSerializer.Serialize(new { path, old_str = "a", new_str = "b" }),
        })
        : JsonSerializer.Serialize(new
        {
            cwd = _repo,
            toolName = "edit",
            toolArgs = JsonSerializer.Serialize(new { path, old_str = "a", new_str = "b" }),
        });

    private string Guard(string harness, string json)
    {
        using StringWriter stdout = new();
        WorkerGuard.Run(harness, json, stdout, TextWriter.Null, _home.Path);
        return stdout.ToString();
    }

    private void StartInWorktree()
    {
        Git(_repo, "worktree", "add", "-q", "-b", Branch, _worktree);
        Record(_worktree);
    }

    private void Record(string worktree) =>
        RunBaseState.Write(
            _home.Path,
            Branch,
            "origin/main",
            new RunStartPlace(
                GitFacts.GetCommonDirectory(_repo) ?? throw new InvalidOperationException("no git dir"),
                worktree,
                DateTimeOffset.UtcNow));

    private static void Git(string workingDirectory, params string[] args)
    {
        using Process process = new();
        process.StartInfo.FileName = "git";
        process.StartInfo.WorkingDirectory = workingDirectory;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        foreach (string arg in args)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        process.Start();
        string error = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {error}");
        }
    }
}
