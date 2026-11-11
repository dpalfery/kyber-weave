using KyberWeave.Core.CopilotPrompt;
using KyberWeave.Core.FileSystem;

namespace KyberWeave.Cli.Update;

/// <summary>
/// Rewrites the installed Copilot system-prompt file during an update, so the file tracks the
/// prompt the new binary ships.
/// </summary>
/// <remarks>
/// Only a file the person already installed is refreshed. An absent file is never created here, because creating it
/// changes every Copilot chat on the machine and that decision belongs to <c>squad copilot-prompt install</c>.
/// A read-only file is the person's own choice and is left alone, with a warning, rather than replaced.
/// </remarks>
internal sealed class CopilotPromptRefresher
{
    private readonly string _homeDirectory;
    private readonly Action<string>? _log;

    /// <summary>Creates a refresher for one home directory.</summary>
    /// <param name="homeDirectory">The user's home directory.</param>
    /// <param name="log">Receives one line per warning; null to stay silent.</param>
    internal CopilotPromptRefresher(string homeDirectory, Action<string>? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);
        _homeDirectory = homeDirectory;
        _log = log;
    }

    /// <summary>
    /// Rewrites the installed prompt file when its content differs from the embedded text.
    /// </summary>
    /// <returns>True when a file was rewritten; false when it was already current or not installed.</returns>
    internal bool Refresh()
    {
        string path = CopilotPromptFile.InstalledPath(_homeDirectory);
        if (!File.Exists(path))
            return false;

        string embedded = CopilotPromptFile.EmbeddedText();
        try
        {
            if (string.Equals(File.ReadAllText(path), embedded, StringComparison.Ordinal))
                return false;

            if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
            {
                _log?.Invoke($"the installed Copilot prompt file at {path} is read-only, so it was not refreshed");
                return false;
            }

            AtomicFile.WriteAllText(path, embedded);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _log?.Invoke($"the installed Copilot prompt file at {path} was not refreshed: {ex.Message}");
            return false;
        }
    }
}
