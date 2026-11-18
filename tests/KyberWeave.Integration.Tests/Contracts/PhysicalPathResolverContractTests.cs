using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Data.Hooks;
using KyberWeave.Data.Paths;
using KyberWeave.Tests.Paths;
using Xunit;

namespace KyberWeave.Integration.Tests.Contracts;

/// <summary>Runs the path contract against the resolver over the real disk.</summary>
public sealed class PhysicalPathResolverContractTests : PathResolverContractTests
{
    [Fact]
    public void TheHostConstructorTakesItsCaseRuleFromTheVolume()
    {
        PhysicalFileSystem files = new();
        PhysicalPathResolver resolver = new(files);
        string root = files.CreateTemporaryDirectory("kw-path-host-");
        try
        {
            string name = Path.Combine(root, "Name.txt");
            string otherCase = Path.Combine(root, "nAME.txt");
            files.WriteAllText(name, "x");

            Assert.Equal(files.FileExists(otherCase), resolver.AreSame(name, otherCase));
        }
        finally
        {
            files.DeleteTreeNoFollow(root);
        }
    }

    /// <inheritdoc />
    protected override IWritableFileSystem CreateFileSystem() => new PhysicalFileSystem();

    /// <inheritdoc />
    protected override IPathResolver CreateResolver(IFileSystem files) =>
        new PhysicalPathResolver(files, caseSensitiveFileSystem: CaseSensitive);

    /// <inheritdoc />
    protected override bool CaseSensitive => OperatingSystem.IsLinux();
}
