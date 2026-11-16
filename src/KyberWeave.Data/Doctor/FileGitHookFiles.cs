using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Data.Doctor;

/// <summary>Reads and writes git hook files on the real file system.</summary>
public sealed class FileGitHookFiles : IGitHookFiles
{
    /// <summary>The executable mode a new hook gets on Unix: readable and executable by everyone, writable by its owner.</summary>
    private const UnixFileMode ExecutableMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    /// <inheritdoc />
    public bool Exists(string path) => File.Exists(path);

    /// <inheritdoc />
    public bool IsExecutable(string path) =>
        OperatingSystem.IsWindows() || (File.GetUnixFileMode(path) & UnixFileMode.UserExecute) != 0;

    /// <inheritdoc />
    public string ReadAllText(string path) => File.ReadAllText(path);

    /// <inheritdoc />
    public void Write(string path, string text, bool makeExecutable)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new IOException($"'{path}' has no folder."));
        File.WriteAllText(path, text);

        if (makeExecutable && !OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, ExecutableMode);
    }

    /// <inheritdoc />
    public void Delete(string path) => File.Delete(path);
}
