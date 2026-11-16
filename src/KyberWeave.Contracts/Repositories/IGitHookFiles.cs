namespace KyberWeave.Contracts.Repositories;

/// <summary>The files in a git hooks folder.</summary>
public interface IGitHookFiles
{
    /// <summary>Gets whether a hook file exists.</summary>
    /// <param name="path">The hook file.</param>
    /// <returns>True when the file exists.</returns>
    bool Exists(string path);

    /// <summary>Gets whether git can run a hook file.</summary>
    /// <param name="path">The hook file.</param>
    /// <returns>True when the owner may execute it, always true on Windows where there is no executable bit.</returns>
    bool IsExecutable(string path);

    /// <summary>Reads a hook file.</summary>
    /// <param name="path">The hook file.</param>
    /// <returns>The hook's text.</returns>
    string ReadAllText(string path);

    /// <summary>Writes a hook file, creating its folder first.</summary>
    /// <param name="path">The hook file.</param>
    /// <param name="text">The new text.</param>
    /// <param name="makeExecutable">Whether to give the file the executable mode. Ignored on Windows.</param>
    void Write(string path, string text, bool makeExecutable);

    /// <summary>Deletes a hook file.</summary>
    /// <param name="path">The hook file.</param>
    void Delete(string path);
}
