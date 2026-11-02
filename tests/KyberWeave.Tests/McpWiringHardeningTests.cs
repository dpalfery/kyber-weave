using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Mcp;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The boundary half of the wiring feature: what the repair and the removal may touch, and what they
/// refuse to interpret.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="McpWiringRepairerTests"/> covers the happy path and the documented refusals of a
/// repository that is in good order. This suite covers the tree that is not: a receipt somebody
/// edited, a file whose name reads like a parent reference, a directory this process may not
/// traverse, a harness template that is the wrong shape, and a declared server whose name would
/// break the text it is pasted into.
/// </para>
/// <para>
/// Every case arranges its condition on a real tree and drives the real entry point, because each
/// claim is about observable behaviour: a tampered receipt on disk, a real symbolic link, a real
/// permission bit. Where the platform refuses to arrange the condition — Windows has no permission
/// bits, and an account that bypasses them (root, or one holding a capability) reads a denied
/// directory anyway — the case is skipped with the reason rather than failed, so a red result means
/// the code under test is wrong rather than that the fixture never ran.
/// </para>
/// </remarks>
public sealed class McpWiringHardeningTests : IDisposable
{
    private const string ServerName = "kyber-weave";

    private const string BinaryName = "kyber-weave-mcp";

    private const string ClaudeCode = "claude-code";

    private const string VsCode = "vscode";

    private const string RefusalCode = "KW-MCP-WIRE-005";

    /// <summary>The wiring check's "not wired" id, on the findings the fix list is derived from.</summary>
    private const string NotWiredCode = "KW-MCP-WIRE-001";

    private readonly TempDirectory _root = new();

    public McpWiringHardeningTests()
    {
        Directory.CreateDirectory(Repo);
        Directory.CreateDirectory(Bin);

        McpWiringFixture.WriteExecutable(Bin, BinaryName);
    }

    private string Repo => Path.Combine(_root.Path, "repo");

    /// <summary>Where a case puts the fake launch binaries it wants the resolver to find.</summary>
    private string Bin => Path.Combine(_root.Path, "bin");

    /// <summary>The receipt the design fixes at this path; asserted as a literal, not via its owner.</summary>
    private string ReceiptPath => Path.Combine(Repo, ".kyber-weave", "receipts", "mcp-wiring.json");

    public void Dispose() => _root.Dispose();

    // ---- A receipt is local state on disk, so nothing it names may reach outside the repository.

    /// <summary>
    /// A record whose path escapes the repository is not loaded, so no later step can combine it into
    /// a path outside the tree.
    /// </summary>
    /// <remarks>
    /// The receipt is a file in the operator's checkout, so it can be corrupted, hand-edited, or
    /// written by something other than this tool. A record naming <c>../../outside</c> is a path the
    /// removal would resolve outside the repository it was asked to repair, which is the one thing
    /// 2.6 and 5.2 refuse absolutely. The whole receipt is rejected rather than the record dropped:
    /// a document whose shape this code cannot trust is not a document it can prove anything from,
    /// and an empty receipt refuses every removal, which is the safe direction.
    /// </remarks>
    [Theory]
    [InlineData("../../outside/mcp.json")]
    [InlineData(".kyber-weave/../../outside/mcp.json")]
    public void AReceiptRecordWhosePathEscapesTheRepositoryIsNotLoaded(string escaping)
    {
        string outside = Path.Combine(_root.Path, "outside", "mcp.json");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        File.WriteAllText(outside, """{"mcpServers": {}}""");

        WriteReceipt(Record(escaping));

        Assert.Empty(McpWiringReceipt.Load(Repo).Files);
        Assert.Null(McpWiringReceipt.Load(Repo).Find(escaping));
        Assert.Equal("""{"mcpServers": {}}""", File.ReadAllText(outside));
    }

    /// <summary>
    /// A record whose path is already absolute is rejected for the same reason: combining it with the
    /// root would discard the root.
    /// </summary>
    [Fact]
    public void AReceiptRecordWithAnAbsolutePathIsNotLoaded()
    {
        string absolute = Path.Combine(_root.Path, "outside", "mcp.json");

        WriteReceipt(Record(absolute));

        Assert.Empty(McpWiringReceipt.Load(Repo).Files);
    }

    /// <summary>
    /// The chokepoint that turns a repository-relative name into a filesystem path refuses anything
    /// absolute or escaping, so a path that reaches it from a place this suite does not anticipate —
    /// a catalog row, a receipt, a caller — cannot be resolved outside the repository.
    /// </summary>
    [Fact]
    public void TheTargetPathChokepointRefusesAPathOutsideTheRepository()
    {
        Assert.Equal(
            Path.Combine(Repo, ".vscode", "mcp.json"),
            McpWiringRepairer.ContainedPath(Repo, ".vscode/mcp.json"));

        Assert.Null(McpWiringRepairer.ContainedPath(Repo, "../outside/mcp.json"));
        Assert.Null(McpWiringRepairer.ContainedPath(Repo, ".vscode/../../outside/mcp.json"));
        Assert.Null(McpWiringRepairer.ContainedPath(Repo, Path.Combine(_root.Path, "outside", "mcp.json")));
    }

    // ---- One answer to "is this path inside the repository", asked by every caller.

