using KyberWeave.Contracts.Host;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Domain.CopilotPrompt;

namespace KyberWeave.Application.Update;

/// <summary>
/// Rewrites the installed Copilot system-prompt file during an update, so the file tracks the
/// prompt the new binary ships.
/// </summary>
/// <remarks>
/// Only a file the person already installed is refreshed. An absent file is never created here, because creating it
/// changes every Copilot chat on the machine and that decision belongs to <c>squad copilot-prompt install</c>.
/// A read-only file is the person's own choice and is left alone, with a warning, rather than replaced.
/// </remarks>
/// <param name="files">The file system the installed prompt is read and rewritten through.</param>
/// <param name="resources">Where the prompt text this binary ships is read.</param>
internal sealed class CopilotPromptRefresher(IWritableFileSystem files, IEmbeddedResources resources)
{
    /// <summary>
    /// Rewrites the installed prompt file when its content differs from the embedded text.
    /// </summary>
    /// <param name="homeDirectory">The user's home directory.</param>
    /// <param name="log">Receives one line per warning; null to stay silent.</param>
    /// <returns>True when a file was rewritten; false when it was already current or not installed.</returns>
    internal bool Refresh(string homeDirectory, Action<string>? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);
        string path = CopilotPromptFile.InstalledPath(homeDirectory);
        if (!files.FileExists(path))
            return false;

        string embedded = CopilotPromptFile.EmbeddedText(resources);
        try
        {
            if (string.Equals(files.ReadAllText(path), embedded, StringComparison.Ordinal))
                return false;

            if ((files.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
            {
                log?.Invoke($"the installed Copilot prompt file at {path} is read-only, so it was not refreshed");
                return false;
            }

            files.WriteAllTextAtomic(path, embedded);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            log?.Invoke($"the installed Copilot prompt file at {path} was not refreshed: {ex.Message}");
            return false;
        }
    }
}
