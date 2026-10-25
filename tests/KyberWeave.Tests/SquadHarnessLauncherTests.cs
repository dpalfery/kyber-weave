using System.Diagnostics;
using KyberWeave.Core.Squad.Deployment;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
///     Unit tests for <see cref="SquadHarnessLauncher" />.
///     Pins pure start-info generation, path resolution, environment variable injection,
///     Windows .cmd/.bat ComSpec wrapping, argument quoting, and dangerous character rejection.
/// </summary>
public sealed class SquadHarnessLauncherTests
{
    private static readonly SquadInstanceRow ClaudeRow = SquadInstanceCatalog.GetRow(SquadTarget.Claude);
    private static readonly SquadInstanceRow CodexRow = SquadInstanceCatalog.GetRow(SquadTarget.Codex);

    [Fact]
    public void FindsExecutableThroughPathAndSetsFileName()
    {
        string expectedPath = "/usr/local/bin/claude";
        string instanceRoot = "/tmp/instances/claude/fast";
        string[] args = ["--version"];

        ProcessStartInfo startInfo = SquadHarnessLauncher.BuildStartInfo(
            ClaudeRow,
            instanceRoot,
            args,
            _ => null,
            name => string.Equals(name, "claude", StringComparison.Ordinal) ? expectedPath : null,
            false);

        Assert.Equal(expectedPath, startInfo.FileName);
    }

