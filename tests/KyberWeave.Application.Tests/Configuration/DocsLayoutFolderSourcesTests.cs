using KyberWeave.Arbiter.Hooks;
using KyberWeave.Core.Configuration;

using Xunit;

namespace KyberWeave.Application.Tests.Configuration;

/// <summary>
/// The folder list in <see cref="DocsLayout"/> is the one source for the planning folders. These tests tie the
/// registry the configuration publishes and the conductor guard's contract folders back to that list, so a folder
/// added or renamed in one place cannot silently leave the others naming something else.
/// </summary>
public sealed class DocsLayoutFolderSourcesTests
{
    [Fact]
    public void EveryScaffoldedFolderWithAnIndexIsPublishedByTheRegistry()
    {
        OntologyConfig ontology = new() { DocsRoots = ["docs"] };
        IReadOnlyList<ConfigRegEntry> entries = ConfigRegConfig.ProductDefaults.Resolve(ontology);

        foreach (string folder in DocsLayout.Folders.Where(folder => folder != DocsLayout.Standards))
        {
            string expected = DocsLayout.FolderIndex("docs", folder);
            Assert.Contains(entries, entry => string.Equals(entry.Path, expected, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void TheConductorContractFoldersAreTheScaffoldedFolderNames()
    {
        Assert.All(
            ConductorGuardRules.ContractDirectories,
            folder => Assert.Contains(folder, DocsLayout.Folders));
    }
}
