using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Processes;
using Xunit;

namespace KyberWeave.Tests.Processes;

/// <summary>
/// The contract every <see cref="IProcessRunner"/> must satisfy. Run it once against the scripted fake and once
/// against the real adapter, so a fake cannot keep a unit suite green while real programs behave differently.
/// </summary>
/// <remarks>
/// A derived class supplies the implementation under test and a real program to start. The fake derived class
/// answers the same requests from its script table; the adapter derived class starts a tiny probe program that
/// exists on macOS, Linux and Windows CI.
/// </remarks>
public abstract class ProcessRunnerContractTests : IDisposable
{
    /// <summary>A program name no machine has installed.</summary>
    protected const string MissingProgram = "kw-no-such-program-contract";

    /// <summary>A program name that is not on the path.</summary>
    protected const string MissingProgramOnPath = "kw-no-such-program-on-path-contract";

    /// <summary>The variable the environment contract checks.</summary>
    protected const string MarkerVariable = "KW_PROCESS_SEAM_CONTRACT";

    /// <summary>The value <see cref="MarkerVariable"/> must carry.</summary>
    protected const string MarkerValue = "marker-value";

    /// <summary>A process id no machine can be running.</summary>
    protected const int DeadProcessId = int.MaxValue;

    /// <summary>Creates the implementation under test.</summary>
    /// <returns>The process seam. The suite may create more than one instance.</returns>
    protected abstract IProcessRunner CreateRunner();

    /// <summary>Builds the same request for both implementations, pointing at the probe.</summary>
    /// <param name="arguments">The probe instruction and its arguments.</param>
    /// <param name="standardInput">The text written to the probe, if any.</param>
    /// <param name="timeout">The longest the probe may run, if bounded.</param>
    /// <param name="workingDirectory">The directory to start in; null uses <see cref="WorkingDirectory"/>.</param>
    /// <param name="maxOutputBytes">The most bytes each stream may produce, if bounded.</param>
    /// <returns>The request.</returns>
    protected abstract ProcessRunRequest Probe(
        string[] arguments,
        string? standardInput = null,
        TimeSpan? timeout = null,
        string? workingDirectory = null,
        long? maxOutputBytes = null);

    /// <summary>The directory the probe starts in by default.</summary>
    protected abstract string WorkingDirectory { get; }

    /// <summary>A directory that does not exist.</summary>
    protected abstract string MissingDirectory { get; }

    /// <summary>A process id that is running now.</summary>
    protected abstract int LiveProcessId { get; }

    /// <summary>Creates a file the probe can read relative to its working directory.</summary>
    /// <param name="name">The file name.</param>
    /// <param name="content">The content the probe reads back.</param>
    protected abstract void PrepareWorkingDirectoryFile(string name, string content);

    /// <summary>Releases anything the implementation under test created.</summary>
    public virtual void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task CapturesStandardOutputAndAZeroExitCode()
    {
        ProcessRunResult result = await CreateRunner()
            .RunAsync(Probe([ProcessProbeProgram.Echo, "hello"]), CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Equal("hello", result.StandardOutput);
        Assert.Equal("hello", result.Output);
        Assert.Equal(string.Empty, result.StandardError);
    }

    [Fact]
    public async Task KeepsStandardOutputAndStandardErrorApart()
    {
        ProcessRunResult result = await CreateRunner()
            .RunAsync(Probe([ProcessProbeProgram.Both]), CancellationToken.None);

        Assert.Equal("out", result.StandardOutput);
        Assert.Equal("err", result.StandardError);
        Assert.Contains("out", result.Output, StringComparison.Ordinal);
        Assert.Contains("err", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportsANonZeroExitCodeAndTheErrorText()
    {
        ProcessRunResult result = await CreateRunner()
            .RunAsync(Probe([ProcessProbeProgram.Fail, "7", "bad"]), CancellationToken.None);

        Assert.Equal(7, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Equal("bad", result.StandardError);
    }

    [Fact]
    public async Task WritesTheCompleteStandardInput()
    {
        ProcessRunResult result = await CreateRunner()
            .RunAsync(Probe([ProcessProbeProgram.Cat], standardInput: "piped text"), CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("piped text", result.StandardOutput);
    }

    [Fact]
    public async Task ReportsWhenAStreamExceedsTheOutputLimit()
    {
        string verbose = new('x', 200);

        await Assert.ThrowsAsync<OutputLimitExceededException>(() =>
            CreateRunner().RunAsync(Probe([ProcessProbeProgram.Echo, verbose], maxOutputBytes: 10), CancellationToken.None));
    }

    [Fact]
    public async Task StartsInTheRequestedWorkingDirectory()
    {
        PrepareWorkingDirectoryFile("marker.txt", "from-working-directory");

        ProcessRunResult result = await CreateRunner()
            .RunAsync(Probe([ProcessProbeProgram.Read, "marker.txt"]), CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("from-working-directory", result.StandardOutput);
    }

    [Fact]
    public async Task PassesTheRequestedEnvironment()
    {
        ProcessRunResult result = await CreateRunner()
            .RunAsync(Probe([ProcessProbeProgram.Environment, MarkerVariable]), CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(MarkerValue, result.StandardOutput);
    }

    [Fact]
    public async Task ReportsATimedOutRunWithoutOutput()
    {
        ProcessRunResult result = await CreateRunner()
            .RunAsync(Probe([ProcessProbeProgram.Sleep, "10000"], timeout: TimeSpan.FromSeconds(1)), CancellationToken.None);

        Assert.True(result.TimedOut);
        Assert.Equal(-1, result.ExitCode);
        Assert.Equal(string.Empty, result.Output);
    }

    [Fact]
    public async Task ReportsAMissingProgramAsCommandNotFound()
    {
        ProcessRunRequest request = Probe([ProcessProbeProgram.Echo, "hello"]) with { Program = MissingProgram };

        FileNotFoundException exception = await Assert.ThrowsAsync<FileNotFoundException>(
            () => CreateRunner().RunAsync(request, CancellationToken.None));

        Assert.Contains("command not found", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportsAMissingProgramWithoutAWorkingDirectoryAsCommandNotFound()
    {
        ProcessRunRequest request = new()
        {
            Program = MissingProgram,
            Timeout = TimeSpan.FromSeconds(5)
        };

        FileNotFoundException exception = await Assert.ThrowsAsync<FileNotFoundException>(
            () => CreateRunner().RunAsync(request, CancellationToken.None));

        Assert.Contains("command not found", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportsAMissingWorkingDirectoryByItsOwnName()
    {
        ProcessRunRequest request = Probe([ProcessProbeProgram.Echo, "hello"], workingDirectory: MissingDirectory);

        DirectoryNotFoundException exception = await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => CreateRunner().RunAsync(request, CancellationToken.None));

        Assert.Contains("working directory not found", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FindsAProgramThatIsOnThePathAndNotOneThatIsNot()
    {
        IProcessRunner runner = CreateRunner();

        string? found = runner.FindOnPath("dotnet");

        Assert.NotNull(found);
        Assert.Equal("dotnet", Path.GetFileNameWithoutExtension(found), ignoreCase: true);
        Assert.Null(runner.FindOnPath(MissingProgramOnPath));
    }

    [Fact]
    public void ReportsWhetherAProcessIsAlive()
    {
        IProcessRunner runner = CreateRunner();

        Assert.True(runner.IsAlive(LiveProcessId));
        Assert.False(runner.IsAlive(DeadProcessId));
    }
}