    [Fact]
    public void ExecutableMissingFromPathThrowsFileNotFoundExceptionNamingExecutable()
    {
        string instanceRoot = "/tmp/instances/claude/fast";
        string[] args = ["--version"];

        FileNotFoundException ex = Assert.Throws<FileNotFoundException>(() =>
            SquadHarnessLauncher.BuildStartInfo(
                ClaudeRow,
                instanceRoot,
                args,
                _ => null,
                _ => null,
                false));

        Assert.Contains(ClaudeRow.Executable, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SetsInstanceRootToConfiguredRowVariable()
    {
        string instanceRoot = "/tmp/instances/claude/fast";
        string[] args = [];

        ProcessStartInfo startInfo = SquadHarnessLauncher.BuildStartInfo(
            ClaudeRow,
            instanceRoot,
            args,
            _ => null,
            _ => "/usr/local/bin/claude",
            false);

        Assert.NotNull(ClaudeRow.Variable);
        Assert.True(startInfo.Environment.ContainsKey(ClaudeRow.Variable));
        Assert.Equal(instanceRoot, startInfo.Environment[ClaudeRow.Variable]);
    }

    [Fact]
    public void NonWindowsUsesProcessArgumentList()
    {
        string instanceRoot = "/tmp/instances/codex/fast";
        string[] args = ["--model", "gpt-4o", "--sandbox", "project"];

        ProcessStartInfo startInfo = SquadHarnessLauncher.BuildStartInfo(
            CodexRow,
            instanceRoot,
            args,
            _ => null,
            _ => "/usr/bin/codex",
            false);

        Assert.Equal("/usr/bin/codex", startInfo.FileName);
        Assert.Equal(4, startInfo.ArgumentList.Count);
        Assert.Equal("--model", startInfo.ArgumentList[0]);
        Assert.Equal("gpt-4o", startInfo.ArgumentList[1]);
        Assert.Equal("--sandbox", startInfo.ArgumentList[2]);
        Assert.Equal("project", startInfo.ArgumentList[3]);
        Assert.True(string.IsNullOrEmpty(startInfo.Arguments));
    }

    [Fact]
    public void WindowsBinaryExeUsesProcessArgumentList()
    {
        string instanceRoot = @"C:\Users\tester\.kyber-weave\squad\instances\claude\fast";
        string exePath = @"C:\Program Files\Claude\claude.exe";
        string[] args = ["--verbose", "run"];

        ProcessStartInfo startInfo = SquadHarnessLauncher.BuildStartInfo(
            ClaudeRow,
            instanceRoot,
            args,
            key => string.Equals(key, "ComSpec", StringComparison.OrdinalIgnoreCase)
                ? @"C:\Windows\System32\cmd.exe"
                : null,
            _ => exePath,
            true);

        Assert.Equal(exePath, startInfo.FileName);
        Assert.Equal(2, startInfo.ArgumentList.Count);
        Assert.Equal("--verbose", startInfo.ArgumentList[0]);
        Assert.Equal("run", startInfo.ArgumentList[1]);
        Assert.True(string.IsNullOrEmpty(startInfo.Arguments));
    }

    [Fact]
    public void WindowsCmdScriptUsesComSpecWithQuotedArguments()
    {
        string instanceRoot = @"C:\Users\tester\.kyber-weave\squad\instances\claude\fast";
        string shimPath = @"C:\Users\tester\AppData\Roaming\npm\claude.cmd";
        string comSpec = @"C:\Windows\System32\cmd.exe";
        string[] args = ["run", "my project"];

        ProcessStartInfo startInfo = SquadHarnessLauncher.BuildStartInfo(
            ClaudeRow,
            instanceRoot,
            args,
            key => string.Equals(key, "ComSpec", StringComparison.OrdinalIgnoreCase) ? comSpec : null,
            _ => shimPath,
            true);

        Assert.Equal(comSpec, startInfo.FileName);
        string expectedArguments = $"/d /s /c \"\"{shimPath}\" \"run\" \"my project\"\"";
        Assert.Equal(expectedArguments, startInfo.Arguments);
        Assert.Empty(startInfo.ArgumentList);
    }

    [Fact]
    public void WindowsBatScriptUsesComSpecWithQuotedArguments()
    {
        string instanceRoot = @"C:\Users\tester\.kyber-weave\squad\instances\claude\fast";
        string shimPath = @"C:\Tools\claude.bat";
        string comSpec = @"C:\Windows\System32\cmd.exe";
        string[] args = ["start"];

        ProcessStartInfo startInfo = SquadHarnessLauncher.BuildStartInfo(
            ClaudeRow,
            instanceRoot,
            args,
            key => string.Equals(key, "ComSpec", StringComparison.OrdinalIgnoreCase) ? comSpec : null,
            _ => shimPath,
            true);

        Assert.Equal(comSpec, startInfo.FileName);
        string expectedArguments = $"/d /s /c \"\"{shimPath}\" \"start\"\"";
        Assert.Equal(expectedArguments, startInfo.Arguments);
    }

    [Fact]
    public void WindowsCmdScriptFallsBackToDefaultCmdExeWhenComSpecUnset()
    {
        string instanceRoot = @"C:\Users\tester\.kyber-weave\squad\instances\claude\fast";
        string shimPath = @"C:\Users\tester\AppData\Roaming\npm\claude.cmd";
        string[] args = ["run"];

        ProcessStartInfo startInfo = SquadHarnessLauncher.BuildStartInfo(
            ClaudeRow,
            instanceRoot,
            args,
            _ => null,
            _ => shimPath,
            true);

        Assert.Equal("cmd.exe", startInfo.FileName);
        string expectedArguments = $"/d /s /c \"\"{shimPath}\" \"run\"\"";
        Assert.Equal(expectedArguments, startInfo.Arguments);
    }

    [Fact]
    public void ArgumentContainingDoubleQuoteThrowsArgumentExceptionNamingOffendingArgument()
    {
        string instanceRoot = "/tmp/instances/claude/fast";
        string offending = "bad\"arg";
        string[] args = ["--safe", offending];

        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            SquadHarnessLauncher.BuildStartInfo(
                ClaudeRow,
                instanceRoot,
                args,
                _ => null,
                _ => "/bin/claude",
                false));

        Assert.Contains(offending, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ArgumentContainingPercentThrowsArgumentExceptionNamingOffendingArgument()
    {
        string instanceRoot = "/tmp/instances/claude/fast";
        string offending = "%ENV_VAR%";
        string[] args = [offending];

        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            SquadHarnessLauncher.BuildStartInfo(
                ClaudeRow,
                instanceRoot,
                args,
                _ => null,
                _ => "/bin/claude",
                false));

        Assert.Contains(offending, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ArgumentContainingExclamationThrowsArgumentExceptionNamingOffendingArgument()
    {
        string instanceRoot = "/tmp/instances/claude/fast";
        string offending = "!delayed!";
        string[] args = [offending];

        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            SquadHarnessLauncher.BuildStartInfo(
                ClaudeRow,
                instanceRoot,
                args,
                _ => null,
                _ => "/bin/claude",
                false));

        Assert.Contains(offending, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ArgumentContainingCaretThrowsArgumentExceptionNamingOffendingArgument()
    {
        string instanceRoot = "/tmp/instances/claude/fast";
        string offending = "escape^caret";
        string[] args = [offending];

        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            SquadHarnessLauncher.BuildStartInfo(
                ClaudeRow,
                instanceRoot,
                args,
                _ => null,
                _ => "/bin/claude",
                false));

        Assert.Contains(offending, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ArgumentContainingCarriageReturnThrowsArgumentExceptionNamingOffendingArgument()
    {
        string instanceRoot = "/tmp/instances/claude/fast";
        string offending = "first\rsecond";
        string[] args = [offending];

        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            SquadHarnessLauncher.BuildStartInfo(
                ClaudeRow,
                instanceRoot,
                args,
                _ => null,
                _ => "/bin/claude",
                false));

        Assert.Contains(offending, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ArgumentContainingLineFeedThrowsArgumentExceptionNamingOffendingArgument()
    {
        string instanceRoot = "/tmp/instances/claude/fast";
        string offending = "first\nsecond";
        string[] args = [offending];

        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            SquadHarnessLauncher.BuildStartInfo(
                ClaudeRow,
                instanceRoot,
                args,
                _ => null,
                _ => "/bin/claude",
                false));

        Assert.Contains(offending, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CleanArgumentsArePreservedWithoutModification()
    {
        string instanceRoot = "/tmp/instances/claude/fast";
        string[] cleanArgs = ["--opt=val", "plain text", "path/to/file", "-v"];

        ProcessStartInfo startInfo = SquadHarnessLauncher.BuildStartInfo(
            ClaudeRow,
            instanceRoot,
            cleanArgs,
            _ => null,
            _ => "/bin/claude",
            false);

        Assert.Equal(cleanArgs.Length, startInfo.ArgumentList.Count);
        for (int i = 0; i < cleanArgs.Length; i++) Assert.Equal(cleanArgs[i], startInfo.ArgumentList[i]);
    }
}
