namespace KyberWeave.Core.CopilotPrompt;

/// <summary>
/// The Copilot system-prompt override file: the text Kyber-Weave ships, where it is installed,
/// and the check that a file on disk is usable.
/// </summary>
public static class CopilotPromptFile
{
    /// <summary>
    /// Returns the system-prompt YAML text that ships inside the Kyber-Weave binary.
    /// </summary>
    /// <returns>The embedded YAML text with a top-level <c>systemPrompt</c> key.</returns>
    public static string EmbeddedText()
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Returns the path where the system-prompt YAML file is installed for one user.
    /// </summary>
    /// <param name="homeDirectory">The user's home directory.</param>
    /// <returns>The absolute path <c>&lt;home&gt;/.kyber-weave/copilot/system-prompt.yaml</c>.</returns>
    public static string InstalledPath(string homeDirectory)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Checks that YAML text is a usable system-prompt file.
    /// </summary>
    /// <param name="yamlText">The YAML text to check.</param>
    /// <returns>Null when the text is valid; otherwise the reason it is not.</returns>
    public static string? Validate(string yamlText)
    {
        throw new NotImplementedException();
    }
}
