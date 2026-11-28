using KyberWeave.Core.Doctor;
using KyberWeave.Domain.CodeGraph;
using KyberWeave.Domain.Doctor;
using KyberWeave.Tests;
using KyberWeave.Tests.Doctor;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Application.Tests.Doctor;

/// <summary>
/// Pins the D4 check <c>codegraph.nested-worktrees</c>: a CodeGraph index that holds a run worktree under
/// <c>.agent_worktrees/</c> fails the doctor, because the worktree's copy of the code would be indexed twice and
/// returned as if it were the repository.
/// </summary>
/// <remarks>
/// Each test owns its in-memory repository and the index it reports. The check reads the index through the
/// <see cref="KyberWeave.Contracts.Repositories.ICodeGraphPortsFactory"/> port, so a fake resolver stands in for it.
/// </remarks>
public sealed class RepositoryDoctorNestedWorktreeTests
{
    private const string McpExecutable = "kyber-weave-mcp";

    private const string CheckId = "codegraph.nested-worktrees";

    private const string Fix = "add '.agent_worktrees/' to .gitignore, then rebuild the index with 'codegraph index .'";

    /// <summary>An index whose only file is a run worktree's copy of a source file.</summary>
    private static InMemoryDoctorWorld WorldIndexing(params string[] files) =>
        new(FakeCodeGraphResolver.WithNodes(files.Select(file => new CodeGraphNode(file, "file", file, file, file, "csharp", 1)).ToArray()));

    /// <summary>Options for a healthy MCP server over <paramref name="world"/>.</summary>
    private static DoctorOptions HealthyOptions(InMemoryDoctorWorld world)
    {
        world.HealthyMcp(world.Root);
        return DoctorHarness.Options(McpExecutable);
    }

    /// <summary>An index that holds a file under the run worktree folder fails the check with the fix line, exit 1, in check, provision and ensure modes.</summary>
    [Theory]
    [InlineData(DoctorMode.Check)]
    [InlineData(DoctorMode.Provision)]
    [InlineData(DoctorMode.Ensure)]
    public async Task IndexHoldingAgentWorktreeFile_FailsWithFixAndExits1(DoctorMode mode)
    {
        InMemoryDoctorWorld world = WorldIndexing(".agent_worktrees/run-1/src/Program.cs");
        world.Files.CreateDirectory(Path.Combine(world.Root, ".codegraph"));
        ScriptedProcessExecutor executor = DoctorHarness.Executor(world.Root);

        DoctorReport report = await world.Doctor.RunAsync(world.Root, mode, HealthyOptions(world), executor, _ => null, world.Locks);

        DoctorCheck check = DoctorHarness.CheckNamed(report, CheckId);
        Assert.False(check.Passed);
        Assert.Equal(Fix, check.Fix);
        Assert.Equal(1, report.ExitCode);
    }

    /// <summary>Under <c>stop-on-tool-failure: false</c> an ensure run reports the nested worktree as a warning and exits 0.</summary>
    [Fact]
    public async Task EnsureWithStopOnToolFailureFalse_ReportsWarningAndExits0()
    {
        InMemoryDoctorWorld world = WorldIndexing(".agent_worktrees/run-1/src/Program.cs");
        world.WriteGoverned("stop-on-tool-failure: false\n");
        world.Files.CreateDirectory(Path.Combine(world.Root, ".codegraph"));
        ScriptedProcessExecutor executor = DoctorHarness.Executor(world.Root);

        DoctorReport report = await world.Doctor.RunAsync(world.Root, DoctorMode.Ensure, HealthyOptions(world), executor, _ => null, world.Locks);

        DoctorCheck check = DoctorHarness.CheckNamed(report, CheckId);
        Assert.True(check.Passed);
        Assert.StartsWith("warning: ", check.Reason, StringComparison.Ordinal);
        Assert.Equal(0, report.ExitCode);
    }

    /// <summary>An index with no file under the run worktree folder passes the check.</summary>
    [Fact]
    public async Task IndexWithoutAgentWorktreeFile_PassesCheck()
    {
        InMemoryDoctorWorld world = WorldIndexing("src/Program.cs");
        world.Files.CreateDirectory(Path.Combine(world.Root, ".codegraph"));
        ScriptedProcessExecutor executor = DoctorHarness.Executor(world.Root);

        DoctorReport report = await world.Doctor.RunAsync(world.Root, DoctorMode.Check, HealthyOptions(world), executor, _ => null, world.Locks);

        Assert.True(DoctorHarness.CheckNamed(report, CheckId).Passed);
    }

    /// <summary>An unavailable index passes this check: <c>codegraph.index</c> already reports the missing index, and a second line would repeat it.</summary>
    [Fact]
    public async Task IndexUnavailable_PassesCheckWithoutNoise()
    {
        InMemoryDoctorWorld world = new(new UnavailableCodeGraphResolver());
        world.Files.CreateDirectory(Path.Combine(world.Root, ".codegraph"));
        ScriptedProcessExecutor executor = DoctorHarness.Executor(world.Root);

        DoctorReport report = await world.Doctor.RunAsync(world.Root, DoctorMode.Check, HealthyOptions(world), executor, _ => null, world.Locks);

        DoctorCheck check = DoctorHarness.CheckNamed(report, CheckId);
        Assert.True(check.Passed);
        Assert.Null(check.Reason);
    }

    /// <summary>A sibling folder whose name only starts with <c>.agent_worktrees</c> is not a run worktree, so it does not fail the check.</summary>
    [Fact]
    public async Task SiblingFolderWithSimilarPrefix_PassesCheck()
    {
        InMemoryDoctorWorld world = WorldIndexing(".agent_worktrees-other/src/Program.cs");
        world.Files.CreateDirectory(Path.Combine(world.Root, ".codegraph"));
        ScriptedProcessExecutor executor = DoctorHarness.Executor(world.Root);

        DoctorReport report = await world.Doctor.RunAsync(world.Root, DoctorMode.Check, HealthyOptions(world), executor, _ => null, world.Locks);

        Assert.True(DoctorHarness.CheckNamed(report, CheckId).Passed);
    }
}
