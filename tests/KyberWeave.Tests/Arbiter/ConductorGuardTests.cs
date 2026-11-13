using System.Diagnostics;
using System.Text.Json;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Core.Processes;
using KyberWeave.Core.Squad.Rendering;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>Pins the Copilot conductor guard: what it denies, permits, and restates.</summary>
public sealed class ConductorGuardTests
{
    private const string Cwd = "/repo";
    private const string CopilotHome = "/home/me/.copilot";

    [Theory]
    [InlineData("edit")]
    [InlineData("create")]
    [InlineData("apply_patch")]
    [InlineData("rg")]
    [InlineData("web_fetch")]
    [InlineData("codegraph-codegraph_explore")]
    [InlineData("evil-arbiter_evaluate_agent_dispatch_or_return")]
    public void ProjectWorkTools_AreDenied(string tool)
    {
        Assert.Contains(tool, ReasonFor(Event(tool, new { path = "src/a.cs" })), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("task")]
    [InlineData("ask_user")]
    [InlineData("task_complete")]
    [InlineData("rename_chat")]
    [InlineData("update_todo")]
    [InlineData("tool_search_tool")]
    [InlineData("kyber-weave-docs_answer_question_from_repo_documentation")]
    [InlineData("kw-docs-docs_look_up_glossary_term_definition")]
    [InlineData("arbiter_evaluate_agent_dispatch_or_return")]
    [InlineData("arbiter_select_relevant_document_sections")]
    [InlineData("arbiter_classify_bot_review_comment")]
    [InlineData("kw-arbiter-arbiter_select_relevant_document_sections")]
    [InlineData("kw-arbiter-arbiter_classify_bot_review_comment")]
    public void DelegationAndBookkeepingTools_ArePermittedSilently(string tool)
    {
        Assert.Empty(Run(Event(tool, new { })));
    }

    [Theory]
    [InlineData("kyber-weave run start --harness copilot --type fix --slug a-b")]
    [InlineData("kyber-weave run start --harness copilot --type fix --slug a --failing-version 1.2.3 --resume copilot/fix/a --json")]
    [InlineData("kyber-weave doctor --ensure")]
    [InlineData("kyber-weave doctor --ensure /repo")]
    public void RunStartCommands_ArePermitted(string command)
    {
        Assert.Empty(Run(Event("bash", new { command })));
    }

    [Theory]
    [InlineData("git status")]
    [InlineData("kyber-weave run start")]
    [InlineData("kyber-weave run start --evil-flag x")]
    [InlineData("kyber-weave doctor --ensure --evil-flag")]
    [InlineData("kyber-weave doctor --ensure; rm -rf /")]
    [InlineData("kyber-weave run start --slug a && cat secrets")]
    [InlineData("kyber-weave run start --slug $(id)")]
    [InlineData("kyber-weave run start --slug `id`")]
    [InlineData("kyber-weave run start --slug a | tee x")]
    [InlineData("kyber-weave run start --slug a\nrm x")]
    public void OtherCommands_AreDenied(string command)
    {
        Assert.NotEmpty(ReasonFor(Event("bash", new { command })));
    }

    [Theory]
    [InlineData("/home/me/.copilot/agents/conductor/references/plan-path.md")]
    [InlineData(".github/agents/conductor/references/plan-path.md")]
    [InlineData("AGENTS.md")]
    [InlineData("/repo/6-Docs/plans/README.md")]
    [InlineData("docs/specs/feature/tasks.md")]
    [InlineData("docs/todo/item.md")]
    [InlineData("docs/plans/2026-11-12-widget.tasks.json")]
    [InlineData("/home/me/.copilot/skills/delivery-execution/SKILL.md")]
    [InlineData("/home/me/.copilot/skills/delivery-execution/references/gate.md")]
    [InlineData(".github/skills/delivery-execution/SKILL.md")]
    public void ContractFiles_ArePermitted(string path)
    {
        Assert.Empty(Run(Event("view", new { path })));
    }

    [Theory]
    [InlineData("src/App.cs")]
    [InlineData("README.md")]
    [InlineData("docs/plans/../../src/notes.md")]
    [InlineData("/elsewhere/plans/x.md")]
    [InlineData("/home/me/.copilot/config.json")]
    [InlineData("src/skills/x.cs")]
    [InlineData("docs/plans/code.cs")]
    [InlineData("web/specs/login.spec.ts")]
    [InlineData("evil/agents/conductor/secret.md")]
    [InlineData("/tmp/agents/conductor/secret.md")]
    public void OtherFiles_AreDenied(string path)
    {
        Assert.NotEmpty(ReasonFor(Event("view", new { path })));
    }

    [Theory]
    [InlineData("edit")]
    [InlineData("create")]
    [InlineData("apply_patch")]
    public void WritingAPlanningFile_IsStillDenied(string tool)
    {
        Assert.Contains(tool, ReasonFor(Event(tool, new { path = "docs/plans/2026-11-12-widget.tasks.json" })), StringComparison.Ordinal);
    }

    [Fact]
    public void AConfiguredPlanningDirectory_IsReadable()
    {
        string repo = Directory.CreateTempSubdirectory("guard-config-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(repo, ".kyber-weave"));
            File.WriteAllText(
                Path.Combine(repo, ".kyber-weave", "kyber-weave.yml"),
                "ontology:\n  docs-root: handbook\nconfig-reg:\n  plan-index: handbook/roadmap/README.md\n");

            Assert.Empty(RunIn(repo, "handbook/roadmap/widget.tasks.json"));
            Assert.Contains("deny", RunIn(repo, "handbook/guide.md"), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public void ArgumentsSentAsAJsonString_AreRead()
    {
        string json = JsonSerializer.Serialize(new
        {
            cwd = Cwd,
            toolName = "bash",
            toolArgs = JsonSerializer.Serialize(new { command = "git status" }),
        });

        Assert.NotEmpty(ReasonFor(json));
    }

    [Fact]
    public void ABatchWithOneBlockedTool_IsDenied()
    {
        string json = JsonSerializer.Serialize(new
        {
            cwd = Cwd,
            toolCalls = new object[]
            {
                new { name = "task", args = new { } },
                new { name = "edit", args = new { path = "a.cs" } },
            },
        });

        Assert.Contains("edit", ReasonFor(json), StringComparison.Ordinal);
    }

    [Fact]
    public void AWorkerToolCall_IsPermitted()
    {
        string json = JsonSerializer.Serialize(new { cwd = Cwd, agentId = "a1", parentAgentId = "p1", agentType = "csharp-dev", toolName = "edit", toolArgs = new { } });

        Assert.Empty(Run(json));
    }

    [Theory]
    [InlineData("{\"cwd\":\"/repo\",\"agentId\":\"a1\",\"toolName\":\"edit\",\"toolArgs\":{}}")]
    [InlineData("{\"cwd\":\"/repo\",\"agent_id\":\"a1\",\"toolName\":\"edit\",\"toolArgs\":{}}")]
    [InlineData("{\"cwd\":\"/repo\",\"agentId\":\"a1\",\"parentAgentId\":\"p1\",\"toolName\":\"edit\",\"toolArgs\":{}}")]
    [InlineData("{\"cwd\":\"/repo\",\"agentId\":\"a1\",\"parentAgentId\":\"p1\",\"agentType\":\"conductor\",\"toolName\":\"edit\",\"toolArgs\":{}}")]
    [InlineData("{\"cwd\":\"/repo\",\"toolName\":\"edit\",\"toolArgs\":{\"agentId\":\"a1\",\"parentAgentId\":\"p1\",\"agentType\":\"x\"}}")]
    public void AnIncompleteOrForgedWorkerId_IsDenied(string json)
    {
        Assert.Contains("edit", ReasonFor(json), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"toolName\":\"bash\",\"toolArgs\":\"not json\"}")]
    public void UnreadableEvents_AreDenied(string json)
    {
        Assert.NotEmpty(ReasonFor(json));
    }

    [Fact]
    public void AToolCallCarryingAPromptField_IsStillDenied()
    {
        string json = JsonSerializer.Serialize(new { cwd = Cwd, transformedPrompt = "hi", toolName = "edit", toolArgs = new { } });

        Assert.Contains("edit", ReasonFor(json), StringComparison.Ordinal);
    }

    [Fact]
    public void AnOverlongPath_IsDeniedNotThrown()
    {
        Assert.NotEmpty(ReasonFor(Event("view", new { path = $"docs/plans/{new string('a', 100_000)}\0.md" })));
    }

    [Fact]
    public async Task GuardVerb_DeniesWhenStdinCannotBeRead()
    {
        using StringWriter stdout = new();
        using StringReader closed = new(string.Empty);
        closed.Dispose();

        int exit = await KyberWeave.Arbiter.Composition.DispatchAsync(["guard"], closed, stdout, TextWriter.Null);

        Assert.Equal(0, exit);
        Assert.Contains("\"deny\"", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GuardVerb_DeniesWhenStdinAndStderrBothFail()
    {
        using StringWriter stdout = new();
        using StringReader closedIn = new(string.Empty);
        using StringWriter closedErr = new();
        closedIn.Dispose();
        await closedErr.DisposeAsync();

        int exit = await KyberWeave.Arbiter.Composition.DispatchAsync(["guard"], closedIn, stdout, closedErr);

        Assert.Equal(0, exit);
        Assert.Contains("\"deny\"", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ARelativeCopilotHome_GrantsNothing()
    {
        using StringWriter stdout = new();
        ConductorGuard.Run(Event("view", new { path = ".copilot/agents/conductor/x.md" }), stdout, TextWriter.Null, copilotHome: ".copilot");

        Assert.Contains("deny", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void GuardCommandLine_AcceptsADottedServerName()
    {
        Assert.Equal("kyber-weave-arbiter guard --allow-mcp my.server", ArbiterHookWiring.GuardCommandLine("my.server"));
        Assert.Throws<ArgumentException>(() => ArbiterHookWiring.GuardCommandLine("a b"));
    }

    [Fact]
    public void AllowedMcpServer_IsPermittedOnlyWhenNamed()
    {
        using StringWriter stdout = new();
        ConductorGuard.Run(Event("operator-channel-send", new { }), stdout, TextWriter.Null, "operator-channel", CopilotHome);

        Assert.Empty(stdout.ToString());
        Assert.NotEmpty(ReasonFor(Event("operator-channel-send", new { })));
    }

    [Theory]
    [InlineData("kyber-weave run start --harness copilot --type fix --slug ../../evil")]
    [InlineData("kyber-weave run start --harness ../x --type fix --slug a")]
    [InlineData("kyber-weave run start --harness copilot --type fix --slug a --resume ../main")]
    [InlineData("kyber-weave run start --harness copilot --type fix --slug a --resume /etc/x")]
    [InlineData("kyber-weave run start --harness copilot --type fix --slug a --resume .hidden")]
    [InlineData("kyber-weave run start --harness copilot --type fix --slug a --failing-version 1/2")]
    public void RunStartValuesThatTraverse_AreDenied(string command)
    {
        Assert.NotEmpty(ReasonFor(Event("bash", new { command })));
    }

    [Fact]
    public void AContractPathThroughASymlink_IsDenied()
    {
        string root = Directory.CreateTempSubdirectory("guard-link-").FullName;
        try
        {
            string outside = Directory.CreateDirectory(Path.Combine(root, "outside")).FullName;
            File.WriteAllText(Path.Combine(outside, "secret.md"), "x");
            string repo = Directory.CreateDirectory(Path.Combine(root, "repo")).FullName;
            Directory.CreateDirectory(Path.Combine(repo, "docs", "plans"));
            Directory.CreateSymbolicLink(Path.Combine(repo, "docs", "plans", "link"), outside);
            File.WriteAllText(Path.Combine(repo, "docs", "plans", "real.md"), "x");

            Assert.Contains("deny", RunIn(repo, "docs/plans/link/secret.md"), StringComparison.Ordinal);
            Assert.Empty(RunIn(repo, "docs/plans/real.md"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AFailingStdout_StillReturnsZero()
    {
        using StringWriter closed = new();
        closed.Dispose();

        Assert.Equal(0, ConductorGuard.Run(Event("edit", new { }), closed, TextWriter.Null, copilotHome: CopilotHome));
    }

    [Fact]
    public void TheFailureLog_IsOneLine()
    {
        using StringWriter stdout = new();
        using StringWriter log = new();
        ConductorGuard.Run("{\"toolName\":\"bash\",\"toolArgs\":\"not\\njson\"}", stdout, log, copilotHome: CopilotHome);

        Assert.Single(log.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void ModelFacingPrompt_GetsTheWorkflowAppended()
    {
        string json = JsonSerializer.Serialize(new { cwd = Cwd, transformedPrompt = "Complete the original task." });

        using JsonDocument answer = JsonDocument.Parse(Run(json));
        string modified = answer.RootElement.GetProperty("modifiedTransformedPrompt").GetString()!;

        Assert.StartsWith("Complete the original task.", modified, StringComparison.Ordinal);
        Assert.EndsWith(ConductorGuard.WorkflowReminder, modified, StringComparison.Ordinal);
    }

    [Fact]
    public void APlanInTheRunsOwnWorktree_IsPermittedFromTheCanonicalCheckout()
    {
        using TempDirectory parent = new();
        string repo = Path.Combine(parent.Path, "repo");
        string other = Path.Combine(parent.Path, "other");
        Directory.CreateDirectory(repo);
        Directory.CreateDirectory(Path.Combine(other, "docs", "plans"));
        Git(repo, "init", "-q", "-b", "main");
        Git(repo, "-c", "user.email=a@example.com", "-c", "user.name=A", "commit", "-q", "--allow-empty", "-m", "initial");
        string worktree = Path.Combine(parent.Path, "repo.worktrees", "run");
        Git(repo, "worktree", "add", "-q", "-b", "claude/feat/run", worktree);
        string root = GitRoot(worktree);
        Directory.CreateDirectory(Path.Combine(root, "docs", "plans"));

        Assert.Empty(RunIn(repo, Path.Combine(root, "docs", "plans", "2026-11-13-widget.md")));
        Assert.Empty(RunIn(repo, Path.Combine(root, "docs", "plans", "2026-11-13-widget.tasks.json")));
        Assert.NotEmpty(RunIn(repo, Path.Combine(root, "src", "App.cs")));
        Assert.NotEmpty(RunIn(repo, Path.Combine(other, "docs", "plans", "x.md")));
    }

    [Fact]
    public void ARunRecord_IsPermittedAndTheRestOfHomeIsNot()
    {
        using TempDirectory home = new();
        string runs = Path.Combine(home.Path, ".kyber-weave", "runs");
        Directory.CreateDirectory(runs);

        Assert.Empty(RunIn(Cwd, Path.Combine(runs, "claude-feat-run.json"), home.Path));
        Assert.NotEmpty(RunIn(Cwd, Path.Combine(home.Path, ".kyber-weave", "credentials.json"), home.Path));
        Assert.NotEmpty(RunIn(Cwd, Path.Combine(runs, "..", "secrets.json"), home.Path));
    }

    private static string GitRoot(string directory) =>
        KyberWeave.Core.Arbiter.Facts.GitFacts.GetRoot(directory) ?? throw new InvalidOperationException("no git root");

    private static void Git(string directory, params string[] arguments)
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
        foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);

        ProcessResult result = ProcessRunner.Run(startInfo, string.Empty, TimeSpan.FromSeconds(60));
        Assert.True(result.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {result.StandardError}");
    }

    private static string Event(string tool, object arguments) =>
        JsonSerializer.Serialize(new { cwd = Cwd, toolName = tool, toolArgs = arguments });

    private static string Run(string json)
    {
        using StringWriter stdout = new();
        Assert.Equal(0, ConductorGuard.Run(json, stdout, TextWriter.Null, copilotHome: CopilotHome));
        return stdout.ToString();
    }

    private static string RunIn(string cwd, string path, string? home = null)
    {
        using StringWriter stdout = new();
        ConductorGuard.Run(
            JsonSerializer.Serialize(new { cwd, toolName = "view", toolArgs = new { path } }),
            stdout,
            TextWriter.Null,
            copilotHome: CopilotHome,
            homeDirectory: home);
        return stdout.ToString();
    }

    private static string ReasonFor(string json)
    {
        using JsonDocument answer = JsonDocument.Parse(Run(json));
        Assert.Equal("deny", answer.RootElement.GetProperty("permissionDecision").GetString());
        return answer.RootElement.GetProperty("permissionDecisionReason").GetString()!;
    }
}
