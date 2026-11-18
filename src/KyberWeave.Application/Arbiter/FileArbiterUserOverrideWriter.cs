using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Arbiter;

namespace KyberWeave.Data.Arbiter;

/// <summary>Writes the per-user Arbiter override file under the operator's home directory.</summary>
/// <param name="fileSystem">The file system the override is written to.</param>
public sealed class FileArbiterUserOverrideWriter(IWritableFileSystem fileSystem) : IArbiterUserOverrideWriter
{
    /// <inheritdoc />
    public string Write(string homeDirectory, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string path = ArbiterUserSettings.GetPath(homeDirectory);
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            fileSystem.CreateDirectory(directory);
        fileSystem.WriteAllText(path, text);
        return path;
    }
}
