using KyberWeave.Core.Docs.Graph;
using Xunit;

namespace KyberWeave.Tests;

public sealed class IndexSectionCatalogTests
{
    [Fact]
    public void MyPlansReadmeDoesNotMatchPlansInventorySuffix()
    {
        Assert.False(IndexSectionCatalog.IsIndexSection("6-Docs/myplans/README.md", "Archived Plans"));
        Assert.True(IndexSectionCatalog.IsIndexSection("6-Docs/plans/README.md", "Archived Plans"));
    }

    [Fact]
    public void ArchivedPlanInventoryInItsOwnFileIsAnIndexSection()
    {
        Assert.True(IndexSectionCatalog.IsIndexSection("docs/archive/plans/README.md", "Archived Plans"));
        Assert.False(IndexSectionCatalog.IsIndexSection("docs/archive/myplans/README.md", "Archived Plans"));
    }
}
