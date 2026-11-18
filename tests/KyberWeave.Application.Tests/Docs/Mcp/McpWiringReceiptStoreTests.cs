using KyberWeave.Core.Docs.Mcp;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Docs.Mcp;

/// <summary>
/// The receipt store over the file-system seam: the undo record is read, written and refused in memory, and the
/// bytes it writes are pinned because the receipt is a file users keep.
/// </summary>
public sealed class McpWiringReceiptStoreTests
{
    private const string Root = "/work/repo";
    private const string ReceiptFile = Root + "/.kyber-weave/receipts/mcp-wiring.json";

    private static readonly DateTimeOffset Written = new(2026, 11, 18, 9, 30, 0, TimeSpan.Zero);

    private readonly InMemoryFileSystem _files = new();

    private McpWiringReceiptStore Store() => new(_files);

    private static McpWiringReceiptRecord Inserted() => new(
        ".mcp.json",
        McpWiringWriteMode.Inserted,
        ["kw-docs"],
        "AA11",
        "BB22",
        Written,
        "e30=");

    private static McpWiringReceiptRecord Created() => new(
        ".factory/mcp.json",
        McpWiringWriteMode.Created,
        ["kw-docs"],
        null,
        "CC33",
        Written);

    [Fact]
    public void AnAbsentReceiptLoadsAsEmpty()
    {
        Assert.Empty(Store().Load(Root).Files);
    }

    [Fact]
    public void SavedRecordsLoadBackInTheOrderTheyWereWritten()
    {
        Assert.Null(Store().Save(Root, [Inserted(), Created()]));

        McpWiringReceipt loaded = Store().Load(Root);

        Assert.Equal([".mcp.json", ".factory/mcp.json"], loaded.Files.Select(record => record.Path));
        Assert.Equal(Inserted(), loaded.Find(".mcp.json"), new RecordComparer());
        Assert.Equal(McpWiringWriteMode.Created, loaded.Find(".factory/mcp.json")?.Mode);
    }

    [Fact]
    public void TheReceiptIsWrittenAsIndentedCamelCaseJsonWithoutNullsAndWithoutAByteOrderMark()
    {
        Store().Save(Root, [Created()]);

        string expected = string.Join(
            "\n",
            "{",
            "  \"version\": 1,",
            "  \"files\": [",
            "    {",
            "      \"path\": \".factory/mcp.json\",",
            "      \"mode\": \"created\",",
            "      \"servers\": [",
            "        \"kw-docs\"",
            "      ],",
            "      \"postSha256\": \"CC33\",",
            "      \"writtenAtUtc\": \"2026-11-18T09:30:00+00:00\"",
            "    }",
            "  ]",
            "}");

        string actual = _files.ReadAllText(ReceiptFile).ReplaceLineEndings("\n");

        Assert.Equal(expected, actual);
        Assert.NotEqual(0xEF, _files.ReadAllBytes(ReceiptFile)[0]);
    }

    [Fact]
    public void ASecondSaveReplacesTheFirst()
    {
        Store().Save(Root, [Inserted()]);
        Store().Save(Root, [Created()]);

        Assert.Equal([".factory/mcp.json"], Store().Load(Root).Files.Select(record => record.Path));
    }

    [Fact]
    public void AReceiptThatIsNotJsonLoadsAsEmpty()
    {
        _files.CreateDirectory(Root + "/.kyber-weave/receipts");
        _files.WriteAllText(ReceiptFile, "not json");

        Assert.Empty(Store().Load(Root).Files);
    }

    [Fact]
    public void AReceiptOfAnotherSchemaVersionLoadsAsEmpty()
    {
        _files.CreateDirectory(Root + "/.kyber-weave/receipts");
        _files.WriteAllText(ReceiptFile, "{\"version\":2,\"files\":[]}");

        Assert.Empty(Store().Load(Root).Files);
    }

    [Fact]
    public void AReceiptThatNamesAPathOutsideTheRepositoryLoadsAsEmpty()
    {
        Store().Save(Root, [Inserted() with { Path = "../elsewhere/mcp.json" }]);

        Assert.Empty(Store().Load(Root).Files);
    }

    [Fact]
    public void AReceiptThatCannotBeReadLoadsAsEmpty()
    {
        Store().Save(Root, [Inserted()]);
        _files.FailWhen(operation => operation.Name == "ReadAllText", new IOException("disk gone"));

        Assert.Empty(Store().Load(Root).Files);
    }

    [Fact]
    public void ASaveThatCannotWriteAnswersWithTheReasonInsteadOfThrowing()
    {
        _files.FailWhen(operation => operation.Name == "WriteAllText", new IOException("read-only volume"));

        string? failure = Store().Save(Root, [Inserted()]);

        Assert.Equal("read-only volume", failure);
        Assert.False(_files.FileExists(ReceiptFile));
    }

    [Fact]
    public void ASaveDeniedAccessAnswersWithTheReasonInsteadOfThrowing()
    {
        _files.FailWhen(operation => operation.Name == "CreateDirectory", new UnauthorizedAccessException("no access"));

        Assert.Equal("no access", Store().Save(Root, [Inserted()]));
    }

    private sealed class RecordComparer : IEqualityComparer<McpWiringReceiptRecord?>
    {
        public bool Equals(McpWiringReceiptRecord? x, McpWiringReceiptRecord? y) =>
            x is not null && y is not null
            && x.Path == y.Path
            && x.Mode == y.Mode
            && x.Servers.SequenceEqual(y.Servers)
            && x.PreSha256 == y.PreSha256
            && x.PostSha256 == y.PostSha256
            && x.WrittenAtUtc == y.WrittenAtUtc
            && x.PreContent == y.PreContent;

        public int GetHashCode(McpWiringReceiptRecord? obj) => obj?.Path.GetHashCode(StringComparison.Ordinal) ?? 0;
    }
}
