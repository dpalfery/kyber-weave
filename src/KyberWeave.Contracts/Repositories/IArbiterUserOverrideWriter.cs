namespace KyberWeave.Contracts.Repositories;

/// <summary>Writes the per-user Arbiter provider override that <see cref="IArbiterUserSettings"/> reads.</summary>
public interface IArbiterUserOverrideWriter
{
    /// <summary>Writes the override under the home directory, creating its folder.</summary>
    /// <param name="homeDirectory">The operator's home directory.</param>
    /// <param name="text">The file's text.</param>
    /// <returns>The path written.</returns>
    /// <exception cref="IOException">The file or its folder could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The home directory is not writable.</exception>
    string Write(string homeDirectory, string text);
}
