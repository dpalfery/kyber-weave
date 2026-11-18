using KyberWeave.Core.Docs.Model;
using Xunit;

namespace KyberWeave.Application.Tests.Docs.Model;

/// <summary>The component grouping the catalog command reads, over documents built in memory.</summary>
public sealed class DocumentSetGroupingTests
{
    [Fact]
    public void ItGroupsDocumentsByComponentAndOrdersEachGroupByPath()
    {
        DocumentSet set = new()
        {
            Documents =
            [
                Document("docs/b.md", "core"),
                Document("docs/a.md", "core"),
                Document("docs/c.md", "web")
            ]
        };

        IReadOnlyDictionary<string, IReadOnlyList<DocumentModel>> groups = set.ByComponent();

        Assert.Equal(["docs/a.md", "docs/b.md"], groups["core"].Select(d => d.RelativePath));
        Assert.Equal(["docs/c.md"], groups["web"].Select(d => d.RelativePath));
    }

    [Fact]
    public void ItFilesADocumentWithoutAComponentUnderNone()
    {
        DocumentSet set = new() { Documents = [Document("docs/x.md", null)] };

        Assert.Equal(["docs/x.md"], set.ByComponent()["(none)"].Select(d => d.RelativePath));
    }

    private static DocumentModel Document(string path, string? component) =>
        new()
        {
            RelativePath = path,
            FilePath = "/repo/" + path,
            HasFrontmatter = true,
            Frontmatter = new DocumentFrontmatter { Component = component }
        };
}
