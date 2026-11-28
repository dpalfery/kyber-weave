using System.Diagnostics;
using KyberWeave.Core.Processes;
using Xunit;

namespace KyberWeave.Integration.Tests;

/// <summary>The built <c>kyber-weave</c> program renders its root help: the host starts and prints without a style error.</summary>
public sealed class CliRealHostHelpTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task TheRealHostRendersTheRootHelp()
    {
        ProcessResult result = await RunCli(["--help"]);
        string output = result.StandardOutput + "\n" + result.StandardError;

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("Could not find color or style", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Error:", output, StringComparison.Ordinal);
    }

    private static async Task<ProcessResult> RunCli(string[] args)
    {
        string host = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "kyber-weave.exe" : "kyber-weave");
        Assert.True(File.Exists(host), $"The kyber-weave host '{host}' is missing from the test output directory.");
        ProcessStartInfo startInfo = new(host)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in args)
            startInfo.ArgumentList.Add(argument);

        return await ProcessRunner.RunAsync(startInfo, string.Empty, Timeout);
    }
}
