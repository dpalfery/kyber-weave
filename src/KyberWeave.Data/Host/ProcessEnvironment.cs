using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Data.Host;

/// <summary>Reports the real process: its environment variables and its well-known directories.</summary>
public sealed class ProcessEnvironment : IProcessEnvironment
{
    /// <inheritdoc />
    public string CurrentDirectory => Environment.CurrentDirectory;

    /// <inheritdoc />
    public string HomeDirectory => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <inheritdoc />
    public string TempDirectory => Path.GetTempPath();

    /// <inheritdoc />
    public string? GetVariable(string name) => Environment.GetEnvironmentVariable(name);
}
