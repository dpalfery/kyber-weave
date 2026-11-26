namespace KyberWeave.Data.Update;

/// <summary>The adapter over the operating system's permission bits.</summary>
internal sealed class UnixUpdateFileModes : IUpdateFileModes
{
    /// <inheritdoc />
    public UnixFileMode? Get(string path) =>
        OperatingSystem.IsWindows() ? null : File.GetUnixFileMode(path);

    /// <inheritdoc />
    public void Set(string path, UnixFileMode mode)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, mode);
    }
}
