using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Data.Hooks;
using KyberWeave.Data.Paths;
using KyberWeave.Tests.Paths;

namespace KyberWeave.Integration.Tests.Contracts;

/// <summary>Runs the path contract against the resolver over the real disk.</summary>
public sealed class PhysicalPathResolverContractTests : PathResolverContractTests
{
    /// <inheritdoc />
    protected override IWritableFileSystem CreateFileSystem() => new PhysicalFileSystem();

    /// <inheritdoc />
    protected override IPathResolver CreateResolver(IFileSystem files) =>
        new PhysicalPathResolver(files, caseSensitiveFileSystem: CaseSensitive);

    /// <inheritdoc />
    protected override bool CaseSensitive => OperatingSystem.IsLinux();
}
