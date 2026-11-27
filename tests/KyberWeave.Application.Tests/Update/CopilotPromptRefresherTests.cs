using KyberWeave.Domain.CopilotPrompt;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;
using KyberWeave.Application.Update;

namespace KyberWeave.Application.Tests.Update;

/// <summary>
/// The update-time refresh of the installed Copilot system-prompt file: an installed file tracks the embedded
/// text, an absent file is never created, and a file that cannot be written is logged, not thrown.
/// </summary>
public sealed class CopilotPromptRefresherTests
{
    private const string Home = "/home/person";
    private const string Shipped = "systemPrompt: |\n  Text this binary ships.\n";
    private const string Stale = "systemPrompt: |\n  Stale text from an older binary.\n";

    private readonly InMemoryFileSystem _files = new();
    private readonly List<string> _log = [];
    private readonly CopilotPromptRefresher _refresher;

    public CopilotPromptRefresherTests() =>
        _refresher = new CopilotPromptRefresher(
            _files,
            new FakeEmbeddedResources().Add("CopilotPrompt.system-prompt.yaml", Shipped));

    private static string PromptPath => CopilotPromptFile.InstalledPath(Home);

    private void Install(string text)
    {
        _files.CreateDirectory(Path.GetDirectoryName(PromptPath)!);
        _files.WriteAllText(PromptPath, text);
    }

    [Fact]
    public void AnInstalledStaleFileIsRewrittenWithTheShippedText()
    {
        Install(Stale);

        bool rewritten = _refresher.Refresh(Home, _log.Add);

        Assert.True(rewritten);
        Assert.Equal(Shipped, _files.ReadAllText(PromptPath));
        Assert.Empty(_log);
    }

    [Fact]
    public void AnInstalledCurrentFileIsLeftAlone()
    {
        Install(Shipped);

        Assert.False(_refresher.Refresh(Home, _log.Add));
        Assert.Equal(Shipped, _files.ReadAllText(PromptPath));
    }

    [Fact]
    public void AnAbsentFileIsNeverCreated()
    {
        bool rewritten = _refresher.Refresh(Home, _log.Add);

        Assert.False(rewritten);
        Assert.False(_files.PathExists(PromptPath));
        Assert.False(_files.DirectoryExists(Path.GetDirectoryName(PromptPath)!));
    }

    [Fact]
    public void AReadOnlyFileIsLeftAloneWithAWarning()
    {
        Install(Stale);
        _files.SetAttributes(PromptPath, FileAttributes.ReadOnly);

        bool rewritten = _refresher.Refresh(Home, _log.Add);

        Assert.False(rewritten);
        Assert.Equal(Stale, _files.ReadAllText(PromptPath));
        Assert.Contains("read-only", Assert.Single(_log), StringComparison.Ordinal);
    }

    [Fact]
    public void AWriteFailureIsLoggedNotThrown()
    {
        Install(Stale);
        _files.FailWhen(operation => operation.Name == nameof(InMemoryFileSystem.WriteAllTextAtomic), new IOException("disk full"));

        bool? rewritten = null;
        Exception? thrown = Record.Exception(() => rewritten = _refresher.Refresh(Home, _log.Add));

        Assert.Null(thrown);
        Assert.False(rewritten);
        Assert.Contains("disk full", Assert.Single(_log), StringComparison.Ordinal);
    }

    [Fact]
    public void ABlankHomeIsRefused() =>
        Assert.Throws<ArgumentException>(() => _refresher.Refresh(" ", _log.Add));
}