    /// <summary>
    /// The names a repository owns resolve to the file they name, and the check, the repair and the
    /// fix list all resolve them to the same file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These are the repository-scope files the catalog declares, so they are the names every caller
    /// meets in a real tree. Asserting all three callers rather than the shared rule alone is what
    /// pins the claim the type exists for: a caller that grew its own answer is how a read and a
    /// write came to disagree about one file (ADR 0051).
    /// </para>
    /// <para>
    /// <c>..foo/mcp.json</c> is in the list on purpose: it is an ordinary directory inside the tree
    /// whose name merely begins with the characters that spell a parent reference, and a rule that
    /// tested for those two characters refused a file the repository really has.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(".vscode/mcp.json")]
    [InlineData(".factory/mcp.json")]
    [InlineData(".mcp.json")]
    [InlineData(".cursor/mcp.json")]
    [InlineData(".codex/config.toml")]
    [InlineData("opencode.json")]
    [InlineData("..foo/mcp.json")]
    public void ANameInsideTheRepositoryResolvesToTheSameFileForEveryCaller(string name)
    {
        string expected = Path.Combine(Repo, name.Replace('/', Path.DirectorySeparatorChar));

        Assert.Equal(expected, McpRepositoryPath.Contained(Repo, name));
        Assert.Equal(expected, McpWiringRepairer.ContainedPath(Repo, name));

        McpWiringFix fix = Assert.Single(McpWiringFixes.Derive(ReportOffering(name), Repo));
        Assert.Equal(name, fix.Path);
        Assert.True(fix.Create);
    }

    /// <summary>
    /// A name that escapes the repository is refused by every caller, so no step of the check, the
    /// repair or the fix list can act on a file the repository does not own.
    /// </summary>
    /// <remarks>
    /// A name is refused twice over: once for its spelling, which needs no filesystem access at all,
    /// and once for where it resolves. These two are the spelling refusals — a bare parent reference
    /// and one that climbs out after descending — and they are the shapes a catalog row, a receipt
    /// record, or a hand-edited fix list can carry. Asserting all three callers is the point: the
    /// check deciding which file to read and the repair deciding which file to write must not answer
    /// differently about the same name.
    /// </remarks>
    [Theory]
    [InlineData("../x")]
    [InlineData(".vscode/../../x")]
    public void ANameOutsideTheRepositoryIsRefusedByEveryCaller(string name)
    {
        Assert.Null(McpRepositoryPath.Contained(Repo, name));
        Assert.Null(McpWiringRepairer.ContainedPath(Repo, name));
        Assert.Empty(McpWiringFixes.Derive(ReportOffering(name), Repo));
    }

    /// <summary>
    /// An absolute path is refused by every caller: it names a root of its own, so combining it with
    /// the repository would discard the repository.
    /// </summary>
    [Fact]
    public void AnAbsoluteNameIsRefusedByEveryCaller()
    {
        string absolute = Path.Combine(_root.Path, "outside", "mcp.json");

        Assert.Null(McpRepositoryPath.Contained(Repo, absolute));
        Assert.Null(McpWiringRepairer.ContainedPath(Repo, absolute));
        Assert.Empty(McpWiringFixes.Derive(ReportOffering(absolute), Repo));
    }

    /// <summary>
    /// A sibling directory whose name merely begins with the repository's is not inside it.
    /// </summary>
    /// <remarks>
    /// <c>…/repo-other</c> beside <c>…/repo</c> is the case a prefix test without a trailing separator
    /// reads as being inside the tree, and it is not hypothetical: a checkout and its worktrees sit
    /// beside each other under names like that. The name rule refuses this path for being absolute
    /// before any prefix is compared, so the case states the guarantee rather than the branch that
    /// enforces it — a later relaxation of the name rule must not let a sibling through.
    /// </remarks>
    [Fact]
    public void ASiblingDirectorySharingTheRepositorysNamePrefixIsRefused()
    {
        string sibling = Path.Combine(_root.Path, "repo-other", "mcp.json");
        Directory.CreateDirectory(Path.GetDirectoryName(sibling)!);
        File.WriteAllText(sibling, """{"mcpServers": {}}""");

        Assert.Null(McpRepositoryPath.Contained(Repo, sibling));
        Assert.Null(McpWiringRepairer.ContainedPath(Repo, sibling));
        Assert.Empty(McpWiringFixes.Derive(ReportOffering(sibling), Repo));
    }

    /// <summary>
    /// A name that spells a parent reference is refused for its spelling, before it is resolved —
    /// including one that would resolve back inside the tree — and the fix list refuses it too.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>.vscode/../.mcp.json</c> resolves to a file the repository owns, so a rule that compared
    /// only resolved paths would accept it. The shared rule refuses it earlier, on the name: a
    /// configuration path this code writes is one it composes from a catalog row it controls, and a
    /// catalog row or a receipt record that spells a parent reference is not one it can prove stays
    /// inside the tree. The refusal costs nothing an operator wants — the same file is reachable as
    /// <c>.mcp.json</c>, the spelling every catalog row already uses.
    /// </para>
    /// <para>
    /// This case asserts the two callers <em>agree</em>, which is the property that matters: the file
    /// the fix list offers is the file the check read. It does not assert that the name resolves to a
    /// path, because it does not.
    /// </para>
    /// </remarks>
    [Fact]
    public void ANameSpellingAParentReferenceIsRefusedBeforeItIsResolvedAndTheFixListAgrees()
    {
        Assert.Null(McpRepositoryPath.Contained(Repo, ".vscode/../.mcp.json"));
        Assert.Null(McpWiringRepairer.ContainedPath(Repo, ".vscode/../.mcp.json"));
        Assert.Empty(McpWiringFixes.Derive(ReportOffering(".vscode/../.mcp.json"), Repo));
    }

