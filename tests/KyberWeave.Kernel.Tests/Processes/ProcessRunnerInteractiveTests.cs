using System.ComponentModel;
using System.Diagnostics;
using KyberWeave.Core.Processes;
using Xunit;

namespace KyberWeave.Tests.Processes;

/// <summary>
/// <c>ProcessRunner.RunInteractive</c> is the production <c>squad run</c> launch path: the child is wired straight to
/// the console, never through a shell, and the caller gets the child's own exit code.
/// </summary>
public sealed class ProcessRunnerInteractiveTests
{
    [Fact]
    public void TheChildsExitCodeIsReturned()
    {
        SkipUnlessPosix();
        ProcessStartInfo startInfo = new("/bin/sh");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("exit 3");

        int exitCode = ProcessRunner.RunInteractive(startInfo);

        Assert.Equal(3, exitCode);
    }

    [Fact]
    public void ARunThatSucceedsReturnsZero()
    {
        SkipUnlessPosix();
        ProcessStartInfo startInfo = new("/bin/sh");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("exit 0");

        Assert.Equal(0, ProcessRunner.RunInteractive(startInfo));
    }

    [Fact]
    public void TheStartInfoIsForcedOffTheShellAndOffRedirection()
    {
        SkipUnlessPosix();
        ProcessStartInfo startInfo = new("/bin/sh")
        {
            UseShellExecute = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("exit 0");

        _ = ProcessRunner.RunInteractive(startInfo);

        Assert.False(startInfo.UseShellExecute);
        Assert.False(startInfo.RedirectStandardInput);
        Assert.False(startInfo.RedirectStandardOutput);
        Assert.False(startInfo.RedirectStandardError);
    }

    [Fact]
    public void AProgramThatDoesNotExistIsAStartFailure()
    {
        ProcessStartInfo startInfo = new("kyber-weave-no-such-program-for-the-interactive-test");

        _ = Assert.Throws<Win32Exception>(() => ProcessRunner.RunInteractive(startInfo));
    }

    [Fact]
    public void ANullStartInfoIsRefused()
    {
        _ = Assert.Throws<ArgumentNullException>(() => ProcessRunner.RunInteractive(null!));
    }

    private static void SkipUnlessPosix()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            Assert.Skip("These cases start /bin/sh, which exists only on Linux and macOS.");
        }
    }
}
