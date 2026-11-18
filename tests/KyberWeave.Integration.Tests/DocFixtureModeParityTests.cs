using KyberWeave.Core.Diagnostics;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The in-memory <see cref="DocFixture"/> must answer exactly what the disk fixture answers for one corpus: the
/// loader and validator read the file-system seam, and the fake is only as good as its agreement with the adapter.
/// </summary>
public sealed class DocFixtureModeParityTests
{
    [Fact]
    public void ValidatorReportsTheSameFindingsOnDiskAndInMemory()
    {
        using DocFixture disk = Corpus(DocFixture.Create());
        using DocFixture memory = Corpus(DocFixture.CreateInMemory());

        string[] onDisk = Findings(disk);
        string[] inMemory = Findings(memory);

        Assert.NotEmpty(onDisk);
        Assert.Equal(onDisk, inMemory);
    }

    [Fact]
    public void LoaderReadsTheSameDocumentsOnDiskAndInMemory()
    {
        using DocFixture disk = Corpus(DocFixture.Create());
        using DocFixture memory = Corpus(DocFixture.CreateInMemory());

        string[] onDisk = [.. disk.Load().Documents.Select(d => d.RelativePath).Order(StringComparer.Ordinal)];
        string[] inMemory = [.. memory.Load().Documents.Select(d => d.RelativePath).Order(StringComparer.Ordinal)];

        Assert.Contains("6-Docs/reference/good.md", onDisk);
        Assert.Equal(onDisk, inMemory);
    }

    [Fact]
    public void AnInMemoryFixtureLeavesNothingOnDisk()
    {
        using DocFixture memory = Corpus(DocFixture.CreateInMemory());

        Assert.False(Directory.Exists(memory.Root));
        Assert.True(memory.Memory!.FileExists(Path.Combine(memory.Root, "6-Docs", "reference", "good.md")));
    }

    private static DocFixture Corpus(DocFixture fixture) =>
        fixture.WithCatalog()
            .Write("6-Docs/reference/good.md", Document("reference/good", "current"))
            .Write("6-Docs/reference/bad.md", Document("reference/bad", "not-a-status"));

    private static string Document(string id, string status) => $"""
        ---
        id: {id}
        title: A document
        doc-type: reference
        status: {status}
        owner: Maintainers
        last-reviewed: 2026-07-21
        ---

        # A document

        Body.
        """;

    private static string[] Findings(DocFixture fixture) =>
    [
        .. fixture.Validate().Items
            .Select(d => $"{d.Code}|{d.Severity}|{Neutral(d.Message, fixture)}|{Neutral(d.Subject, fixture)}|{Neutral(d.FilePath, fixture)}|{d.StartLine}")
            .Order(StringComparer.Ordinal)
    ];

    private static string Neutral(string? text, DocFixture fixture) =>
        (text ?? string.Empty).Replace(fixture.Root, "<root>", StringComparison.Ordinal).Replace('\\', '/');
}