    // ---- The link guard must test for a parent-reference segment, not for the two characters.

    /// <summary>
    /// A sibling named <c>..foo</c> is inspected like any other segment.
    /// </summary>
    /// <remarks>
    /// The guard used to treat a leading <c>..</c> as "outside the root" and answer "no link", which
    /// is right for <c>..</c> and wrong for every file whose name merely starts that way: a symlink
    /// called <c>..foo</c> was then not a link as far as the check, the repair and the removal were
    /// concerned, and all three would read and write through it.
    /// </remarks>
    [Fact]
    public void ASymlinkNamedLikeAParentReferenceIsFoundByTheLinkGuard()
    {
        string outside = Path.Combine(_root.Path, "outside-mcp.json");
        File.WriteAllText(outside, """{"mcpServers": {}}""");
        string link = Path.Combine(Repo, "..foo");
        McpWiringFixture.CreateSymlink(link, outside);

        Assert.Equal("..foo", McpConfigurationLink.LinkedSegment(Repo, link));
    }

    /// <summary>A path that really does leave the root is not this guard's question.</summary>
    [Fact]
    public void APathOutsideTheRepositoryIsNotReportedAsALink()
    {
        string outside = Path.Combine(_root.Path, "elsewhere", "mcp.json");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        File.WriteAllText(outside, """{"mcpServers": {}}""");

        Assert.Null(McpConfigurationLink.LinkedSegment(Repo, outside));
    }

    // ---- Paths are compared by value, and on a case-sensitive filesystem case is part of the value.

    /// <summary>
    /// Two names that differ only in case are two different files, and the refusal says so.
    /// </summary>
    /// <remarks>
    /// <c>Config.json</c> and <c>config.json</c> are distinct entries on a case-sensitive filesystem.
    /// Comparing them with an ignore-case rule described the wrong file and, in the receipt, matched
    /// the wrong record — the drift check and the removal would then act on a file the record does
    /// not describe.
    /// </remarks>
    [Fact]
    public void ANameThatDiffersFromTheLinkOnlyByCaseIsDescribedAsBeingBehindIt()
    {
        Assert.Equal(
            "Config.json lies behind the symbolic link 'config.json'",
            McpConfigurationLink.Describe("Config.json", "config.json"));

        Assert.Equal(
            ".mcp.json is a symbolic link",
            McpConfigurationLink.Describe(".mcp.json", ".mcp.json"));
    }

    /// <summary>
    /// A receipt record for a path that differs from the target only in case does not match it, so a
    /// removal cannot act on a file the record does not describe.
    /// </summary>
    [Fact]
    public void ARecordForAPathThatDiffersOnlyInCaseDoesNotMatchTheFile()
    {
        McpWiringFixture.WriteText(Repo, ".mcp.json", """{"mcpServers": {}}""");

        WriteReceipt(Record(".MCP.json"));

        Assert.Null(McpWiringReceipt.Load(Repo).Find(".mcp.json"));
    }

    // ---- A refusal for want of permission is not the same fact as a missing file.

    /// <summary>
    /// A directory this process may not traverse makes the guard report a denial, not an absence.
    /// </summary>
    /// <remarks>
    /// "Cannot inspect" and "does not exist" lead to opposite decisions: the first must refuse, the
    /// second may create. A probe that folds the denial into "not a link" makes the repair take the
    /// create path over a file it never managed to read.
    /// </remarks>
    [Fact]
    public void AnUnreadableConfigurationIsReportedAsDeniedRatherThanAbsent()
    {
        string directory = Path.Combine(Repo, ".vscode");
        string target = Path.Combine(directory, "mcp.json");
        Directory.CreateDirectory(directory);
        File.WriteAllText(target, """{"servers": {}}""");

        try
        {
            DenyTraversal(directory, target);

            Assert.Equal(McpLinkObstruction.Denied, McpConfigurationLink.Probe(Repo, target).Obstruction);
            Assert.Equal(".vscode/mcp.json", McpConfigurationLink.Probe(Repo, target).Segment);
        }
        finally
        {
            AllowTraversal(directory);
        }
    }

    /// <summary>
    /// A repair over such a file refuses and names the permission problem, rather than reporting a
    /// write that could not be attempted.
    /// </summary>
    [Fact]
    public void ARepairRefusesAConfigurationItMayNotInspectAndLeavesItByteIdentical()
    {
        string directory = Path.Combine(Repo, ".vscode");
        string target = Path.Combine(directory, "mcp.json");
        Directory.CreateDirectory(directory);
        File.WriteAllText(target, """{"servers": {}}""");
        byte[] before = File.ReadAllBytes(target);

        try
        {
            DenyTraversal(directory, target);

            McpWiringFileChange change = SingleChange(Repair(VsCode), VsCode);

            Assert.Equal(McpWiringChangeOutcome.Refused, change.Outcome);
            Assert.False(change.Written);
            Assert.Equal(RefusalCode, change.Code);
            Assert.Contains("could not be inspected", DiagnosticText(change), StringComparison.Ordinal);
        }
        finally
        {
            AllowTraversal(directory);
        }

        Assert.Equal(before, File.ReadAllBytes(target));
    }

