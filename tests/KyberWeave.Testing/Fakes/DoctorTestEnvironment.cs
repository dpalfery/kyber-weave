using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Tests;

/// <summary>A process environment for the doctor command: a chosen terminal state and a chosen environment reader.</summary>
/// <param name="terminal">Whether standard input is a terminal.</param>
/// <param name="read">Answers each environment variable.</param>
internal sealed class DoctorTestEnvironment(bool terminal, Func<string, string?> read) : IProcessEnvironment
{
    /// <inheritdoc />
    public string CurrentDirectory => Environment.CurrentDirectory;

    /// <inheritdoc />
    public string HomeDirectory => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <inheritdoc />
    public string TempDirectory => Path.GetTempPath();

    /// <inheritdoc />
    public bool IsInputRedirected => !terminal;

    /// <inheritdoc />
    public string? GetVariable(string name) => read(name);
}
