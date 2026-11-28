using System.ComponentModel;
using System.Diagnostics;
using KyberWeave.Core.Processes;
using Xunit;
using Xunit.Sdk;

namespace KyberWeave.Integration.Tests;

/// <summary>
/// Full-duplex child process execution. A parent that fills stdin before draining output
/// can deadlock with a child that fills its output pipes before reading stdin.
/// </summary>
public sealed class ProcessRunnerInputTests
{
    private const int BytesPerStream = 300_000;
    private const int TimeoutSeconds = 30;

    private static string Emit(int bytes, char fill, bool toStandardError) =>
        $"head -c {bytes} /dev/zero | tr '\\0' '{fill}'{(toStandardError ? " >&2" : string.Empty)}";

    private static ProcessStartInfo CreateShellStartInfo(string script)
    {
        ProcessStartInfo startInfo = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(script);
        return startInfo;
    }

    [Fact]
    public async Task RunWithLargeInputAndOutputTransfersAllStreamsWithoutDeadlock()
    {
        if (OperatingSystem.IsWindows())
        {
            throw SkipException.ForSkip("POSIX shell process execution tests are not run on Windows.");
        }

        string input = new string('i', BytesPerStream);
        ProcessStartInfo startInfo = CreateShellStartInfo(
            $"{Emit(BytesPerStream, 'e', toStandardError: true)}; " +
            $"{Emit(BytesPerStream, 'o', toStandardError: false)}; " +
            "count=$(wc -c | tr -d '[:space:]'); printf '\\nstdin:%s\\n' \"$count\"; exit 11");

        ProcessResult result = await ProcessRunner.RunAsync(
            startInfo,
            input,
            TimeSpan.FromSeconds(TimeoutSeconds)).WaitAsync(TimeSpan.FromSeconds(TimeoutSeconds));

        Assert.Equal(new string('o', BytesPerStream) + $"\nstdin:{BytesPerStream}\n", result.StandardOutput);
        Assert.Equal(new string('e', BytesPerStream), result.StandardError);
        Assert.Equal(11, result.ExitCode);
    }

    [Fact]
    public async Task RunPreservesCallerEnvironmentOverrides()
    {
        if (OperatingSystem.IsWindows())
        {
            throw SkipException.ForSkip("POSIX shell process execution tests are not run on Windows.");
        }

        ProcessStartInfo startInfo = CreateShellStartInfo("printf '%s' \"$KYBER_WEAVE_PROCESS_MARKER\"");
        startInfo.Environment["KYBER_WEAVE_PROCESS_MARKER"] = "from-caller";

        ProcessResult result = await ProcessRunner.RunAsync(startInfo, string.Empty, TimeSpan.FromSeconds(TimeoutSeconds));

        Assert.Equal("from-caller", result.StandardOutput);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("stdin")]
    [InlineData("stdout")]
    [InlineData("stderr")]
    public async Task RunWhenARequiredStreamIsNotRedirectedRejectsTheStartInfo(string stream)
    {
        ProcessStartInfo startInfo = CreateShellStartInfo("exit 0");
        switch (stream)
        {
            case "stdin":
                startInfo.RedirectStandardInput = false;
                break;
            case "stdout":
                startInfo.RedirectStandardOutput = false;
                break;
            case "stderr":
                startInfo.RedirectStandardError = false;
                break;
        }

        await Assert.ThrowsAsync<ArgumentException>(() => ProcessRunner.RunAsync(startInfo, string.Empty));
    }

    [Fact]
    public async Task RunWhenArgumentsIsAConcatenatedStringRejectsTheStartInfo()
    {
        ProcessStartInfo startInfo = new ProcessStartInfo("sqlite3")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            Arguments = "--version"
        };

        ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(() => ProcessRunner.RunAsync(startInfo, string.Empty));
        Assert.Equal("startInfo", exception.ParamName);
    }

    [Fact]
    public async Task RunWhenProcessCannotStartPropagatesTheStartupFailure()
    {
        string missingExecutable = Path.Combine(
            Path.GetTempPath(),
            $"kyber-weave-missing-{Guid.NewGuid():N}");
        ProcessStartInfo startInfo = new ProcessStartInfo(missingExecutable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        await Assert.ThrowsAsync<Win32Exception>(() => ProcessRunner.RunAsync(startInfo, string.Empty));
    }

    [Fact]
    public async Task RunWhenChildClosesStdinPropagatesTheWriteFailure()
    {
        if (OperatingSystem.IsWindows())
        {
            throw SkipException.ForSkip("POSIX shell process execution tests are not run on Windows.");
        }

        ProcessStartInfo startInfo = CreateShellStartInfo("exec 0<&-; exit 0");
        string input = new string('i', BytesPerStream * 4);

        await Assert.ThrowsAnyAsync<IOException>(() =>
            ProcessRunner.RunAsync(startInfo, input).WaitAsync(TimeSpan.FromSeconds(TimeoutSeconds)));
    }

    [Fact]
    public async Task RunWhenTheChildExceedsTheTimeoutThrowsTimeoutException()
    {
        if (OperatingSystem.IsWindows())
        {
            throw SkipException.ForSkip("POSIX shell process execution tests are not run on Windows.");
        }

        ProcessStartInfo startInfo = CreateShellStartInfo("sleep 2");

        await Assert.ThrowsAsync<TimeoutException>(() =>
            ProcessRunner.RunAsync(startInfo, string.Empty, TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public async Task RunWhenOutputExceedsLimitThrowsInvalidOperationExceptionAndKillsChild()
    {
        if (OperatingSystem.IsWindows())
        {
            throw SkipException.ForSkip("POSIX shell process execution tests are not run on Windows.");
        }

        ProcessStartInfo startInfo = CreateShellStartInfo("head -c 2000 /dev/zero | tr '\\0' 'a'");

        OutputLimitExceededException ex = await Assert.ThrowsAsync<OutputLimitExceededException>(() =>
            ProcessRunner.RunAsync(startInfo, string.Empty, TimeSpan.FromSeconds(TimeoutSeconds), maxOutputBytes: 1000));

        Assert.Contains("exceeded the limit", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
