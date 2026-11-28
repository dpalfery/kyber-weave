using KyberWeave.Application.Review;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Review;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Review;

/// <summary>
/// The suppression scan and the command over it resolve the repository path through the path seam: a relative
/// spelling is judged against the injected working directory, never the process's own.
/// </summary>
public sealed class SuppressionSeamTests
{
    private static readonly string[] DiffArgv =
        ["diff", "--no-color", "--no-ext-diff", "-U0", "-M", "--relative", "--diff-filter=ACMRT", "origin/main", "--"];

    private static readonly string[] UntrackedArgv =
        ["ls-files", "-z", "--others", "--exclude-standard", "--"];

    private readonly InMemoryFileSystem _files = new();

    private readonly FakeProcessEnvironment _environment = new(currentDirectory: "/work");

    private SuppressionScanService Scanner(ScriptedProcessRunner programs) =>
        new(programs, _files, new NoFileReader(), new FakePathResolver(_files), _environment);

    [Fact]
    public void TheScanReportsARelativeDirectoryThatDoesNotExistUnderTheInjectedWorkingDirectory()
    {
        SuppressionScanResult result = Scanner(new ScriptedProcessRunner()).Scan("repo", "origin/main");

        Assert.False(result.Succeeded);
        Assert.Contains("/work/repo", result.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public void TheScanRunsGitInTheDirectoryARelativeSpellingResolvesTo()
    {
        _files.CreateDirectory("/work/repo");
        ScriptedProcessRunner programs = new ScriptedProcessRunner()
            .Script("git", new ProcessRunResult(0, string.Empty, false), DiffArgv)
            .Script("git", new ProcessRunResult(0, string.Empty, false), UntrackedArgv);

        SuppressionScanResult result = Scanner(programs).Scan("repo", "origin/main");

        Assert.True(result.Succeeded);
        Assert.Empty(result.Findings);
        Assert.All(programs.Calls, call => Assert.Equal("/work/repo", call.WorkingDirectory));
    }

    [Fact]
    public void TheCommandRefusesARelativePathThatResolvesToNoDirectory()
    {
        ReviewSuppressionsService service = new(
            new RecordingScanner(), _files, new FakePathResolver(_files), _environment);

        ReviewCommandResult result = service.Run(new ReviewSuppressionsRequest("repo", null));

        Assert.Equal(ReviewCommandResult.ClientInputExitCode, result.ExitCode);
        Assert.Contains("/work/repo", result.ClientError, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCommandHandsTheScannerTheDirectoryARelativeSpellingResolvesTo()
    {
        _files.CreateDirectory("/work/repo");
        RecordingScanner scanner = new();
        ReviewSuppressionsService service = new(
            scanner, _files, new FakePathResolver(_files), _environment);

        ReviewCommandResult result = service.Run(new ReviewSuppressionsRequest("repo", "origin/main"));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("/work/repo", scanner.ScannedDirectory);
        Assert.Equal("origin/main", scanner.BaseRef);
    }

    [Fact]
    public async Task TheCommandAsyncHandsTheScannerTheDirectoryARelativeSpellingResolvesTo()
    {
        _files.CreateDirectory("/work/repo");
        RecordingScanner scanner = new();
        ReviewSuppressionsService service = new(
            scanner, _files, new FakePathResolver(_files), _environment);

        ReviewCommandResult result = await service.RunAsync(
            new ReviewSuppressionsRequest("repo", "origin/main"), CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("/work/repo", scanner.ScannedDirectory);
        Assert.Equal("origin/main", scanner.BaseRef);
    }

    [Fact]
    public async Task TheScanAsyncRunsGitWithoutBlockingAndReportsCleanChange()
    {
        _files.CreateDirectory("/work/repo");
        ScriptedProcessRunner programs = new ScriptedProcessRunner()
            .Script("git", new ProcessRunResult(0, string.Empty, false), DiffArgv)
            .Script("git", new ProcessRunResult(0, string.Empty, false), UntrackedArgv);

        SuppressionScanResult result = await Scanner(programs).ScanAsync("repo", "origin/main", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Empty(result.Findings);
        Assert.All(programs.Calls, call => Assert.Equal("/work/repo", call.WorkingDirectory));
    }

    /// <summary>A bounded reader the scan never reaches, because every scripted change adds no untracked file.</summary>
    private sealed class NoFileReader : IBoundedFileReader
    {
        public byte[]? ReadBytes(string path, int maxBytes) => throw new InvalidOperationException("unexpected read");
    }

    /// <summary>A scan that records what it was asked and reports a change that adds no suppression.</summary>
    private sealed class RecordingScanner : ISuppressionScanService
    {
        public string? ScannedDirectory { get; private set; }

        public string? BaseRef { get; private set; }

        public SuppressionScanResult Scan(string workingDirectory, string baseRef)
        {
            ScannedDirectory = workingDirectory;
            BaseRef = baseRef;
            return new SuppressionScanResult([], null);
        }

        public Task<SuppressionScanResult> ScanAsync(
            string workingDirectory,
            string baseRef,
            CancellationToken cancellationToken = default)
        {
            ScannedDirectory = workingDirectory;
            BaseRef = baseRef;
            return Task.FromResult(new SuppressionScanResult([], null));
        }
    }
}
