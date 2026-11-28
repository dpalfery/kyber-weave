using System.ComponentModel;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.RunStart;
using KyberWeave.Data.RunStart;
using Xunit;

namespace KyberWeave.Tests.RunStart;

/// <summary>
/// The asynchronous branch reader must answer exactly what the synchronous one does: the same branch from the same
/// git answer, the same null on a detached head, the same exit-code mapping, and the same null when git cannot
/// answer. Each case runs the async member against a scripted git.
/// </summary>
public sealed class GitCheckedOutBranchReaderAsyncTests
{
    private const string Checkout = "/checkout";

    [Fact]
    public async Task ReadAsyncReturnsTheBranchGitReports()
    {
        ScriptedGit git = new((_, _) => new GitOutcome(0, "main\n", string.Empty));
        GitCheckedOutBranchReader reader = new(git);

        string? branch = await reader.ReadAsync(Checkout);

        Assert.Equal("main", branch);
        Assert.Equal(Checkout, git.LastWorkingDirectory);
        Assert.True(git.LastArguments?.SequenceEqual(["branch", "--show-current"], StringComparer.Ordinal) ?? false);
    }

    [Fact]
    public async Task ReadAsyncReturnsNullOnADetachedHead()
    {
        ScriptedGit git = new((_, _) => new GitOutcome(0, "\n", string.Empty));
        GitCheckedOutBranchReader reader = new(git);

        Assert.Null(await reader.ReadAsync(Checkout));
    }

    [Fact]
    public async Task ReadAsyncReturnsNullWhenGitFails()
    {
        ScriptedGit git = new((_, _) => new GitOutcome(128, string.Empty, "fatal: not a git repository"));
        GitCheckedOutBranchReader reader = new(git);

        Assert.Null(await reader.ReadAsync(Checkout));
    }

    [Fact]
    public async Task ReadAsyncReturnsNullWhenGitCannotStart()
    {
        ScriptedGit git = new((_, _) => throw new Win32Exception(2, "git not found"));
        GitCheckedOutBranchReader reader = new(git);

        Assert.Null(await reader.ReadAsync(Checkout));
    }

    [Fact]
    public async Task ReadAsyncReturnsNullWhenGitOutlivesItsLimit()
    {
        ScriptedGit git = new((_, _) => throw new TimeoutException("The process did not exit in time."));
        GitCheckedOutBranchReader reader = new(git);

        Assert.Null(await reader.ReadAsync(Checkout));
    }

    [Fact]
    public async Task ReadAsyncPassesTheCallerTokenThrough()
    {
        ScriptedGit git = new((_, _) => new GitOutcome(0, "main", string.Empty));
        GitCheckedOutBranchReader reader = new(git);
        using CancellationTokenSource source = new();

        _ = await reader.ReadAsync(Checkout, source.Token);

        Assert.Equal(source.Token, git.LastToken);
    }

    [Fact]
    public async Task IsAncestorAsyncAnswersTrueWhenGitExitsZero()
    {
        ScriptedGit git = new((_, _) => new GitOutcome(0, string.Empty, string.Empty));
        GitCheckedOutBranchReader reader = new(git);

        Assert.Equal(true, await reader.IsAncestorAsync(Checkout, "main", "feature"));
    }

    [Fact]
    public async Task IsAncestorAsyncAnswersFalseWhenGitExitsOne()
    {
        ScriptedGit git = new((_, _) => new GitOutcome(1, string.Empty, string.Empty));
        GitCheckedOutBranchReader reader = new(git);

        Assert.Equal(false, await reader.IsAncestorAsync(Checkout, "main", "feature"));
    }

    [Fact]
    public async Task IsAncestorAsyncAnswersNullWhenGitCannotAnswer()
    {
        ScriptedGit git = new((_, _) => new GitOutcome(128, string.Empty, "fatal: not a git repository"));
        GitCheckedOutBranchReader reader = new(git);

        Assert.Null(await reader.IsAncestorAsync(Checkout, "main", "feature"));
    }

    [Fact]
    public async Task IsAncestorAsyncReturnsNullWhenGitCannotStart()
    {
        ScriptedGit git = new((_, _) => throw new Win32Exception(2, "git not found"));
        GitCheckedOutBranchReader reader = new(git);

        Assert.Null(await reader.IsAncestorAsync(Checkout, "main", "feature"));
    }

    private sealed class ScriptedGit : IGitCommandRunner
    {
        private readonly Func<string, IReadOnlyList<string>, GitOutcome> _respond;

        public ScriptedGit(Func<string, IReadOnlyList<string>, GitOutcome> respond)
        {
            ArgumentNullException.ThrowIfNull(respond);
            _respond = respond;
        }

        public string? LastWorkingDirectory { get; private set; }

        public IReadOnlyList<string>? LastArguments { get; private set; }

        public CancellationToken LastToken { get; private set; }

        public GitOutcome Run(string workingDirectory, IReadOnlyList<string> arguments) =>
            throw new NotSupportedException();

        public Task<GitOutcome> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            LastWorkingDirectory = workingDirectory;
            LastArguments = arguments;
            LastToken = cancellationToken;
            return Task.FromResult(_respond(workingDirectory, arguments));
        }
    }
}
