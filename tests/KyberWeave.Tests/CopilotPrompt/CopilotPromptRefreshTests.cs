using KyberWeave.Cli.Update;
using KyberWeave.Core.CopilotPrompt;
using Xunit;

namespace KyberWeave.Tests.CopilotPrompt;

/// <summary>
/// Pins the update-time refresh of the installed Copilot system-prompt file: an installed file tracks the
/// embedded text, an absent file is never created, and a file that cannot be written is logged, not thrown.
/// </summary>
public sealed class CopilotPromptRefreshTests : IDisposable
{
    private const string StaleText = "systemPrompt: |\n  Stale text from an older binary.\n";

    private readonly TempDirectory _home = new();
    private readonly List<string> _log = new();

    public void Dispose()
    {
        // A read-only fixture would block the temp directory's cleanup on Windows; restore it first.
        if (File.Exists(PromptPath))
            File.SetAttributes(PromptPath, FileAttributes.Normal);
        _home.Dispose();
    }

    private string PromptPath => CopilotPromptFile.InstalledPath(_home.Path);

    private void WritePrompt(string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PromptPath)!);
        File.WriteAllText(PromptPath, text);
    }

    [Fact]
    public void Refresh_InstalledFile_RewritesWithEmbeddedTextAndReturnsTrue()
    {
        WritePrompt(StaleText);
        CopilotPromptRefresher refresher = new(_home.Path, _log.Add);

        bool rewritten = refresher.Refresh();

        Assert.True(rewritten, "an installed prompt file that differs from the embedded text must be rewritten");
        Assert.Equal(CopilotPromptFile.EmbeddedText(), File.ReadAllText(PromptPath));
    }

    [Fact]
    public void Refresh_NotInstalled_CreatesNothingAndReturnsFalse()
    {
        string installDirectory = Path.Combine(_home.Path, ".kyber-weave");
        CopilotPromptRefresher refresher = new(_home.Path, _log.Add);

        bool rewritten = refresher.Refresh();

        Assert.False(rewritten);
        Assert.False(File.Exists(PromptPath), "an absent prompt file must not be created by an update");
        Assert.False(Directory.Exists(installDirectory), "an absent prompt file must not create its directory");
    }

    [Fact]
    public void Refresh_UnwritableFile_LogsAndReturnsFalseWithoutThrowing()
    {
        WritePrompt(StaleText);
        File.SetAttributes(PromptPath, FileAttributes.ReadOnly);
        CopilotPromptRefresher refresher = new(_home.Path, _log.Add);

        bool? rewritten = null;
        Exception? thrown = Record.Exception(() => rewritten = refresher.Refresh());

        Assert.Null(thrown);
        Assert.False(rewritten);
        Assert.NotEmpty(_log);
    }
}
