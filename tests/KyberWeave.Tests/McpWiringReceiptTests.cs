using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Mcp;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The receipt (5.8–5.10): every write is auditable and removable, and removal refuses rather than
/// restoring over someone else's edit.
/// </summary>
/// <remarks>
/// <para>
/// The receipt is asserted from the file on disk and from digests computed here over the fixture's
/// own bytes, so a receipt that records the wrong digest fails even though the repair "worked". The
/// digest is the whole point: it is what lets <c>mcp-unwire</c> tell its own write from a foreign
/// edit, and a record whose post-write digest does not match the file it names can never restore
/// anything safely.
/// </para>
/// <para>
/// Removal is tested in both modes, because they are different operations: an <c>inserted</c> file is
/// restored to the bytes the receipt captured, and a <c>created</c> file is deleted outright. Drift
/// in either mode is refused with <c>KW-MCP-WIRE-006</c> and leaves the file exactly as the foreign
/// edit left it (5.9).
/// </para>
/// </remarks>
public sealed class McpWiringReceiptTests : IDisposable
{
    private const string ServerName = "kyber-weave";

    private const string BinaryName = "kyber-weave-mcp";

    private const string DriftCode = "KW-MCP-WIRE-006";

    private const string VsCode = "vscode";

    private const string VsCodeConfigFile = ".vscode/mcp.json";

    private readonly TempDirectory _root = new();

    public McpWiringReceiptTests()
    {
        Directory.CreateDirectory(Repo);
        Directory.CreateDirectory(Bin);
        McpWiringFixture.WriteExecutable(Bin, BinaryName);
    }

    private string Repo => Path.Combine(_root.Path, "repo");

    /// <summary>Where a case puts the fake launch binary it wants the resolver to find.</summary>
    private string Bin => Path.Combine(_root.Path, "bin");

    private string ReceiptPath => McpWiringReceipt.PathFor(Repo);

    public void Dispose() => _root.Dispose();

    /// <summary>
    /// The location is a design decision the removal path depends on (DD13): a receipt anywhere else
    /// is a receipt <c>mcp-unwire</c> will not find, and a repair nobody can undo.
    /// </summary>
    [Fact]
    public void TheReceiptLivesAtThePathTheDesignFixes()
    {
        Assert.Equal(".kyber-weave/receipts/mcp-wiring.json", McpWiringReceipt.RelativePath);
        Assert.Equal(1, McpWiringReceipt.SchemaVersion);
        Assert.Equal(
            Path.Combine(Repo, ".kyber-weave", "receipts", "mcp-wiring.json"),
            ReceiptPath);
    }

    [Fact]
    public void TheDriftRefusalCarriesTheRuleIdTheSpecificationNames() =>
        Assert.Equal(DriftCode, McpWiringReceipt.DriftCode);

    [Fact]
    public void ARepositoryWithNoReceiptLoadsAsEmpty()
    {
        McpWiringReceipt receipt = McpWiringReceipt.Load(Repo);

        Assert.Empty(receipt.Files);
        Assert.Null(receipt.Find(VsCodeConfigFile));
        Assert.False(File.Exists(ReceiptPath));
    }

    /// <summary>
    /// An insert into an existing file records both digests — the file as it was, and the file as it
    /// now is — under the schema's own names, so the record is readable by anything that is not this
    /// code (5.8, DD13).
    /// </summary>
    /// <remarks>
    /// The pre-write <em>content</em> is asserted beside the digest, because 5.8 requires removal to
    /// restore the state the receipt recorded and a digest cannot produce bytes to restore: it can
    /// only prove that bytes are the ones recorded. Without the content the record would document
    /// the write without being able to undo it.
    /// </remarks>
    [Fact]
    public void AnInsertedWriteRecordsBothDigestsAndTheMode()
    {
        byte[] original = Encoding.UTF8.GetBytes("""{"servers": {}}""");
        McpWiringFixture.WriteBytes(Repo, VsCodeConfigFile, original);

        Repair(VsCode);

        byte[] written = File.ReadAllBytes(Path.Combine(Repo, VsCodeConfigFile));

        using JsonDocument receipt = JsonDocument.Parse(File.ReadAllText(ReceiptPath));
        Assert.Equal(1, receipt.RootElement.GetProperty("version").GetInt32());

        JsonElement record = Assert.Single(receipt.RootElement.GetProperty("files").EnumerateArray());
        Assert.Equal(VsCodeConfigFile, record.GetProperty("path").GetString());
        Assert.Equal("inserted", record.GetProperty("mode").GetString());
        Assert.Equal(ServerName, Assert.Single(record.GetProperty("servers").EnumerateArray()).GetString());
        Assert.Equal(Digest(original), Normalize(record.GetProperty("preSha256").GetString()));
        Assert.Equal(Digest(written), Normalize(record.GetProperty("postSha256").GetString()));

        // The recorded content is the file the repair found, byte for byte, and it is the content the
        // recorded digest describes. Asserting both is what makes the receipt the source a restore
        // reads rather than a witness to bytes obtained some other way.
        byte[] recordedPreWrite = Convert.FromBase64String(record.GetProperty("preContent").GetString() ?? string.Empty);
        Assert.Equal(original, recordedPreWrite);
        Assert.Equal(Normalize(record.GetProperty("preSha256").GetString()), Digest(recordedPreWrite));

        McpWiringReceiptRecord loaded = RecordFor(McpWiringReceipt.Load(Repo), VsCodeConfigFile);
        Assert.Equal(McpWiringWriteMode.Inserted, loaded.Mode);
        Assert.Equal(ServerName, Assert.Single(loaded.Servers));
        Assert.Equal(Digest(original), Normalize(loaded.PreSha256));
        Assert.Equal(Digest(written), Normalize(loaded.PostSha256));
        Assert.Equal(TimeSpan.Zero, loaded.WrittenAtUtc.Offset);
        Assert.Equal(original, Convert.FromBase64String(loaded.PreContent ?? string.Empty));
    }

