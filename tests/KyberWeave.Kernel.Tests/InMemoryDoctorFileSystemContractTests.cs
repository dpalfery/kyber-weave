using KyberWeave.Kernel.FileSystem;
using KyberWeave.Tests.Doctor;
using KyberWeave.Tests.FileSystem;

namespace KyberWeave.Tests;

/// <summary>Runs the doctor file-system contract against the adapter over the in-memory file system.</summary>
public sealed class InMemoryDoctorFileSystemContractTests : DoctorFileSystemContractTests
{
    private readonly InMemoryFileSystem _memory = new();

    /// <inheritdoc />
    protected override IDoctorFileSystem CreateFileSystem() => new InMemoryDoctorFileSystem(_memory);

    /// <inheritdoc />
    protected override void EnsureDirectory(string path) => _memory.CreateDirectory(path);

    /// <inheritdoc />
    protected override void WriteFile(string path, string text)
    {
        _memory.CreateDirectory(Path.GetDirectoryName(path)!);
        _memory.WriteAllText(path, text);
    }

    /// <inheritdoc />
    protected override void CreateLink(string linkPath, string targetPath) => _memory.CreateFileLink(linkPath, targetPath);

    /// <inheritdoc />
    protected override void DeleteTree(string root)
    {
        // The in-memory file system holds no resources to release.
    }
}