    /// <summary>
    /// A configuration this process may not inspect is reported as a gap that names the permission
    /// and offers no wire command, because the repair refuses the same file for the same reason.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The check reports the file it could not read rather than treating it as absent, and the two
    /// facts lead to opposite decisions: "absent" is a harness the repair may create a file for, and
    /// "denied" is one it may not touch. Reporting the denial at <c>info</c>-as-absent would have
    /// offered a write over a file nobody read.
    /// </para>
    /// <para>
    /// The hint names the permission and nothing else. A wire command here would be a command
    /// guaranteed to come back as <c>KW-MCP-WIRE-005</c>, so its absence is the assertion that the
    /// report does not send the operator on that errand.
    /// </para>
    /// </remarks>
    [Fact]
    public void AConfigurationTheProcessMayNotInspectIsReportedAsNotFixableAndOffersNoWireCommand()
    {
        string directory = Path.Combine(Repo, ".vscode");
        string target = Path.Combine(directory, "mcp.json");
        Directory.CreateDirectory(directory);
        File.WriteAllText(target, """{"servers": {}}""");

        try
        {
            DenyTraversal(directory, target);

            McpWiringReport report = McpWiringInspector.Inspect(
                Repo,
                McpWiringFixture.Declare(FirstPartyServer()),
                new LaunchCommandResolver([Bin]));

            McpWiringFinding finding = Assert.Single(report.Findings, candidate => candidate.Harness == VsCode);

            Assert.Equal(McpWiringKind.NotWired, finding.Kind);
            Assert.Equal(NotWiredCode, finding.Code);
            Assert.Equal(McpWiringFixability.NotFixable, finding.Fixable);
            Assert.Contains("could not be inspected: access to '", finding.Message, StringComparison.Ordinal);
            Assert.Contains(
                "a file this process cannot inspect cannot be told from one that is absent",
                finding.Message,
                StringComparison.Ordinal);
            Assert.DoesNotContain("mcp-wire", finding.Hint, StringComparison.Ordinal);

            // The fix list is derived from the findings, and this finding is never offered as a
            // write: the repair would refuse it, so the operator is never asked to consent to it.
            Assert.Empty(McpWiringFixes.Derive(report, Repo));
        }
        finally
        {
            AllowTraversal(directory);
        }
    }

    // ---- The receipt is a schema, and a document that does not match it proves nothing.

    /// <summary>
    /// A receipt whose file list is missing or null loads as empty instead of loading a document the
    /// next step then dereferences.
    /// </summary>
    [Theory]
    [InlineData("""{"version": 1, "files": null}""")]
    [InlineData("""{"version": 1}""")]
    public void AReceiptWithNoFileListLoadsAsEmpty(string text)
    {
        WriteReceiptText(text);

        Assert.Empty(McpWiringReceipt.Load(Repo).Files);
    }

    /// <summary>
    /// A record missing either of the values every later step assumes — its path, its server list —
    /// rejects the receipt rather than yielding a half-built record.
    /// </summary>
    /// <remarks>
    /// <c>path</c> is combined into a filesystem path and <c>servers</c> is enumerated in the report,
    /// so a null in either place is a crash waiting for the removal to reach it. The schema check is
    /// the only place that can refuse it while nothing has been touched.
    /// </remarks>
    [Theory]
    [InlineData("""{"version": 1, "files": [{"mode": "created", "servers": ["kyber-weave"], "postSha256": "AA", "writtenAtUtc": "2026-10-31T00:00:00+00:00"}]}""")]
    [InlineData("""{"version": 1, "files": [{"path": ".mcp.json", "mode": "created", "servers": null, "postSha256": "AA", "writtenAtUtc": "2026-10-31T00:00:00+00:00"}]}""")]
    [InlineData("""{"version": 1, "files": [{"path": ".mcp.json", "mode": "created", "postSha256": "AA", "writtenAtUtc": "2026-10-31T00:00:00+00:00"}]}""")]
    public void AReceiptRecordThatDoesNotMatchTheSchemaRejectsTheReceipt(string text)
    {
        WriteReceiptText(text);

        Assert.Empty(McpWiringReceipt.Load(Repo).Files);
    }

