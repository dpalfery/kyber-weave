using System.Text.Json;
using KyberWeave.Arbiter;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Plans;
using KyberWeave.Core.Arbiter.Providers;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// A conductor run writes its plan in a git worktree of the repository, untracked, while some harnesses run the hook
/// in the main checkout. The plan is then found in the worktree that holds it, by machine facts: the file on disk and
/// the repository's own worktree list.
/// </summary>
public sealed class ArbiterPlanWorktreeTests
{
    private const string PlanRelative = "docs/plans/2026-11-12-job-status-cards.md";

    private static string Prompt(string body = "Write the failing tests.") =>
        $"KYBER-ARBITER: true\nPLAN_FILE: {PlanRelative}\nTASK: T7\n\n{body}";

    private static void WritePlan(string root)
    {
        string path = Path.Combine(root, PlanRelative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "# Plan\n\n## Tasks\n\n### T7 — Status cards tests\nFiles: tests/StatusCardsTests.cs\n");
    }

    private static string AddWorktree(RunStartProbe probe, string name)
    {
        string path = Path.Combine(Path.GetDirectoryName(probe.Root)!, name);
        probe.Git("worktree", "add", "-b", name, path, "origin/main");
        return path;
    }

    private static bool HoldsPlan(string root) => File.Exists(Path.Combine(root, PlanRelative));

    [Fact]
    public void APlanThatExistsOnlyInARunWorktree_IsReadFromThatWorktree()
    {
        using RunStartProbe probe = RunStartProbe.Create();
        string worktree = AddWorktree(probe, "run-wt");
        WritePlan(worktree);

        string root = PlanRootResolver.Resolve(probe.Root, Prompt());

        Assert.True(HoldsPlan(root), $"resolved '{root}', which does not hold the plan");
        Assert.NotEqual(Path.GetFullPath(probe.Root), Path.GetFullPath(root));
    }

    [Fact]
    public void APlanInTheHookCheckout_KeepsTheHookCheckout()
    {
        using RunStartProbe probe = RunStartProbe.Create();
        string worktree = AddWorktree(probe, "run-wt");
        WritePlan(worktree);
        WritePlan(probe.Root);

        Assert.Equal(probe.Root, PlanRootResolver.Resolve(probe.Root, Prompt()));
    }

    [Fact]
    public void APlanNoWorktreeHolds_KeepsTheHookCheckout()
    {
        using RunStartProbe probe = RunStartProbe.Create();
        _ = AddWorktree(probe, "run-wt");

        Assert.Equal(probe.Root, PlanRootResolver.Resolve(probe.Root, Prompt()));
    }

    [Fact]
    public void SeveralWorktreesHoldingThePlan_PreferTheOneTheDispatchNames()
    {
        using RunStartProbe probe = RunStartProbe.Create();
        string first = AddWorktree(probe, "run-a");
        string second = AddWorktree(probe, "run-b");
        WritePlan(first);
        WritePlan(second);
        File.SetLastWriteTimeUtc(Path.Combine(first, PlanRelative), DateTime.UtcNow.AddHours(1));

        string named = PlanRootResolver.Resolve(probe.Root, Prompt($"Worktree: {second}\n\nWrite the tests."));
        string newest = PlanRootResolver.Resolve(probe.Root, Prompt());

        Assert.EndsWith("run-b", Path.TrimEndingDirectorySeparator(named), StringComparison.Ordinal);
        Assert.EndsWith("run-a", Path.TrimEndingDirectorySeparator(newest), StringComparison.Ordinal);
    }

    [Fact]
    public void AReturnWhoseHeadersWereStripped_FollowsTheWorktreeItsBodyNames()
    {
        using RunStartProbe probe = RunStartProbe.Create();
        string worktree = AddWorktree(probe, "run-wt");

        string root = PlanRootResolver.Resolve(probe.Root, $"Worktree: {worktree}\n\nWrite the tests.");

        Assert.EndsWith("run-wt", Path.TrimEndingDirectorySeparator(root), StringComparison.Ordinal);
    }

    /// <summary>
    /// The live failure: a well-formed dispatch from a hook running in the main checkout, for a plan only the run
    /// worktree holds, was refused by PLAN-001, SCOPE-001 and OWNER-001 at once. The plan is read from the worktree.
    /// </summary>
    [Fact]
    public void TheHookInTheMainCheckout_DecidesFromThePlanInTheRunWorktree()
    {
        using RunStartProbe probe = RunStartProbe.Create();
        using TempDirectory home = new();
        string worktree = AddWorktree(probe, "run-wt");
        WritePlan(worktree);
        Directory.CreateDirectory(Path.Combine(probe.Root, ".kyber-weave"));
        File.WriteAllText(
            Path.Combine(probe.Root, ".kyber-weave", "kyber-weave.yml"),
            "ontology:\n  docs-root: docs\narbiter:\n  enabled: true\n  provider:\n    kind: none\n");

        ArbiterHookDecisionEngine engine = new(providerFactory: _ => new NoneProvider(), homeDirectory: () => home.Path);
        HookCommand command = new(HarnessAdapterRegistry.CreateDefault(engine), Composition.LoadHostConfig, () => "wt-1");
        string toolInput = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["description"] = "T7",
            ["prompt"] = Prompt($"Worktree: {worktree}\n\nWrite the failing tests."),
            ["subagent_type"] = "test-dev",
        });
        string stdin = "{\"hook_event_name\":\"PreToolUse\",\"session_id\":\"s-wt\",\"cwd\":" +
            JsonSerializer.Serialize(probe.Root) + ",\"agent_type\":\"conductor\",\"tool_name\":\"Agent\"," +
            "\"tool_input\":" + toolInput + ",\"tool_use_id\":\"toolu-wt-1\"}";
        using StringWriter stdout = new();
        using StringWriter log = new();

        int exit = command.Run("claude", "conductor", stdin, stdout, log);

        Assert.Equal(0, exit);
        Assert.DoesNotContain("\"deny\"", stdout.ToString(), StringComparison.Ordinal);
        ArbiterDecisionRecord record = new DecisionLog(Path.Combine(probe.Root, "artifacts", "arbiter"))
            .ReadAll()
            .First(decision => decision.Trigger == "delegate");
        Assert.Contains(record.Rules, rule => rule.Id == "KW-ARB-PLAN-001" && rule.Answer == "has-tasks");
        Assert.DoesNotContain(record.Rules, rule => rule.Effect == "escalate");
    }
}
