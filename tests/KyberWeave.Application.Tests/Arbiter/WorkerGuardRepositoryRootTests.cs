using System.Text.Json;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Arbiter.Stores;
using KyberWeave.Core.Configuration;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Arbiter;

/// <summary>
/// The worker guard finds the repository whose ledger names a read-only dispatch's technology by probing for
/// <c>.git</c> through the file-system seam. The probe's answer decides which ledger directory a refusal reads, so
/// it is observed here through the directory the guard asks the state factory for, over an in-memory file system.
/// </summary>
public sealed class WorkerGuardRepositoryRootTests
{
    private const string Home = "/kw/home";

    private readonly InMemoryFileSystem _files = new();

    [Fact]
    public void ADirectoryNamedGitAboveTheWorkingDirectoryMakesItsParentTheRepositoryRoot()
    {
        _files.CreateDirectory("/kw/repo/.git");
        _files.CreateDirectory("/kw/repo/src/app");

        List<string> opened = RunFrom("/kw/repo/src/app");

        Assert.Contains("/kw/repo/artifacts/arbiter", opened);
        Assert.DoesNotContain("/kw/repo/src/app/artifacts/arbiter", opened);
    }

    [Fact]
    public void AFileNamedGitMarksALinkedWorktreesRoot()
    {
        _files.CreateDirectory("/kw/wt/feature/pkg");
        _files.WriteAllText("/kw/wt/feature/.git", "gitdir: /kw/repo/.git/worktrees/feature\n");

        List<string> opened = RunFrom("/kw/wt/feature/pkg");

        Assert.Contains("/kw/wt/feature/artifacts/arbiter", opened);
    }

    [Fact]
    public void WithNoGitEntryAboveItTheWorkingDirectoryItselfIsTheRoot()
    {
        _files.CreateDirectory("/kw/loose/inner");

        List<string> opened = RunFrom("/kw/loose/inner");

        Assert.Contains("/kw/loose/inner/artifacts/arbiter", opened);
        Assert.DoesNotContain("/kw/loose/artifacts/arbiter", opened);
    }

    /// <summary>Runs a read-only dispatch's command through the guard and returns every ledger directory it opened.</summary>
    private List<string> RunFrom(string cwd)
    {
        RecordingState state = new();
        WorkerGuard guard = new(
            new NoBudgets(),
            new NoCheckouts(),
            new DefaultConfig(),
            new FakeProcessEnvironment(currentDirectory: "/kw/work"),
            TimeProvider.System,
            state,
            _files);
        string json = JsonSerializer.Serialize(new
        {
            cwd,
            session_id = "sess-1",
            hook_event_name = "PreToolUse",
            agent_id = "agent-1",
            agent_type = "platform-researcher",
            tool_name = "Bash",
            tool_input = new { command = "az vm list" },
        });

        using StringWriter stdout = new();
        _ = guard.Run("claude", json, stdout, TextWriter.Null, Home);

        return state.Opened;
    }

    private sealed class DefaultConfig : IHostConfigLoader
    {
        public HostConfigLoadResult TryLoad(string repoRoot) => new(true, KyberWeaveConfigAssembler.FromYaml("ontology:\n  docs-root: docs\n"), null, null);
    }

    private sealed class RecordingState : IArbiterStateFactory
    {
        public List<string> Opened { get; } = [];

        // An unreadable ledger is the guard's documented fail-closed path, so throwing after recording is enough.
        public IArbiterLedger OpenLedger(string arbiterDirectory)
        {
            Opened.Add(arbiterDirectory);
            throw new IOException("no ledger");
        }

        public IArbiterDecisionLog OpenDecisionLog(string arbiterDirectory) => throw new NotSupportedException();

        public IArbiterRunStore OpenRunStore(string arbiterDirectory, ArbiterStoreOptions? options = null, TimeProvider? clock = null) =>
            throw new NotSupportedException();
    }
}
