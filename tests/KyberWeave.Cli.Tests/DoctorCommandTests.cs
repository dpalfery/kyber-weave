using KyberWeave.Cli.Commands.Doctor;
using KyberWeave.Tests.Doctor;
using KyberWeave.Domain.Doctor;
using KyberWeave.Core.Processes;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
///     Verifies that <see cref="DoctorCommand" /> and <c>IRepositoryDoctor.Run</c> correctly inspect
///     a specified canonical repository path when invoked with an explicit path argument from within an
///     arbitrary directory (simulating a worktree checking its canonical parent) (T3).
/// </summary>
public sealed class DoctorCommandTests
{
    private const string McpExecutable = "kyber-weave-mcp";

    private readonly InMemoryDoctorWorld _world = new();

    private string CanonicalPath => _world.Root;

    private string WorktreePath => _world.Environment.CurrentDirectory;

    public DoctorCommandTests() => _world.Files.CreateDirectory(WorktreePath);

    /// <summary>
    ///     When invoked with an explicit path argument pointing to the canonical repository from an arbitrary
    ///     directory, DoctorCommand executes checks against the canonical repository rather than the caller's working
    ///     directory.
    /// </summary>
    [Fact]
    public async Task DoctorCommand_Check_WithExplicitCanonicalPathFromArbitraryDirectory_InspectsCanonicalRepository()
    {
        _world.Files.CreateDirectory(Path.Combine(CanonicalPath, ".codegraph"));
        _world.HealthyMcp(CanonicalPath);

        bool gitInvokedOnCanonical = false;
        ScriptedProcessExecutor executor = new(startInfo =>
        {
            if (startInfo.FileName == "git")
            {
                if (string.Equals(startInfo.WorkingDirectory, CanonicalPath, StringComparison.Ordinal))
                    gitInvokedOnCanonical = true;

                return new ProcessResult(0, CanonicalPath + "\n", string.Empty);
            }

            if (startInfo.FileName == "codegraph") return DoctorHarness.Ok();

            throw new InvalidOperationException($"Unexpected child process: '{startInfo.FileName}'.");
        });

        DoctorCommand command = _world.Command(true, executor, McpExecutable);
        DoctorSettings settings = new()
        {
            Path = CanonicalPath,
            Check = true
        };

        int exitCode = await command.ExecuteAsync(null!, settings, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.True(gitInvokedOnCanonical,
            "Git rev-parse must be invoked with WorkingDirectory pointing to the canonical repository.");
    }

    /// <summary>
    ///     Verifies that DoctorCommand resolves relative path arguments pointing to the canonical parent
    ///     correctly and inspects the canonical repository.
    /// </summary>
    [Fact]
    public async Task DoctorCommand_Check_WithRelativePathToCanonicalParent_ResolvesAndInspectsCanonicalPath()
    {
        _world.Files.CreateDirectory(Path.Combine(CanonicalPath, ".codegraph"));
        _world.HealthyMcp(CanonicalPath);

        string relativePath = Path.GetRelativePath(WorktreePath, CanonicalPath);
        string expectedCanonicalPath = Path.GetFullPath(relativePath, WorktreePath);

        bool gitInvokedOnCanonical = false;
        ScriptedProcessExecutor executor = new(startInfo =>
        {
            if (startInfo.FileName == "git")
            {
                Assert.Equal(expectedCanonicalPath, startInfo.WorkingDirectory);
                gitInvokedOnCanonical = true;
                return new ProcessResult(0, CanonicalPath + "\n", string.Empty);
            }

            if (startInfo.FileName == "codegraph") return DoctorHarness.Ok();

            throw new InvalidOperationException($"Unexpected child process: '{startInfo.FileName}'.");
        });

        DoctorSettings settings = new()
        {
            Path = relativePath,
            Check = true
        };

        DoctorCommand command = _world.Command(true, executor, McpExecutable);

        int exitCode = await command.ExecuteAsync(null!, settings, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.True(gitInvokedOnCanonical,
            "Git rev-parse must be invoked with WorkingDirectory pointing to the canonical repository.");
    }

    /// <summary>
    ///     Verifies that IRepositoryDoctor.Run inspects the explicit canonical path supplied as root,
    ///     ensuring that DoctorReport.Root reflects the canonical repository and all checks pass.
    /// </summary>
    [Fact]
    public async Task RepositoryDoctor_Run_WithExplicitCanonicalPath_InspectsCanonicalRootAndPassesChecks()
    {
        _world.Files.CreateDirectory(Path.Combine(CanonicalPath, ".codegraph"));
        _world.HealthyMcp(CanonicalPath);
        ScriptedProcessExecutor executor = DoctorHarness.Executor(CanonicalPath);

        DoctorReport report = await _world.Doctor.RunAsync(
            CanonicalPath,
            DoctorMode.Check,
            DoctorHarness.Options(McpExecutable),
            executor,
            _ => null);

        Assert.Equal(0, report.ExitCode);
        Assert.Equal(CanonicalPath, report.Root);

        DoctorCheck indexCheck = DoctorHarness.CheckNamed(report, "codegraph.index");
        Assert.True(indexCheck.Passed);

        DoctorCheck mcpCheck = DoctorHarness.CheckNamed(report, "mcp.start");
        Assert.True(mcpCheck.Passed);
    }

    /// <summary>
    ///     When the explicit path argument is invalid or points to a non-existent path outside any repository,
    ///     DoctorCommand refuses with exit 2.
    /// </summary>
    [Fact]
    public async Task DoctorCommand_WhenTargetIsNotGitRepository_RefusesWithExit2()
    {
        ScriptedProcessExecutor executor = new(startInfo =>
            startInfo.FileName == "git"
                ? new ProcessResult(128, string.Empty, "fatal: not a git repository")
                : throw new InvalidOperationException($"Unexpected child process: '{startInfo.FileName}'."));

        DoctorCommand command = _world.Command(true, executor, "kyber-weave-mcp-absent");
        DoctorSettings settings = new()
        {
            Path = WorktreePath,
            Check = true
        };

        int exitCode = await command.ExecuteAsync(null!, settings, CancellationToken.None);

        Assert.Equal(2, exitCode);
    }

    /// <summary>
    ///     Provisioning an existing index refreshes it with <c>codegraph sync</c> and never re-initialises it.
    /// </summary>
    /// <remarks>
    ///     <c>init</c> over a live index throws the work away and every later run pays to rebuild it. <c>sync</c>
    ///     keeps the index current at a fraction of the cost, and <c>index</c> is reserved for the repair step a
    ///     failed ensure performs once.
    /// </remarks>
    [Fact]
    public async Task DoctorCommand_ProvisionWithAnExistingIndex_SyncsAndNeverInitialises()
    {
        _world.Files.CreateDirectory(Path.Combine(CanonicalPath, ".codegraph"));
        _world.HealthyMcp(CanonicalPath);
        ScriptedProcessExecutor executor = DoctorHarness.Executor(CanonicalPath);

        DoctorReport report = await _world.Doctor.RunAsync(
            CanonicalPath,
            DoctorMode.Provision,
            DoctorHarness.Options(McpExecutable),
            executor,
            _ => null);

        Assert.Contains(
            executor.CallsTo("codegraph"),
            call => call.ArgumentList.Contains("sync", StringComparer.Ordinal));
        DoctorHarness.AssertNoCall(executor, "init");
        Assert.Equal(0, report.ExitCode);
    }

    /// <summary>
    ///     An ensure run on a machine with no CodeGraph binary exits 3, the code that means "a human has to
    ///     install this" rather than "something broke".
    /// </summary>
    /// <remarks>
    ///     The worker that runs ensure branches on this code: exit 3 is the one failure a repository-local
    ///     command cannot repair, because Kyber-Weave never installs external software as a side effect.
    /// </remarks>
    [Fact]
    public void DoctorCommand_EnsureWithNoCodeGraphBinary_Exits3()
    {
        ScriptedProcessExecutor executor = DoctorHarness.Executor(
            CanonicalPath,
            _ => throw DoctorHarness.BinaryNotFound());

        int exitCode = DoctorAppHarness.Run(
            _world.Command(false, executor, "kyber-weave-mcp-absent"),
            "doctor",
            "--ensure",
            CanonicalPath);

        Assert.Equal(3, exitCode);
    }
}
