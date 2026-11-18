using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Tests.FileSystem;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests.Paths;

/// <summary>Runs the path contract against the fake, once per case rule the fake file system offers.</summary>
public abstract class InMemoryPathResolverContractTests : PathResolverContractTests
{
    /// <inheritdoc />
    protected override IWritableFileSystem CreateFileSystem() => new InMemoryFileSystem(caseSensitive: CaseSensitive);

    /// <inheritdoc />
    protected override IPathResolver CreateResolver(IFileSystem files) => new FakePathResolver((InMemoryFileSystem)files);
}

/// <summary>Runs the path contract against the fake with APFS and NTFS name rules.</summary>
public sealed class InMemoryPathResolverCaseInsensitiveContractTests : InMemoryPathResolverContractTests
{
    /// <inheritdoc />
    protected override bool CaseSensitive => false;
}

/// <summary>Runs the path contract against the fake with Linux name rules.</summary>
public sealed class InMemoryPathResolverCaseSensitiveContractTests : InMemoryPathResolverContractTests
{
    /// <inheritdoc />
    protected override bool CaseSensitive => true;
}

/// <summary>Proves the answers a shared contract cannot state about the fake path resolver itself.</summary>
public sealed class FakePathResolverBehaviourTests
{
    [Fact]
    public void ItTakesItsCaseRuleFromTheInMemoryFileSystem()
    {
        FakePathResolver insensitive = FakePathResolver.OverEmptyFileSystem(caseSensitive: false);
        FakePathResolver sensitive = FakePathResolver.OverEmptyFileSystem(caseSensitive: true);
        string root = insensitive.Files.CreateTemporaryDirectory("kw-path-fake-");
        string rootCopy = sensitive.Files.CreateTemporaryDirectory("kw-path-fake-");

        Assert.True(insensitive.AreSame(root, root.ToUpperInvariant()));
        Assert.False(sensitive.AreSame(rootCopy, rootCopy.ToUpperInvariant()));
    }
}