    /// <summary>
    /// A value that is only whitespace names nothing, so a record carrying one is not a record this
    /// code wrote and must not load as proof of one.
    /// </summary>
    /// <remarks>
    /// The loader refuses a declared server whose name is blank, and the receipt is read by the same
    /// removal path that trusts what the loader accepted. <c>String.IsNullOrEmpty</c> let
    /// <c>"servers": [" "]</c> through, so a hand-edited receipt loaded as proven and then enumerated
    /// its blank name into the removal report and the restore.
    /// </remarks>
    [Theory]
    [InlineData("""{"version": 1, "files": [{"path": ".mcp.json", "mode": "created", "servers": [" "], "postSha256": "AA", "writtenAtUtc": "2026-10-31T00:00:00+00:00"}]}""")]
    [InlineData("""{"version": 1, "files": [{"path": ".mcp.json", "mode": "created", "servers": [""], "postSha256": "AA", "writtenAtUtc": "2026-10-31T00:00:00+00:00"}]}""")]
    [InlineData("""{"version": 1, "files": [{"path": ".mcp.json", "mode": "created", "servers": ["kyber-weave", "\t"], "postSha256": "AA", "writtenAtUtc": "2026-10-31T00:00:00+00:00"}]}""")]
    public void AReceiptRecordNamingABlankServerRejectsTheReceipt(string text)
    {
        WriteReceiptText(text);

        Assert.Empty(McpWiringReceipt.Load(Repo).Files);
    }

    /// <summary>
    /// A record naming no servers at all is refused, because the writer never produces one: it records
    /// a file only when at least one server was written to it.
    /// </summary>
    /// <remarks>
    /// The empty list is the same false claim as a blank name, one level up. A record that says a file
    /// was written but names nothing the write added cannot be reported or restored honestly, and
    /// reading it as proven would put a nameless change in front of the operator.
    /// </remarks>
    [Fact]
    public void AReceiptRecordNamingNoServersRejectsTheReceipt()
    {
        WriteReceipt(
            new JsonObject
            {
                ["path"] = ".mcp.json",
                ["mode"] = "created",
                ["servers"] = new JsonArray(),
                ["postSha256"] = "AA",
                ["writtenAtUtc"] = "2026-10-31T00:00:00+00:00"
            });

        Assert.Empty(McpWiringReceipt.Load(Repo).Files);
    }

    /// <summary>
    /// A blank digest is not a digest, and a record whose digests do not say anything cannot prove the
    /// file it names is the file it wrote.
    /// </summary>
    /// <remarks>
    /// Both are compared by value on the removal path, so a whitespace-only one matches no file and no
    /// payload. Loading it as proven defers that to a drift refusal, which reports a change to a file
    /// nobody changed; the shape check is where it can be refused while nothing has been touched.
    /// </remarks>
    [Theory]
    [InlineData("""{"version": 1, "files": [{"path": ".mcp.json", "mode": "created", "servers": ["kyber-weave"], "postSha256": " ", "writtenAtUtc": "2026-10-31T00:00:00+00:00"}]}""")]
    [InlineData("""{"version": 1, "files": [{"path": ".mcp.json", "mode": "inserted", "servers": ["kyber-weave"], "preSha256": " ", "postSha256": "AA", "writtenAtUtc": "2026-10-31T00:00:00+00:00"}]}""")]
    [InlineData("""{"version": 1, "files": [{"path": ".mcp.json", "mode": "inserted", "servers": ["kyber-weave"], "preSha256": "AA", "postSha256": "AA", "preContent": " ", "writtenAtUtc": "2026-10-31T00:00:00+00:00"}]}""")]
    public void AReceiptRecordWithABlankDigestOrPayloadRejectsTheReceipt(string text)
    {
        WriteReceiptText(text);

        Assert.Empty(McpWiringReceipt.Load(Repo).Files);
    }

    /// <summary>
    /// A record with no timestamp is refused: this code writes one on every record it makes, so a
    /// document without one did not come from a write it can describe.
    /// </summary>
    /// <remarks>
    /// The field is the audit half of "every write is removable and auditable" (5.8). A record missing
    /// it cannot say when the file it names was changed, and a receipt that cannot say that is not the
    /// artifact the removal is meant to be reading.
    /// </remarks>
    [Fact]
    public void AReceiptRecordWithNoWriteTimestampRejectsTheReceipt()
    {
        WriteReceipt(
            new JsonObject
            {
                ["path"] = ".mcp.json",
                ["mode"] = "created",
                ["servers"] = new JsonArray(ServerName),
                ["postSha256"] = "AA"
            });

        Assert.Empty(McpWiringReceipt.Load(Repo).Files);
    }

    /// <summary>
    /// The shape check still accepts the record this code writes, with every field read back as
    /// written. Without this case the rule above could tighten into "no record ever loads" and every
    /// removal would refuse for the wrong reason.
    /// </summary>
    [Fact]
    public void AReceiptRecordThisCodeWritesLoadsWithEveryFieldIntact()
    {
        byte[] pre = Encoding.UTF8.GetBytes("""{"mcpServers": {}}""");
        WriteReceipt(Record(
            ".mcp.json",
            McpWiringWriteMode.Inserted,
            Digest(pre),
            Convert.ToBase64String(pre),
            Digest(Encoding.UTF8.GetBytes("""{"mcpServers": {"kyber-weave": {}}}"""))));

        McpWiringReceiptRecord record = McpWiringReceipt.Load(Repo).Find(".mcp.json")
            ?? throw new InvalidOperationException("the record this code writes must load");

        Assert.Equal(".mcp.json", record.Path);
        Assert.Equal(McpWiringWriteMode.Inserted, record.Mode);
        Assert.Equal(ServerName, Assert.Single(record.Servers));
        Assert.Equal(Digest(pre), record.PreSha256);
        Assert.Equal(Convert.ToBase64String(pre), record.PreContent);
        Assert.Equal(
            DateTimeOffset.Parse("2026-10-31T00:00:00+00:00", CultureInfo.InvariantCulture),
            record.WrittenAtUtc);
    }

