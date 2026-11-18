using KyberWeave.Contracts.Repositories;
using KyberWeave.Data.Hooks;
using KyberWeave.Tests.FileSystem;

namespace KyberWeave.Integration.Tests.Contracts;

/// <summary>Runs the file-system contract against the real disk.</summary>
public sealed class PhysicalFileSystemContractTests : FileSystemContractTests
{
    /// <inheritdoc />
    protected override IWritableFileSystem CreateFileSystem() => new PhysicalFileSystem();
}
