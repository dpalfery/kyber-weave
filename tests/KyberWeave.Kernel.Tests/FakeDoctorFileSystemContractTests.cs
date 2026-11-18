using KyberWeave.Kernel.FileSystem;
using KyberWeave.Tests.Doctor;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>Runs the doctor file-system contract against the in-memory fake.</summary>
public sealed class FakeDoctorFileSystemContractTests : DoctorFileSystemContractTests
{
    private readonly FakeDoctorFileSystem _files = new();

    /// <inheritdoc />
    protected override IDoctorFileSystem CreateFileSystem() => _files;

    /// <inheritdoc />
    protected override void EnsureDirectory(string path) => _files.WithDirectory(path);

    /// <inheritdoc />
    protected override void WriteFile(string path, string text) => _files.WithFile(path, text);

    /// <inheritdoc />
    protected override void CreateLink(string linkPath, string targetPath) => _files.WithLink(linkPath, targetPath);

    /// <inheritdoc />
    protected override void DeleteTree(string root)
    {
        // The fake holds no resources to release.
    }

    [Fact]
    public void TheFakeStartsWithNothingATestDidNotPutThere()
    {
        Start();

        Assert.False(Files.FileExists(At("anything")));
        Assert.False(Files.DirectoryExists(At("anything")));
    }
}