    // ---- The undo record has to exist before the file it undoes.

    /// <summary>
    /// A repair that cannot record its own undo writes nothing to the harness.
    /// </summary>
    /// <remarks>
    /// Writing the file first and the record second leaves a window: a run killed in between leaves a
    /// mutated harness configuration with no persisted record of its previous bytes, so
    /// <c>mcp-unwire</c> refuses it as drift and the undo is unreachable except through <c>git</c>.
    /// A record that cannot be written therefore stops the write rather than following it, which
    /// keeps "a change is either recorded or not made" true of every write.
    /// </remarks>
    [Fact]
    public void ARepairWhoseUndoRecordCannotBeWrittenWritesNothingToTheHarness()
    {
        // A directory where the receipt file belongs, so the receipt cannot be written at all.
        Directory.CreateDirectory(ReceiptPath);

        McpWiringFileChange change = SingleChange(Repair(ClaudeCode), ClaudeCode);

        Assert.Equal(McpWiringChangeOutcome.Refused, change.Outcome);
        Assert.Equal(RefusalCode, change.Code);
        Assert.False(change.Written);
        Assert.False(
            File.Exists(Path.Combine(Repo, ".mcp.json")),
            "the harness file must not be written when the undo record that would have recorded it is absent");
    }

    /// <summary>
    /// A record whose write never landed is undone by dropping the record, not refused as drift.
    /// </summary>
    /// <remarks>
    /// The record is written first, so a run killed between the record and the rename leaves a record
    /// for a write that never happened: the file still holds exactly the bytes the receipt captured as
    /// its pre-write state. Refusing that as drift would report a foreign edit that does not exist and
    /// leave the operator a record they cannot clear; the state they asked for is already on disk.
    /// </remarks>
    [Fact]
    public void AnInterruptedInsertIsUndoneByDroppingItsRecordRatherThanRefusedAsDrift()
    {
        byte[] pre = Encoding.UTF8.GetBytes("""{"mcpServers": {}}""");
        McpWiringFixture.WriteBytes(Repo, ".mcp.json", pre);

        // The write that was planned, and never landed: the recorded post-write digest is of bytes
        // that are nowhere on disk, while the file still matches the recorded pre-write state.
        string planned = Digest(Encoding.UTF8.GetBytes("""{"mcpServers": {"kyber-weave": {}}}"""));
        WriteReceipt(Record(".mcp.json", McpWiringWriteMode.Inserted, Digest(pre), Convert.ToBase64String(pre), planned));

        McpWiringFileChange change = Assert.Single(McpWiringRepairer.Unwire(Repo, [ClaudeCode]).Files);

        Assert.NotEqual(McpWiringChangeOutcome.Refused, change.Outcome);
        Assert.False(change.Written, "the pre-write bytes were already on disk, so nothing needed writing");
        Assert.Equal(pre, File.ReadAllBytes(Path.Combine(Repo, ".mcp.json")));
        Assert.Null(McpWiringReceipt.Load(Repo).Find(".mcp.json"));
    }

    /// <summary>
    /// The same window on the create path: a record for a file the repair never created is dropped
    /// rather than reported as a file that has gone missing.
    /// </summary>
    [Fact]
    public void AnInterruptedCreateIsUndoneByDroppingItsRecordRatherThanRefusedAsDrift()
    {
        WriteReceipt(Record(".mcp.json", McpWiringWriteMode.Created, postSha256: Digest(Encoding.UTF8.GetBytes("{}"))));

        McpWiringFileChange change = Assert.Single(McpWiringRepairer.Unwire(Repo, [ClaudeCode]).Files);

        Assert.NotEqual(McpWiringChangeOutcome.Refused, change.Outcome);
        Assert.False(change.Written);
        Assert.False(File.Exists(Path.Combine(Repo, ".mcp.json")));
        Assert.Null(McpWiringReceipt.Load(Repo).Find(".mcp.json"));
    }

    // ---- A template is data, and data that is the wrong shape is refused, not overwritten.

    /// <summary>
    /// A canonical document whose server key holds something other than an object is refused rather
    /// than replaced.
    /// </summary>
    /// <remarks>
    /// <see cref="McpWiringRepairer.CreatedDocument"/> clears the container and refills it. Replacing a
    /// value the operator wrote — a list, a string, a number under the server key — would discard it
    /// without a word, so the wrong shape stops the write and the refusal names the template.
    /// </remarks>
    [Theory]
    [InlineData("""{"servers": ["not", "a", "map"]}""")]
    [InlineData("""{"servers": "not a map"}""")]
    [InlineData("""{"servers": 7}""")]
    public void ACanonicalDocumentWhoseServerKeyHoldsANonObjectIsRefused(string document)
    {
        JsonObject canonical = JsonNode.Parse(document)!.AsObject();

        Assert.Throws<JsonException>(() => McpWiringRepairer.Container(canonical, ["servers"]));
    }

