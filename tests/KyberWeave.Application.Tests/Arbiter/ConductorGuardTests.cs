using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Tests.Arbiter;
using KyberWeave.Data.Arbiter;
using KyberWeave.Core.Arbiter.Plans;
using KyberWeave.Core.Arbiter.Facts;
using System.Text.Json;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Core.Squad.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KyberWeave.Application.Tests.Arbiter;

/// <summary>Pins the Copilot conductor guard: what it denies, permits, and restates.</summary>
public sealed class ConductorGuardTests : IDisposable
{
    private const string Cwd = "/repo";
    private const string CopilotHome = "/home/me/.copilot";

    private readonly InMemoryArbiterHost _host;

    public ConductorGuardTests() =>
        _host = InMemoryArbiterHost.Create(services => services.AddSingleton<IFileLinkProbe>(new NoLinks()));

    public void Dispose() => _host.Dispose();

    private IConductorGuard Guard => _host.Get<IConductorGuard>();

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
    [InlineData("kyber-weave run start --harness copilot --type feat --slug a --brief --path plan --autonomy autonomous")]
    [InlineData("kyber-weave squad brief conductor --path intake --autonomy supervised")]
    [InlineData("kyber-weave squad brief architect --path finalize --plan docs/plans/2026-11-13-x.md --mode test-first")]
    [InlineData("kyber-weave docs tasks docs/plans/2026-11-13-x.md --list")]
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
    [InlineData("kyber-weave run start --slug a --brief > out.txt")]
    [InlineData("kyber-weave run start --path elsewhere")]
    [InlineData("kyber-weave squad brief conductor && rm x")]
    [InlineData("kyber-weave squad brief conductor; rm x")]
    [InlineData("kyber-weave squad brief conductor | tee x")]
    [InlineData("kyber-weave squad brief conductor --plan $(id)")]
    [InlineData("kyber-weave squad brief conductor --plan `id`")]
    [InlineData("kyber-weave squad brief conductor --plan ../../etc/passwd")]
    [InlineData("kyber-weave squad brief conductor --from /tmp/evil")]
    [InlineData("kyber-weave squad brief conductor\nrm x")]
    [InlineData("kyber-weave docs tasks docs/plans/x.md --write")]
    [InlineData("kyber-weave docs tasks docs/plans/x.md --check")]
    [InlineData("kyber-weave docs tasks docs/plans/x.md")]
    [InlineData("kyber-weave docs tasks docs/plans/x.md --list 2> err")]
    [InlineData("kyber-weave docs validate .")]
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
    [InlineData("docs/plans/2026-11-12-widget.tasks.yml")]
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
        Assert.Contains(tool, ReasonFor(Event(tool, new { path = "docs/plans/2026-11-12-widget.tasks.yml" })), StringComparison.Ordinal);
    }

    [Fact]
    public void AConfiguredPlanningDirectory_IsReadable()
    {
        string repo = _host.InWork("guard-config");
        _ = _host.WriteRepoConfig("ontology:\n  docs-root: handbook\nconfig-reg:\n  plan-index: handbook/roadmap/README.md\n", repo);

        Assert.Empty(RunIn(repo, "handbook/roadmap/widget.tasks.yml"));
        Assert.Contains("deny", RunIn(repo, "handbook/guide.md"), StringComparison.Ordinal);
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
        string json = JsonSerializer.Serialize(new { cwd = Cwd, agentId = "a1", parentAgentId = "p1", agentType = "dev-medium", toolName = "edit", toolArgs = new { } });

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
        Guard.Run(Event("view", new { path = ".copilot/agents/conductor/x.md" }), stdout, TextWriter.Null, copilotHome: ".copilot");

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
        Guard.Run(Event("operator-channel-send", new { }), stdout, TextWriter.Null, "operator-channel", CopilotHome);

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
    public void AFailingStdout_StillReturnsZero()
    {
        using StringWriter closed = new();
        closed.Dispose();

        Assert.Equal(0, Guard.Run(Event("edit", new { }), closed, TextWriter.Null, copilotHome: CopilotHome));
    }

    [Fact]
    public void TheFailureLog_IsOneLine()
    {
        using StringWriter stdout = new();
        using StringWriter log = new();
        Guard.Run("{\"toolName\":\"bash\",\"toolArgs\":\"not\\njson\"}", stdout, log, copilotHome: CopilotHome);

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
    public void ARunRecord_IsPermittedAndTheRestOfHomeIsNot()
    {
        string home = _host.InHome("runrecord-home");
        string runs = Path.Combine(home, ".kyber-weave", "runs");
        _host.Files.CreateDirectory(runs);

        Assert.Empty(RunIn(Cwd, Path.Combine(runs, "claude-feat-run.json"), home));
        Assert.NotEmpty(RunIn(Cwd, Path.Combine(home, ".kyber-weave", "credentials.json"), home));
        Assert.NotEmpty(RunIn(Cwd, Path.Combine(runs, "..", "secrets.json"), home));
    }

    private sealed class NoLinks : IFileLinkProbe
    {
        public bool HasLinkBelow(string root, string fullPath) => false;
    }

    private static string Event(string tool, object arguments) =>
        JsonSerializer.Serialize(new { cwd = Cwd, toolName = tool, toolArgs = arguments });

    private string Run(string json)
    {
        using StringWriter stdout = new();
        Assert.Equal(0, Guard.Run(json, stdout, TextWriter.Null, copilotHome: CopilotHome));
        return stdout.ToString();
    }

    private string RunIn(string cwd, string path, string? home = null)
    {
        using StringWriter stdout = new();
        Guard.Run(
            JsonSerializer.Serialize(new { cwd, toolName = "view", toolArgs = new { path } }),
            stdout,
            TextWriter.Null,
            copilotHome: CopilotHome,
            homeDirectory: home);
        return stdout.ToString();
    }

    private string ReasonFor(string json)
    {
        using JsonDocument answer = JsonDocument.Parse(Run(json));
        Assert.Equal("deny", answer.RootElement.GetProperty("permissionDecision").GetString());
        return answer.RootElement.GetProperty("permissionDecisionReason").GetString()!;
    }
}
