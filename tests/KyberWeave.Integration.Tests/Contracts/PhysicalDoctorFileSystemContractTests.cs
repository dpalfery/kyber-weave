using KyberWeave.Data.Doctor;
using KyberWeave.Data.Hooks;
using KyberWeave.Kernel.FileSystem;
using KyberWeave.Tests.Doctor;

namespace KyberWeave.Integration.Tests.Contracts;

/// <summary>Runs the doctor file-system contract against the adapter over the real disk.</summary>
public sealed class PhysicalDoctorFileSystemContractTests : DoctorFileSystemContractTests
{
    private readonly PhysicalFileSystem _disk = new();

    /// <inheritdoc />
    protected override IDoctorFileSystem CreateFileSystem() => new PhysicalDoctorFileSystem();

    /// <inheritdoc />
    protected override void EnsureDirectory(string path) => _disk.CreateDirectory(path);

    /// <inheritdoc />
    protected override void WriteFile(string path, string text) => _disk.WriteAllText(path, text);

    /// <inheritdoc />
    protected override void CreateLink(string linkPath, string targetPath) => _disk.CreateFileLink(linkPath, targetPath);

    /// <inheritdoc />
    protected override void DeleteTree(string root) => _disk.DeleteTreeNoFollow(root);
}
