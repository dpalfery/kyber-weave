using System.ComponentModel;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.CodeGraph;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>The staleness rule over a scripted git and an in-memory checkout: no disk, no git.</summary>
public sealed class CodeGraphIndexStalenessTests
{
    private const string Head = "1111111111111111111111111111111111111111";
    private const string Built = "2222222222222222222222222222222222222222";

    private static readonly string Root = Path.Combine(OperatingSystem.IsWindows() ? @"C:\" : "/", "repo");

    private static readonly DateTimeOffset IndexedAt = new(2026, 11, 18, 9, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(IndexedAt);
    private readonly InMemoryFileSystem _files;
    private readonly ScriptedProcessRunner _git = new();

    public CodeGraphIndexStalenessTests()
    {
        _files = new InMemoryFileSystem(_time);
        _files.CreateDirectory(Path.Combine(Root, "src"));
    }

    private CodeGraphIndexStaleness Rule => new(_files, _git);

    private static IReadOnlyDictionary<string, (long ModifiedMilliseconds, int NodeCount)> Indexed(params string[] paths) =>
        paths.ToDictionary(
            static path => path,
            static _ => (IndexedAt.ToUnixTimeMilliseconds(), 4),
            StringComparer.Ordinal);

    private void GitAnswers(string head, string? differing, string untracked = "")
    {
        _git.Script("git", Ok(head + "\n"), "rev-parse", "HEAD");
        _git.Script(
            "git",
            differing is null ? new ProcessRunResult(128, "fatal", TimedOut: false) { StandardOutput = "fatal", StandardError = "fatal" } : Ok(differing),
            "diff", "--name-only", "--no-renames", Built);
        _git.Script("git", Ok(untracked), "ls-files", "--others", "--exclude-standard");
    }

    private static ProcessRunResult Ok(string output) =>
        new(0, output, TimedOut: false) { StandardOutput = output, StandardError = string.Empty };

    private void WriteSource(string relative)
    {
        _files.WriteAllText(Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar)), "class A {}");
    }

    [Fact]
    public void NoIndexedCode_SaysNothingAndNeverAsksGit()
    {
        string? description = Rule.Describe(Root, Built, new Dictionary<string, (long, int)> { ["docs/a.md"] = (0, 0) });

        Assert.Null(description);
        Assert.Empty(_git.Calls);
    }

    [Fact]
    public void GitCannotAnswer_SaysNothing()
    {
        _git.Script("git", new ProcessRunResult(128, "fatal", TimedOut: false) { StandardOutput = string.Empty, StandardError = "fatal" }, "rev-parse", "HEAD");

        Assert.Null(Rule.Describe(Root, Built, Indexed("src/A.cs")));
    }

    [Fact]
    public void GitMissing_SaysNothing()
    {
        _git.Throws("git", new Win32Exception(2, "not found"));

        Assert.Null(Rule.Describe(Root, Built, Indexed("src/A.cs")));
    }

    [Fact]
    public void GitTimesOut_SaysNothing()
    {
        _git.ScriptTimeout("git", "rev-parse", "HEAD");

        Assert.Null(Rule.Describe(Root, Built, Indexed("src/A.cs")));
    }

    [Fact]
    public void BaselineCommitMissingFromCheckout_ReportsIt()
    {
        GitAnswers(Head, differing: null);

        string? description = Rule.Describe(Root, Built, Indexed("src/A.cs"));

        Assert.Equal("the index was built at commit 22222222, which this checkout does not contain", description);
    }

    [Fact]
    public void FileWrittenAfterTheIndexSawIt_IsCounted()
    {
        WriteSource("src/A.cs");
        _time.Advance(TimeSpan.FromMinutes(5));
        WriteSource("src/A.cs");
        GitAnswers(Head, "src/A.cs\n");

        string? description = Rule.Describe(Root, Built, Indexed("src/A.cs"));

        Assert.Equal("the index was built at 22222222 and HEAD is 11111111, and 1 source file(s) changed since (for example src/A.cs)", description);
    }

    [Fact]
    public void FileAlreadyDirtyWhenIndexed_IsNotCounted()
    {
        WriteSource("src/A.cs");
        GitAnswers(Head, "src/A.cs\n");

        Assert.Null(Rule.Describe(Root, Built, Indexed("src/A.cs")));
    }

    [Fact]
    public void FileDeletedSinceTheIndex_IsCounted()
    {
        GitAnswers(Head, "src/A.cs\n");

        string? description = Rule.Describe(Root, Built, Indexed("src/A.cs"));

        Assert.Contains("1 source file(s) changed since", description, StringComparison.Ordinal);
    }

    [Fact]
    public void NewUntrackedFileUnderIndexedCode_IsCountedAndOneElsewhereIsNot()
    {
        _files.CreateDirectory(Path.Combine(Root, "vendor"));
        WriteSource("src/New.cs");
        WriteSource("vendor/Lib.cs");
        GitAnswers(Built, string.Empty, untracked: "src/New.cs\nvendor/Lib.cs\n");

        string? description = Rule.Describe(Root, Built, Indexed("src/A.cs"));

        Assert.Equal("the index was built at the current commit 22222222, and 1 source file(s) changed since (for example src/New.cs)", description);
    }

    [Fact]
    public void ChangedFilesOfAKindThatYieldedNoSymbols_AreIgnored()
    {
        WriteSource("src/readme.md");
        GitAnswers(Head, "src/readme.md\n");

        Assert.Null(Rule.Describe(Root, Built, Indexed("src/A.cs")));
    }

    [Fact]
    public void GitIsRunInTheCheckoutWithABound()
    {
        GitAnswers(Head, string.Empty);

        _ = Rule.Describe(Root, Built, Indexed("src/A.cs"));

        Assert.All(_git.Calls, call =>
        {
            Assert.Equal(Root, call.WorkingDirectory);
            Assert.NotNull(call.Timeout);
        });
    }
}
