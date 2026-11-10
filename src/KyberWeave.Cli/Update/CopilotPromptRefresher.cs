namespace KyberWeave.Cli.Update;

/// <summary>
/// Rewrites the installed Copilot system-prompt file during an update, so the file tracks the
/// prompt the new binary ships.
/// </summary>
internal sealed class CopilotPromptRefresher
{
    /// <summary>Creates a refresher for one home directory.</summary>
    /// <param name="homeDirectory">The user's home directory.</param>
    /// <param name="log">Receives one line per warning; null to stay silent.</param>
    internal CopilotPromptRefresher(string homeDirectory, Action<string>? log = null)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Rewrites the installed prompt file when its content differs from the embedded text.
    /// </summary>
    /// <returns>True when a file was rewritten; false when it was already current or not installed.</returns>
    internal bool Refresh()
    {
        throw new NotImplementedException();
    }
}
