using System.Text.Json;
using KyberWeave.Arbiter;
using KyberWeave.Core.FileSystem;
using KyberWeave.Data.Hooks;
using KyberWeave.Data.Hooks.Arbiter;
using KyberWeave.Tests;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// The worker guard's hot path: a Factory root session's tool call is never guarded, and the entry answers it from
/// one ledger file-existence test, so no container is built for the calls that make up most of the traffic.
/// </summary>
public sealed class WorkerGuardFactoryHotPathTests : IDisposable
{
    private const string Harness = "factory";
    private const string RootSession = "root-0001";
    private const string ChildSession = "child-0002";

    private readonly TempDirectory _temp = new();
    private readonly FileWorkerSessionStore _sessions = new(new PhysicalFileSystem());
    private readonly string _home;

    public WorkerGuardFactoryHotPathTests()
    {
        _home = Path.Combine(_temp.Path, "home");
        _sessions.EnsureDirectory(_home);
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task ARootSessionsExecuteBuildsNoContainer()
    {
        bool built = false;

        using StringReader stdin = new(Execute(RootSession));
        using StringWriter stdout = new();
        using StringWriter stderr = new();
        int exit = await Composition.DispatchAsync(
            ["worker-guard", "--harness", Harness, "--home", _home],
            stdin,
            stdout,
            stderr,
            commandFactory: null,
            services: () =>
            {
                built = true;
                throw new InvalidOperationException("the container was built for a root call");
            });

        Assert.Equal(0, exit);
        Assert.False(built);
        Assert.Equal(string.Empty, stdout.ToString());
    }

    [Fact]
    public async Task ACallForARecordedChildReachesTheContainer()
    {
        string key = WorkerSessionPaths.KeyOf(Harness, ChildSession);
        _sessions.AppendParent(_home, key, RootSession);
        _sessions.AppendType(_home, key, "dev-medium");
        bool built = false;

        using StringReader stdin = new(Execute(ChildSession));
        using StringWriter stdout = new();
        using StringWriter stderr = new();
        int exit = await Composition.DispatchAsync(
            ["worker-guard", "--harness", Harness, "--home", _home],
            stdin,
            stdout,
            stderr,
            commandFactory: null,
            services: () =>
            {
                built = true;
                throw new InvalidOperationException("the container was built");
            });

        // The container was reached, so its fault is the guard's usual fail-open: logged, allow, exit 0.
        Assert.Equal(0, exit);
        Assert.True(built);
        Assert.Contains("could not start", stderr.ToString(), StringComparison.Ordinal);
    }

    private static string Execute(string sessionId) => JsonSerializer.Serialize(new
    {
        session_id = sessionId,
        hook_event_name = "PreToolUse",
        tool_name = "Execute",
        tool_input = new { command = "ls", summary = "run", riskLevel = "low" },
        cwd = "/kw/repo",
    });
}
