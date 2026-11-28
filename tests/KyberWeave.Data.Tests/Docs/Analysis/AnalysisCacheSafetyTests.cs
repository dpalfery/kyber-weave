using System.ComponentModel;
using System.Diagnostics;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Docs.Analysis.Persistence;
using KyberWeave.Core.Processes;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>The cache-safety check over an in-memory checkout and a scripted git.</summary>
public sealed class AnalysisCacheSafetyTests
{
    private readonly InMemoryFileSystem _files = new();

    private string WorkingDirectory => Path.Combine(_files.Root, "work");

    private string Root => Path.Combine(WorkingDirectory, "repo");

    private string IgnoreFile => Path.Combine(Root, ".kyber-weave", ".gitignore");

    [Fact]
    public async Task ACacheEntryInTheIgnoreFileWithNothingTrackedIsSafe()
    {
        WriteIgnore("cache/");
        ScriptedProcessExecutor git = new(_ => Result(0, ""));

        Assert.True(await Safety(git).IsSafeAsync(Root));

        ProcessStartInfo call = Assert.Single(git.Calls);
        Assert.Equal("git", call.FileName);
        Assert.Equal(Root, call.WorkingDirectory);
        Assert.Equal(["ls-files", "--cached", "--", ".kyber-weave/cache"], call.ArgumentList);
        Assert.Equal(TimeSpan.FromSeconds(60), Assert.Single(git.Timeouts));
    }

    [Fact]
    public async Task AMissingIgnoreFileIsUnsafeAndGitIsNeverAsked()
    {
        ScriptedProcessExecutor git = new(_ => Result(0, ""));

        Assert.False(await Safety(git).IsSafeAsync(Root));

        Assert.Empty(git.Calls);
    }

    [Fact]
    public async Task AnIgnoreFileWithoutTheCacheEntryIsUnsafe()
    {
        WriteIgnore("logs/");
        ScriptedProcessExecutor git = new(_ => Result(0, ""));

        Assert.False(await Safety(git).IsSafeAsync(Root));
    }

    [Fact]
    public async Task ALaterNegationOfTheDatabaseWithdrawsTheProtection()
    {
        WriteIgnore("cache/\n!cache/docs-analysis.sqlite3");
        ScriptedProcessExecutor git = new(_ => Result(0, ""));

        Assert.False(await Safety(git).IsSafeAsync(Root));
    }

    [Fact]
    public async Task ATrackedFileUnderTheCacheMakesItUnsafe()
    {
        WriteIgnore("cache/");
        ScriptedProcessExecutor git = new(_ => Result(0, ".kyber-weave/cache/docs-analysis.sqlite3\n"));

        Assert.False(await Safety(git).IsSafeAsync(Root));
    }

    [Theory]
    [InlineData(typeof(Win32Exception))]
    [InlineData(typeof(TimeoutException))]
    public async Task AGitThatCannotAnswerLeavesTheCacheUnproven(Type failure)
    {
        WriteIgnore("cache/");
        ScriptedProcessExecutor git = new(_ => throw (Exception)Activator.CreateInstance(failure)!);

        Assert.False(await Safety(git).IsSafeAsync(Root));
    }

    [Fact]
    public async Task ARelativeRootIsResolvedAgainstTheWorkingDirectory()
    {
        WriteIgnore("cache/");
        ScriptedProcessExecutor git = new(_ => Result(0, ""));

        Assert.True(await Safety(git).IsSafeAsync("repo"));
    }

    [Fact]
    public async Task IsSafeAsyncReadsGitWithoutBlocking()
    {
        WriteIgnore("cache/");
        ScriptedProcessRunner git = new();
        _ = git.Script(
            "git",
            new ProcessRunResult(0, string.Empty, TimedOut: false) { StandardOutput = string.Empty, StandardError = string.Empty },
            "ls-files", "--cached", "--", ".kyber-weave/cache");

        Assert.True(await SafetyAsync(git).IsSafeAsync(Root));
        Assert.Single(git.Calls);
    }

    [Fact]
    public async Task IsSafeAsyncIsUnsafeWhenTheTrackerNamesTheCache()
    {
        WriteIgnore("cache/");
        ScriptedProcessRunner git = new();
        _ = git.Script(
            "git",
            new ProcessRunResult(0, string.Empty, TimedOut: false)
            {
                StandardOutput = ".kyber-weave/cache/docs-analysis.sqlite3\n",
                StandardError = string.Empty
            },
            "ls-files", "--cached", "--", ".kyber-weave/cache");

        Assert.False(await SafetyAsync(git).IsSafeAsync(Root));
    }

    [Fact]
    public async Task IsSafeAsyncLeavesTheCacheUnprovenWhenGitCannotAnswer()
    {
        WriteIgnore("cache/");
        ScriptedProcessRunner git = new();
        _ = git.Throws(
            "git",
            new TimeoutException(),
            "ls-files", "--cached", "--", ".kyber-weave/cache");

        Assert.False(await SafetyAsync(git).IsSafeAsync(Root));
    }

    private AnalysisCacheSafety Safety(ScriptedProcessExecutor git) =>
        new(_files, new FakePathResolver(_files), new FakeProcessEnvironment(currentDirectory: WorkingDirectory), git);

    private AnalysisCacheSafety SafetyAsync(ScriptedProcessRunner git) =>
        new(_files, new FakePathResolver(_files), new FakeProcessEnvironment(currentDirectory: WorkingDirectory), git);

    private void WriteIgnore(string text)
    {
        _files.CreateDirectory(Path.Combine(Root, ".kyber-weave"));
        _files.WriteAllText(IgnoreFile, text);
    }

    private static ProcessResult Result(int exitCode, string output) => new(exitCode, output, string.Empty);
}
