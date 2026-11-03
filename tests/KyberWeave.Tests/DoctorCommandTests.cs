using KyberWeave.Cli.Commands.Doctor;
using KyberWeave.Core.Doctor;
using KyberWeave.Core.Processes;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
///     Verifies that <see cref="DoctorCommand" /> and <see cref="RepositoryDoctor.Run" /> correctly inspect
///     a specified canonical repository path when invoked with an explicit path argument from within an
///     arbitrary directory (simulating a worktree checking its canonical parent) (T3).
/// </summary>
public sealed class DoctorCommandTests : IDisposable
{
    private readonly TempDirectory _arbitraryWorktree = new();
    private readonly TempDirectory _canonicalRepo = new();
    private readonly TempDirectory _stubs = new();

    private string CanonicalPath => _canonicalRepo.Path;

    private string WorktreePath => _arbitraryWorktree.Path;

    public void Dispose()
    {
        _stubs.Dispose();
        _arbitraryWorktree.Dispose();
        _canonicalRepo.Dispose();
    }

    /// <summary>
    ///     When invoked with an explicit path argument pointing to the canonical repository from an arbitrary
    ///     directory, DoctorCommand executes checks against the canonical repository rather than the caller's working
    ///     directory.
    /// </summary>
    [Fact]
    public void DoctorCommand_Check_WithExplicitCanonicalPathFromArbitraryDirectory_InspectsCanonicalRepository()
    {
        string codegraphDir = Path.Combine(CanonicalPath, ".codegraph");
        Directory.CreateDirectory(codegraphDir);

        string stubPath = DoctorHarness.HealthyStub(_stubs.Path, CanonicalPath);

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

        DoctorCommand command = new()
        {
            Executor = executor,
            IsTerminal = () => true,
            ReadEnvironment = _ => null,
            McpExecutable = stubPath
        };

        DoctorSettings settings = new()
        {
            Path = CanonicalPath,
            Check = true
        };

        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(() =>
            command.Execute(null!, settings, CancellationToken.None));

        Assert.Equal(0, execution.Result);
        Assert.True(gitInvokedOnCanonical,
            "Git rev-parse must be invoked with WorkingDirectory pointing to the canonical repository.");
    }

    /// <summary>
    ///     Verifies that DoctorCommand resolves relative path arguments pointing to the canonical parent
    ///     correctly and inspects the canonical repository.
    /// </summary>
    [Fact]
    public void DoctorCommand_Check_WithRelativePathToCanonicalParent_ResolvesAndInspectsCanonicalPath()
    {
        string codegraphDir = Path.Combine(CanonicalPath, ".codegraph");
        Directory.CreateDirectory(codegraphDir);

        string stubPath = DoctorHarness.HealthyStub(_stubs.Path, CanonicalPath);

        string relativePath = Path.GetRelativePath(Environment.CurrentDirectory, CanonicalPath);

        bool gitInvokedOnCanonical = false;
        ScriptedProcessExecutor executor = new(startInfo =>
        {
            if (startInfo.FileName == "git")
            {
                Assert.Equal(CanonicalPath, startInfo.WorkingDirectory);
                gitInvokedOnCanonical = true;
                return new ProcessResult(0, CanonicalPath + "\n", string.Empty);
            }

            if (startInfo.FileName == "codegraph") return DoctorHarness.Ok();

            throw new InvalidOperationException($"Unexpected child process: '{startInfo.FileName}'.");
        });

        DoctorCommand command = new()
        {
            Executor = executor,
            IsTerminal = () => true,
            ReadEnvironment = _ => null,
            McpExecutable = stubPath
        };

        DoctorSettings settings = new()
        {
            Path = relativePath,
            Check = true
        };

        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(() =>
            command.Execute(null!, settings, CancellationToken.None));

        Assert.Equal(0, execution.Result);
        Assert.True(gitInvokedOnCanonical,
            "Git rev-parse must be invoked with WorkingDirectory pointing to the canonical repository.");
    }

    /// <summary>
    ///     Verifies that RepositoryDoctor.Run inspects the explicit canonical path supplied as root,
    ///     ensuring that DoctorReport.Root reflects the canonical repository and all checks pass.
    /// </summary>
    [Fact]
    public void RepositoryDoctor_Run_WithExplicitCanonicalPath_InspectsCanonicalRootAndPassesChecks()
    {
        string codegraphDir = Path.Combine(CanonicalPath, ".codegraph");
        Directory.CreateDirectory(codegraphDir);

        string stubPath = DoctorHarness.HealthyStub(_stubs.Path, CanonicalPath);
        ScriptedProcessExecutor executor = DoctorHarness.Executor(CanonicalPath);

        DoctorReport report = RepositoryDoctor.Run(
            CanonicalPath,
            DoctorMode.Check,
            DoctorHarness.Options(stubPath),
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
    public void DoctorCommand_WhenTargetIsNotGitRepository_RefusesWithExit2()
    {
        ScriptedProcessExecutor executor = new(startInfo =>
            startInfo.FileName == "git"
                ? new ProcessResult(128, string.Empty, "fatal: not a git repository")
                : throw new InvalidOperationException($"Unexpected child process: '{startInfo.FileName}'."));

        DoctorCommand command = new()
        {
            Executor = executor,
            IsTerminal = () => true,
            ReadEnvironment = _ => null,
            McpExecutable = "kyber-weave-mcp-absent"
        };

        DoctorSettings settings = new()
        {
            Path = WorktreePath,
            Check = true
        };

        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(() =>
            command.Execute(null!, settings, CancellationToken.None));

        Assert.Equal(2, execution.Result);
    }
}