    /// <summary>
    /// A created file has no pre-write content to record, only the digest of what was created —
    /// which is what tells a later removal to delete rather than restore.
    /// </summary>
    [Fact]
    public void ACreatedWriteRecordsTheModeAndItsPostDigest()
    {
        Repair(VsCode);

        byte[] written = File.ReadAllBytes(Path.Combine(Repo, VsCodeConfigFile));

        using JsonDocument receipt = JsonDocument.Parse(File.ReadAllText(ReceiptPath));
        JsonElement record = Assert.Single(receipt.RootElement.GetProperty("files").EnumerateArray());

        Assert.Equal(VsCodeConfigFile, record.GetProperty("path").GetString());
        Assert.Equal("created", record.GetProperty("mode").GetString());
        Assert.Equal(Digest(written), Normalize(record.GetProperty("postSha256").GetString()));
        Assert.True(
            !record.TryGetProperty("preSha256", out JsonElement pre) || pre.ValueKind == JsonValueKind.Null,
            "a created file has no pre-write content, so there is no pre-write digest to record");

        McpWiringReceiptRecord loaded = RecordFor(McpWiringReceipt.Load(Repo), VsCodeConfigFile);
        Assert.Equal(McpWiringWriteMode.Created, loaded.Mode);
        Assert.Equal(Digest(written), Normalize(loaded.PostSha256));
    }

    /// <summary>
    /// One record per file, however many files the run touched (DD13): a second record for the same
    /// path would leave the removal path guessing which write it is undoing.
    /// </summary>
    [Fact]
    public void TheReceiptHoldsOneRecordPerFile()
    {
        Repair(VsCode, "claude-code");

        McpWiringReceipt receipt = McpWiringReceipt.Load(Repo);

        Assert.Equal(2, receipt.Files.Count);
        Assert.Single(receipt.Files, record => record.Path == VsCodeConfigFile);
        Assert.Single(receipt.Files, record => record.Path == ".mcp.json");
    }

    /// <summary>
    /// A write, an undo and a second write leave one record for the file — the latest — not a
    /// growing history nothing prunes.
    /// </summary>
    [Fact]
    public void ASecondWriteAfterUnwireLeavesOneRecordForTheFile()
    {
        Repair(VsCode);
        McpWiringRepairer.Unwire(Repo, [VsCode]);
        Repair(VsCode);

        McpWiringReceipt receipt = McpWiringReceipt.Load(Repo);

        Assert.Single(receipt.Files, record => record.Path == VsCodeConfigFile);
        Assert.Equal(McpWiringWriteMode.Created, RecordFor(receipt, VsCodeConfigFile).Mode);
    }

