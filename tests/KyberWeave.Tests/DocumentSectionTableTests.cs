using KyberWeave.Core.Docs.Graph;
using Xunit;

namespace KyberWeave.Tests;

public sealed class DocumentSectionTableTests
{
    [Fact]
    public void PipeCharactersInsideLinkTitlesStayInOneCell()
    {
        const string body = """
            | Plan | Status | Goal |
            |---|---|---|
            | [a|b](x.md) | ok | done |
            """;

        List<string[]>? rows = DocumentSectionTable.ReadDataRows(body);
        Assert.NotNull(rows);
        string[] row = Assert.Single(rows);
        Assert.Equal(3, row.Length);
        Assert.Equal("[a|b](x.md)", row[0]);
        Assert.Equal("ok", row[1]);
        Assert.Equal("done", row[2]);
    }
}
