using System.Diagnostics;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Core.Processes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The <c>serve</c> process owns stdout: stdio JSON-RPC frames go there, and one
/// stray line corrupts the session. A failure to start is therefore reported on
/// stderr, as a message, and the process exits 1 - never an unhandled exception
/// with its stack trace, and never a partial frame on stdout.
/// </summary>
/// <remarks>
/// The trigger is an argument the host passes straight to the generic host builder.
/// A short switch with no mapping (<c>-=x</c>) makes it throw while reading
/// configuration, which is a startup failure the serve path owns.
/// </remarks>
public sealed class ArbiterServeStartupFailureTests
{
    [Fact]
    public void Serve_AStartupFailure_ExitsOneWithAMessageOnStderrAndNothingOnStdout()
    {
        using TempDirectory repository = new();

        ProcessStartInfo startInfo = new("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = repository.Path,
        };
        startInfo.ArgumentList.Add(typeof(HookCommand).Assembly.Location);
        startInfo.ArgumentList.Add("serve");
        startInfo.ArgumentList.Add("--repo-root");
        startInfo.ArgumentList.Add(repository.Path);
        startInfo.ArgumentList.Add("-=x");

        ProcessResult result = ProcessRunner.Run(startInfo, string.Empty, TimeSpan.FromSeconds(60));

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Contains("kyber-weave-arbiter serve:", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void Serve_ARootThatIsNotADirectory_ExitsOneWithAMessageAndNothingOnStdout()
    {
        using TempDirectory repository = new();
        string missing = Path.Combine(repository.Path, "not-a-directory");

        ProcessStartInfo startInfo = new("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = repository.Path,
        };
        startInfo.ArgumentList.Add(typeof(HookCommand).Assembly.Location);
        startInfo.ArgumentList.Add("serve");
        startInfo.ArgumentList.Add("--repo-root");
        startInfo.ArgumentList.Add(missing);

        ProcessResult result = ProcessRunner.Run(startInfo, string.Empty, TimeSpan.FromSeconds(60));

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Contains("kyber-weave-arbiter serve:", result.StandardError, StringComparison.Ordinal);
        Assert.Contains(missing, result.StandardError, StringComparison.Ordinal);
    }
}