    /// <summary>
    /// Undoing an insert restores the file to the bytes the receipt captured — the whole file, not
    /// the entry's neighbourhood, so a comment or key order nothing in the repair touched is intact.
    /// </summary>
    [Fact]
    public void UnwireRestoresAnInsertedFileToItsPreWriteBytes()
    {
        byte[] original = Encoding.UTF8.GetBytes("""
            {
              // keep this comment
              "servers": {}
            }
            """);
        McpWiringFixture.WriteBytes(Repo, VsCodeConfigFile, original);

        Repair(VsCode);
        Assert.NotEqual(original, File.ReadAllBytes(Path.Combine(Repo, VsCodeConfigFile)));

        McpWiringRepairReport report = McpWiringRepairer.Unwire(Repo, [VsCode]);

        McpWiringFileChange change = Assert.Single(report.Files);
        Assert.Equal(McpWiringChangeOutcome.Restored, change.Outcome);
        Assert.True(change.Written, "a restore is a write");
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(Repo, VsCodeConfigFile)));
        Assert.Null(McpWiringReceipt.Load(Repo).Find(VsCodeConfigFile));
    }

    /// <summary>
    /// A file the repair created is deleted by the undo, not left holding an entry whose record is
    /// gone (5.8): the pre-write state of a created file is "absent".
    /// </summary>
    [Fact]
    public void UnwireDeletesAFileTheRepairCreated()
    {
        Repair(VsCode);
        Assert.True(File.Exists(Path.Combine(Repo, VsCodeConfigFile)));

        McpWiringRepairReport report = McpWiringRepairer.Unwire(Repo, [VsCode]);

        McpWiringFileChange change = Assert.Single(report.Files);
        Assert.Equal(McpWiringChangeOutcome.Deleted, change.Outcome);
        Assert.True(change.Written, "a delete is a write");
        Assert.False(File.Exists(Path.Combine(Repo, VsCodeConfigFile)));
        Assert.Null(McpWiringReceipt.Load(Repo).Find(VsCodeConfigFile));
    }

    /// <summary>
    /// Drift is refused, never restored over (5.9): the file changed after the repair, so its
    /// current digest no longer equals the recorded one and the foreign edit must win.
    /// </summary>
    [Fact]
    public void UnwireRefusesWhenTheFileChangedAfterTheRepair()
    {
        byte[] original = Encoding.UTF8.GetBytes("""{"mcpServers": {}}""");
        McpWiringFixture.WriteBytes(Repo, ".mcp.json", original);
        Repair("claude-code");

        File.AppendAllText(Path.Combine(Repo, ".mcp.json"), "\n// a teammate's edit\n");
        byte[] drifted = File.ReadAllBytes(Path.Combine(Repo, ".mcp.json"));

        McpWiringRepairReport report = McpWiringRepairer.Unwire(Repo, ["claude-code"]);

        McpWiringFileChange change = Assert.Single(report.Files);
        Assert.Equal(McpWiringChangeOutcome.Refused, change.Outcome);
        Assert.Equal(DriftCode, change.Code);
        Assert.False(change.Written);
        Assert.Contains(".mcp.json", DiagnosticText(change), StringComparison.Ordinal);
        Assert.Equal(drifted, File.ReadAllBytes(Path.Combine(Repo, ".mcp.json")));
        Assert.NotNull(
            McpWiringReceipt.Load(Repo).Find(".mcp.json"));
    }

    /// <summary>
    /// A receipt whose recorded content no longer matches its own recorded digest cannot restore
    /// anything (5.8, 5.9): the digest is what makes the content trustworthy, so a payload that fails
    /// it proves nothing about what the file held. Removal must refuse rather than write those bytes
    /// — restoring a tampered payload would put a file on disk that the receipt does not describe,
    /// which is the same failure as restoring over a foreign edit.
    /// </summary>
    [Fact]
    public void UnwireRefusesAReceiptWhoseRecordedContentNoLongerMatchesItsDigest()
    {
        byte[] original = Encoding.UTF8.GetBytes("""{"servers": {}}""");
        McpWiringFixture.WriteBytes(Repo, VsCodeConfigFile, original);
        Repair(VsCode);

        byte[] repaired = File.ReadAllBytes(Path.Combine(Repo, VsCodeConfigFile));

        // The target file is untouched and still matches its post-write digest, so this is not the
        // drift refusal: the only thing wrong is the record's own payload.
        byte[] tampered = Encoding.UTF8.GetBytes("""{"servers": {}, "tampered": true}""");
        ReplaceReceiptPreContent(VsCodeConfigFile, tampered);

        // The tampered receipt still loads and still yields the record — otherwise a refusal would be
        // indistinguishable from "the receipt could not be read", and this case would prove nothing
        // about the integrity check.
        McpWiringReceiptRecord? afterTampering = McpWiringReceipt.Load(Repo).Find(VsCodeConfigFile);
        Assert.NotNull(afterTampering);
        Assert.Equal(tampered, Convert.FromBase64String(afterTampering.PreContent ?? string.Empty));

        McpWiringRepairReport report = McpWiringRepairer.Unwire(Repo, [VsCode]);

        McpWiringFileChange change = Assert.Single(report.Files);
        Assert.Equal(McpWiringChangeOutcome.Refused, change.Outcome);
        Assert.Equal(DriftCode, change.Code);
        Assert.False(change.Written);
        Assert.Contains("pre-write content", DiagnosticText(change), StringComparison.Ordinal);

        // Neither the tampered payload nor the original bytes were written: a refusal is zero bytes.
        Assert.Equal(repaired, File.ReadAllBytes(Path.Combine(Repo, VsCodeConfigFile)));
        Assert.NotNull(
            McpWiringReceipt.Load(Repo).Find(VsCodeConfigFile));
    }

    /// <summary>
    /// Removal without a record is a refusal, not a deletion (5.9): Kyber-Weave only removes what it
    /// can prove it wrote.
    /// </summary>
    [Fact]
    public void UnwireRefusesWhenTheReceiptHoldsNoRecord()
    {
        McpWiringFixture.WriteBytes(Repo, ".mcp.json", Encoding.UTF8.GetBytes("""{"mcpServers": {}}"""));
        byte[] before = File.ReadAllBytes(Path.Combine(Repo, ".mcp.json"));

        McpWiringRepairReport report = McpWiringRepairer.Unwire(Repo, ["claude-code"]);

        McpWiringFileChange change = Assert.Single(report.Files);
        Assert.Equal(McpWiringChangeOutcome.Refused, change.Outcome);
        Assert.Equal(DriftCode, change.Code);
        Assert.False(change.Written);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(Repo, ".mcp.json")));
        Assert.False(File.Exists(ReceiptPath), "a refusal writes no receipt either");
    }

    /// <summary>
    /// The dry-run carve-out reaches the receipt too: a preview that created its own receipt would
    /// make the next real run's undo record a lie (5.10).
    /// </summary>
    [Fact]
    public void ADryRunWritesNoReceiptAndCreatesNoFile()
    {
        McpWiringRepairer.Repair(
            Repo,
            McpWiringFixture.Declare(FirstPartyServer()),
            ResolverOver(Bin),
            [VsCode],
            dryRun: true);

        Assert.False(File.Exists(ReceiptPath));
        Assert.False(File.Exists(Path.Combine(Repo, VsCodeConfigFile)));
        Assert.Empty(McpWiringReceipt.Load(Repo).Files);
    }

    private McpWiringRepairReport Repair(params string[] targets) =>
        McpWiringRepairer.Repair(Repo, McpWiringFixture.Declare(FirstPartyServer()), ResolverOver(Bin), targets, dryRun: false);

    private LaunchCommandResolver ResolverOver(params string[] searchPaths) => new(searchPaths);

    private static RequiredMcpServer FirstPartyServer() =>
        new(ServerName, [BinaryName, "--repo-root", "."], McpProvenance.FirstParty);

    /// <summary>
    /// Rewrites one record's <c>preContent</c> on disk and leaves its digests as they were, which is
    /// exactly the corruption the removal path's integrity check exists for: a payload that no longer
    /// proves itself. The receipt's other fields are preserved, so the record still looks complete.
    /// </summary>
    private void ReplaceReceiptPreContent(string relativePath, byte[] content)
    {
        JsonNode document = JsonNode.Parse(File.ReadAllText(ReceiptPath))
            ?? throw new InvalidOperationException("the receipt is not a JSON document");

        JsonNode? record = document["files"]?
            .AsArray()
            .FirstOrDefault(candidate => candidate?["path"]?.GetValue<string>() == relativePath);

        if (record is null)
            throw new InvalidOperationException($"the receipt holds no record for '{relativePath}'");

        record["preContent"] = Convert.ToBase64String(content);

        File.WriteAllText(ReceiptPath, document.ToJsonString());
    }

    private static McpWiringReceiptRecord RecordFor(McpWiringReceipt receipt, string relativePath) =>
        receipt.Find(relativePath) ??
        throw new InvalidOperationException($"the receipt holds no record for '{relativePath}'");

    /// <summary>Separators are normalized so the refusal is asserted by the path's value.</summary>
    private static string DiagnosticText(McpWiringFileChange change) =>
        $"{change.Message} {change.Hint}".Replace('\\', '/');

    private static string Digest(byte[] content) => Convert.ToHexString(SHA256.HashData(content));

    /// <summary>
    /// Hex case is not part of the contract — a digest is compared by value, and the receipt may be
    /// written by either a lower- or upper-case hex encoder.
    /// </summary>
    private static string Normalize(string? digest) => (digest ?? string.Empty).ToUpperInvariant();

}