    /// <summary>A server map the document already holds is the container, not a value to replace.</summary>
    [Fact]
    public void ACanonicalDocumentsServerMapIsReusedRatherThanReplaced()
    {
        JsonObject canonical = JsonNode.Parse("""{"servers": {"existing": {}}}""")!.AsObject();

        Assert.Same(canonical["servers"], McpWiringRepairer.Container(canonical, ["servers"]));
    }

    // ---- An environment block is the harness's, and only its root entry is this tool's to spell.

    /// <summary>
    /// Only the entry holding the repository root is rewritten; every other entry keeps its value.
    /// </summary>
    /// <remarks>
    /// The writer used to stamp the harness's root spelling into <em>every</em> environment entry, which
    /// collapses a template carrying two variables into two copies of one value. The template's other
    /// entries are not this tool's to invent.
    /// </remarks>
    [Fact]
    public void AnEnvironmentEntryThatIsNotTheRepositoryRootKeepsItsValue()
    {
        JsonObject template = JsonNode.Parse(
            """
            {
              "KYBER_WEAVE_REPO_ROOT": "${workspaceFolder}",
              "PATH": "/usr/bin",
              "KYBER_WEAVE_TOOLS": "${workspaceFolder}/tools"
            }
            """)!.AsObject();

        JsonObject environment = McpWiringRepairer.Environment(template, "${workspaceFolder}", "/repo");

        Assert.Equal(3, environment.Count);
        Assert.Equal("/repo", environment["KYBER_WEAVE_REPO_ROOT"]?.GetValue<string>());
        Assert.Equal("/usr/bin", environment["PATH"]?.GetValue<string>());
        Assert.Equal("/repo/tools", environment["KYBER_WEAVE_TOOLS"]?.GetValue<string>());
    }

    // ---- The root token is the one the registry names, not whichever value comes first.

    /// <summary>
    /// The token is read from the entry the registry names for the repository root, so a template
    /// whose first environment entry is something else still yields the right spelling.
    /// </summary>
    /// <remarks>
    /// The token decides what the check accepts and what the writer substitutes. Reading "the first
    /// non-empty environment value" made it a function of the template's key order: a harness whose
    /// block lists another variable first would have had its spelling replaced by that variable's
    /// value, and the check would then have accepted the wrong spelling of the repository root.
    /// </remarks>
    [Fact]
    public void TheRepositoryRootTokenIsTheValueOfTheEntryTheRegistryNames()
    {
        HarnessMcpEntry row = Row(
            """
            {
              "command": "kyber-weave-mcp",
              "args": ["--repo-root", "${workspaceFolder}"],
              "env": {
                "SOMETHING_ELSE": "not-the-root",
                "KYBER_WEAVE_REPO_ROOT": "${workspaceFolder}"
              }
            }
            """);

        Assert.Equal("${workspaceFolder}", row.RepositoryRootToken);
    }

    /// <summary>A template with no root entry declares nothing, so the declaration's own token is kept.</summary>
    [Fact]
    public void ATemplateWithNoRootEntryKeepsTheDeclarationToken() =>
        Assert.Equal(
            ".",
            Row("""{"command": "kyber-weave-mcp", "args": [], "env": {"PATH": "/usr/bin"}}""").RepositoryRootToken);

    /// <summary>Every catalog row keeps the spelling its runbook example documents.</summary>
    [Theory]
    [InlineData("vscode", "${workspaceFolder}")]
    [InlineData("opencode", ".")]
    [InlineData("factory", ".")]
    [InlineData("claude-code", ".")]
    [InlineData("cursor", ".")]
    [InlineData("codex", ".")]
    public void EachCatalogRowKeepsItsDocumentedRootToken(string harness, string expected) =>
        Assert.Equal(expected, HarnessMcpCatalog.Find(harness)!.RepositoryRootToken);

    // ---- A name is text, and the header it is pasted into is that text's shape.

    /// <summary>
    /// A server name that is not a bare TOML key is quoted, so it cannot close the table header and
    /// open another one.
    /// </summary>
    /// <remarks>
    /// The snippet is built by surrounding <c>key.path.name</c> with brackets. A name holding a
    /// bracket, a quote or a newline closed the header early and injected whatever followed it, and one
    /// holding a dot quietly became a nested table. Quoting is TOML's own answer for a key that is not
    /// bare, so the snippet stays pasteable rather than becoming a refusal.
    /// </remarks>
    [Theory]
    [InlineData("evil]\n[other", "[mcp_servers.\"evil]\\n[other\"]")]
    [InlineData("quo\"te", "[mcp_servers.\"quo\\\"te\"]")]
    [InlineData("with space", "[mcp_servers.\"with space\"]")]
    [InlineData("has.dot", "[mcp_servers.\"has.dot\"]")]
    [InlineData("#comment", "[mcp_servers.\"#comment\"]")]
    public void ATomlTableHeaderQuotesAServerNameThatIsNotABareKey(string name, string expectedHeader)
    {
        string snippet = McpWiringRepairer.TomlSnippet(["mcp_servers"], Server(name));

        Assert.Equal(expectedHeader, snippet.Split('\n')[0]);
        Assert.Single(snippet.Split('\n'), line => line.StartsWith('['));
    }

