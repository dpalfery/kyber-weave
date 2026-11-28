using System.Diagnostics;
using KyberWeave.Core.Processes;
using Xunit;

namespace KyberWeave.Tests.Processes;

/// <summary>
/// The asynchronous process primitives must produce exactly what their synchronous twins produce: the same
/// captured streams, exit codes, output-limit failure and timeout failure. Each case runs both members.
/// </summary>
public sealed class ProcessRunnerAsyncTests
{
    [Fact]
    public async Task RunAsyncCapturesTheSameOutputAndExitCodeAsRun()
    {
        ProcessResult sync = RunSynchronously(Probe(ProcessProbeProgram.Both), string.Empty);
        ProcessResult async = await ProcessRunner.RunAsync(Probe(ProcessProbeProgram.Both), string.Empty);

        Assert.Equal(sync.ExitCode, async.ExitCode);
        Assert.Equal(sync.StandardOutput, async.StandardOutput);
        Assert.Equal(sync.StandardError, async.StandardError);
        Assert.Equal("out", async.StandardOutput);
        Assert.Equal("err", async.StandardError);
    }

    [Fact]
    public async Task RunAsyncWritesTheCompleteStandardInputLikeRun()
    {
        ProcessResult sync = RunSynchronously(Probe(ProcessProbeProgram.Cat), "piped text");
        ProcessResult async = await ProcessRunner.RunAsync(Probe(ProcessProbeProgram.Cat), "piped text");

        Assert.Equal(sync.StandardOutput, async.StandardOutput);
        Assert.Equal("piped text", async.StandardOutput);
    }

    [Fact]
    public async Task RunAsyncReportsANonZeroExitCodeAndErrorTextLikeRun()
    {
        ProcessResult sync = RunSynchronously(Probe(ProcessProbeProgram.Fail, "7", "bad"), string.Empty);
        ProcessResult async = await ProcessRunner.RunAsync(Probe(ProcessProbeProgram.Fail, "7", "bad"), string.Empty);

        Assert.Equal(sync.ExitCode, async.ExitCode);
        Assert.Equal(7, async.ExitCode);
        Assert.Equal(sync.StandardError, async.StandardError);
        Assert.Equal("bad", async.StandardError);
    }

    [Fact]
    public async Task RunAsyncReportsTheSameOutputLimitFailureAsRun()
    {
        string verbose = new('x', 200);

        OutputLimitExceededException sync = Assert.Throws<OutputLimitExceededException>(
            () => RunSynchronously(Probe(ProcessProbeProgram.Echo, verbose), string.Empty, maxOutputBytes: 10));
        OutputLimitExceededException async = await Assert.ThrowsAsync<OutputLimitExceededException>(
            () => ProcessRunner.RunAsync(Probe(ProcessProbeProgram.Echo, verbose), string.Empty, maxOutputBytes: 10));

        Assert.Equal(sync.Message, async.Message);
    }

    [Fact]
    public async Task RunAsyncReportsTheSameTimeoutAsRun()
    {
        TimeoutException sync = Assert.Throws<TimeoutException>(
            () => RunSynchronously(
                Probe(ProcessProbeProgram.Sleep, "10000"),
                string.Empty,
                timeout: TimeSpan.FromSeconds(1)));
        TimeoutException async = await Assert.ThrowsAsync<TimeoutException>(
            () => ProcessRunner.RunAsync(
                Probe(ProcessProbeProgram.Sleep, "10000"),
                string.Empty,
                timeout: TimeSpan.FromSeconds(1)));

        Assert.Equal(sync.Message, async.Message);
    }

    [Fact]
    public async Task RunAsyncReportsTheSameCancellationAsRun()
    {
        using CancellationTokenSource source = new();
        await source.CancelAsync();

        _ = Assert.ThrowsAny<OperationCanceledException>(
            () => RunSynchronously(Probe(ProcessProbeProgram.Sleep, "10000"), string.Empty, cancellationToken: source.Token));
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ProcessRunner.RunAsync(Probe(ProcessProbeProgram.Sleep, "10000"), string.Empty, cancellationToken: source.Token));
    }

    [Fact]
    public async Task ReadToEndAsyncCapturesOutputAndExitCode()
    {
        using Process process = Start(Probe(ProcessProbeProgram.Both));

        ProcessResult result = await ProcessRunner.ReadToEndAsync(process);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("out", result.StandardOutput);
        Assert.Equal("err", result.StandardError);
    }

    private static ProcessStartInfo Probe(params string[] arguments)
    {
        ProcessStartInfo startInfo = new("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(ProcessProbeProgram.AssemblyPath);
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        return startInfo;
    }

    private static Process Start(ProcessStartInfo startInfo) =>
        ProcessRunner.StartRetryingTextFileBusy(ProcessRunner.CreateSafeStartInfo(startInfo));

    // The sync member is the behaviour under comparison. The helper keeps the call out of an async
    // method, where the analyzer would otherwise demand the async twin for the synchronous half.
    private static ProcessResult RunSynchronously(
        ProcessStartInfo startInfo,
        string standardInput,
        TimeSpan? timeout = null,
        long? maxOutputBytes = null,
        CancellationToken cancellationToken = default) =>
        ProcessRunner.Run(startInfo, standardInput, timeout, maxOutputBytes, cancellationToken);
}
