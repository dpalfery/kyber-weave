using System.ComponentModel;
using System.Diagnostics;
using KyberWeave.Core.Docs.Analysis.Persistence;
using KyberWeave.Core.Processes;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>The cache-safety check over an in-memory checkout and a scripted git.</summary>
public sealed class AnalysisCacheSafetyTests
{
    private const string Root = "/work/repo";
    private const string IgnoreFile = "/work/repo/.kyber-weave/.gitignore";

    private readonly InMemoryFileSystem _files = new();

    [Fact]
    public void ACacheEntryInTheIgnoreFileWithNothingTrackedIsSafe()
    {
        WriteIgnore("cache/");
        ScriptedProcessExecutor git = new(_ => Result(0, ""));

        Assert.True(Safety(git).IsSafe(Root));

        ProcessStartInfo call = Assert.Single(git.Calls);
        Assert.Equal("git", call.FileName);
        Assert.Equal(Root, call.WorkingDirectory);
        Assert.Equal(["ls-files", "--cached", "--", ".kyber-weave/cache"], call.ArgumentList);
        Assert.Equal(TimeSpan.FromSeconds(60), Assert.Single(git.Timeouts));
    }

    [Fact]
    public void AMissingIgnoreFileIsUnsafeAndGitIsNeverAsked()
    {
        ScriptedProcessExecutor git = new(_ => Result(0, ""));

        Assert.False(Safety(git).IsSafe(Root));

        Assert.Empty(git.Calls);
    }

    [Fact]
    public void AnIgnoreFileWithoutTheCacheEntryIsUnsafe()
    {
        WriteIgnore("logs/");
        ScriptedProcessExecutor git = new(_ => Result(0, ""));

        Assert.False(Safety(git).IsSafe(Root));
    }

    [Fact]
    public void ALaterNegationOfTheDatabaseWithdrawsTheProtection()
    {
        WriteIgnore("cache/\n!cache/docs-analysis.sqlite3");
        ScriptedProcessExecutor git = new(_ => Result(0, ""));

        Assert.False(Safety(git).IsSafe(Root));
    }

    [Fact]
    public void ATrackedFileUnderTheCacheMakesItUnsafe()
    {
        WriteIgnore("cache/");
        ScriptedProcessExecutor git = new(_ => Result(0, ".kyber-weave/cache/docs-analysis.sqlite3\n"));

        Assert.False(Safety(git).IsSafe(Root));
    }

    [Theory]
    [InlineData(typeof(Win32Exception))]
    [InlineData(typeof(TimeoutException))]
    public void AGitThatCannotAnswerLeavesTheCacheUnproven(Type failure)
    {
        WriteIgnore("cache/");
        ScriptedProcessExecutor git = new(_ => throw (Exception)Activator.CreateInstance(failure)!);

        Assert.False(Safety(git).IsSafe(Root));
    }

    [Fact]
    public void ARelativeRootIsResolvedAgainstTheWorkingDirectory()
    {
        WriteIgnore("cache/");
        ScriptedProcessExecutor git = new(_ => Result(0, ""));

        Assert.True(Safety(git).IsSafe("repo"));
    }

    private AnalysisCacheSafety Safety(ScriptedProcessExecutor git) =>
        new(_files, new FakePathResolver(_files), new FakeProcessEnvironment(currentDirectory: "/work"), git);

    private void WriteIgnore(string text)
    {
        _files.CreateDirectory("/work/repo/.kyber-weave");
        _files.WriteAllText(IgnoreFile, text);
    }

    private static ProcessResult Result(int exitCode, string output) => new(exitCode, output, string.Empty);
}