    /// <summary>
    /// A key path segment that is not bare is quoted on its own, and a bare one is left alone so the
    /// snippet still reads like the runbook's example.
    /// </summary>
    [Fact]
    public void ATomlTableHeaderQuotesOnlyTheSegmentsThatAreNotBareKeys()
    {
        Assert.Equal(
            """[mcp_servers.sub."not bare.\"quoted\"".kyber-weave]""",
            McpWiringRepairer.TomlSnippet(["mcp_servers", "sub", "not bare.\"quoted\""], Server(ServerName))
                .Split('\n')[0]);
    }

    private McpWiringRepairReport Repair(params string[] targets) =>
        McpWiringRepairer.Repair(Repo, McpWiringFixture.Declare(FirstPartyServer()), new LaunchCommandResolver([Bin]), targets);

    private static RequiredMcpServer FirstPartyServer() =>
        new(ServerName, [BinaryName, "--repo-root", "."], McpProvenance.FirstParty);

    private static RequiredMcpServer Server(string name) =>
        new(name, [BinaryName, "--repo-root", "."], McpProvenance.FirstParty);

    private static McpWiringFileChange SingleChange(McpWiringRepairReport report, string harness) =>
        Assert.Single(report.Files, change => change.Harness == harness);

    /// <summary>
    /// A report holding one fixable finding for <paramref name="path"/>, which is what the fix list
    /// is derived from — and the only thing it consults, so a case can hand it a path no catalog row
    /// produces.
    /// </summary>
    private static McpWiringReport ReportOffering(string path) =>
        new([
            new McpWiringFinding(
                McpWiringKind.NotWired,
                Severity.Warning,
                VsCode,
                ServerName,
                path,
                NotWiredCode,
                $"server '{ServerName}' is not wired ({path})",
                "Fix: kyber-weave docs mcp-wire --target vscode",
                McpWiringFixability.Fixable)
        ]);

    /// <summary>Separators are normalized so a diagnostic is asserted by the path's value.</summary>
    private static string DiagnosticText(McpWiringFileChange change) =>
        $"{change.Message} {change.Hint}".Replace('\\', '/');

    /// <summary>
    /// A catalog row carrying <paramref name="entry"/> as its template, so a case can arrange a
    /// template the shipped catalog does not contain.
    /// </summary>
    private static HarnessMcpEntry Row(string entry) => new()
    {
        Harness = "test-harness",
        Shape = McpConfigurationShape.Json,
        Paths = ["test.json"],
        ServerKeyPath = ["servers"],
        CommandShape = McpEntryCommandShape.CommandWithArgs,
        MarkerDirectories = [],
        InPlaySignal = "test.json",
        Fixability = McpWiringFixability.Fixable,
        CanonicalDocument = entry,
        CanonicalEntry = entry
    };

    /// <summary>One receipt record, with the digests a case wants to be the only thing wrong with it.</summary>
    private static JsonObject Record(
        string path,
        McpWiringWriteMode mode = McpWiringWriteMode.Created,
        string? preSha256 = null,
        string? preContent = null,
        string postSha256 = "00",
        IReadOnlyList<string>? servers = null)
    {
        JsonObject record = new()
        {
            ["path"] = path,
            ["mode"] = mode == McpWiringWriteMode.Created ? "created" : "inserted",
            ["servers"] = new JsonArray([.. (servers ?? [ServerName]).Select(name => JsonValue.Create(name))]),
            ["postSha256"] = postSha256,
            ["writtenAtUtc"] = "2026-10-31T00:00:00+00:00"
        };

        if (preSha256 is not null)
            record["preSha256"] = preSha256;

        if (preContent is not null)
            record["preContent"] = preContent;

        return record;
    }

    private void WriteReceipt(params JsonObject[] records) =>
        WriteReceiptText(new JsonObject
        {
            ["version"] = 1,
            ["files"] = new JsonArray([.. records.Select(record => (JsonNode)record)])
        }.ToJsonString());

    private void WriteReceiptText(string text) => McpWiringFixture.WriteText(Repo, ".kyber-weave/receipts/mcp-wiring.json", text);

    private static string Digest(byte[] content) => Convert.ToHexString(SHA256.HashData(content));

    /// <summary>
    /// Removes this process's permission to traverse <paramref name="directory"/>, or skips the calling
    /// test where that cannot be arranged: Windows has no permission bits, and an account that bypasses
    /// them — root, or one holding a capability — reads the directory anyway.
    /// </summary>
    /// <remarks>
    /// The check is the denial itself rather than the account's name, because "am I root" is a guess
    /// about a machine this suite has never seen. <paramref name="probe"/> is a file inside the denied
    /// directory: if it is still visible, this process was not denied and the case would prove nothing.
    /// </remarks>
    private static void DenyTraversal(string directory, string probe)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("This platform has no Unix permission bits, so a directory this process may not traverse cannot be arranged here.");
        }
        else
        {
            File.SetUnixFileMode(directory, UnixFileMode.None);
        }

        if (File.Exists(probe) || Directory.Exists(probe))
        {
            AllowTraversal(directory);
            Assert.Skip("This account can traverse a directory whose permission bits deny it, so the denial cannot be arranged here.");
        }
    }

    private static void AllowTraversal(string directory)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
