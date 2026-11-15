namespace KyberWeave.Contracts.Repositories;

/// <summary>The state of the running process that commands read: environment variables and the three well-known directories.</summary>
/// <remarks>
/// Code above the data layer asks this instead of calling <c>Environment</c> or <c>Directory.GetCurrentDirectory</c>,
/// so a test supplies its own values without touching the real process.
/// </remarks>
public interface IProcessEnvironment
{
    /// <summary>Gets the directory the process was started in.</summary>
    string CurrentDirectory { get; }

    /// <summary>Gets the operator's home directory, or an empty string when the platform reports none.</summary>
    string HomeDirectory { get; }

    /// <summary>Gets the directory for temporary files.</summary>
    string TempDirectory { get; }

    /// <summary>Reads an environment variable.</summary>
    /// <param name="name">The variable name.</param>
    /// <returns>The value, or null when the variable is not set.</returns>
    string? GetVariable(string name);
}
