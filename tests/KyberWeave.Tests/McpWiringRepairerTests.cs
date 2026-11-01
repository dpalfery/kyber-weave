using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Mcp;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The bounded writer (5.4–5.11, 6.2, 6.3): which files a repair creates or extends, which it
/// refuses, what it writes in each case, and what it leaves alone.
/// </summary>
/// <remarks>
/// <para>
/// Every case builds a repository tree in a temp folder and calls the repairer over it, then reads
/// the result from disk: the assertions are about bytes on the tree, the files that do <em>not</em>
/// appear, and the diagnostic each refusal carries. Nothing here reads the developer's own
/// <c>PATH</c> — a launch binary is resolved through an injected search path (design DD5), so the
/// suite reports the same thing on every machine.
/// </para>
/// <para>
/// Two properties earn these tests their keep. The first is that refusal means <em>zero</em> bytes:
/// an existing name, a malformed file, a foreign shape, a snippet-only harness or an unresolvable
/// binary leaves the target exactly as it was found, because a half-written harness document is a
/// harness that will not start. The second is that the written document is the runbook's recipe,
/// built from what the host declared (R5.11) — an entry the check would then report as
/// <c>KW-MCP-WIRE-002</c> is a defect even if the file parses.
/// </para>
/// </remarks>
public sealed class McpWiringRepairerTests : IDisposable
{
    private const string ServerName = "kyber-weave";

    private const string BinaryName = "kyber-weave-mcp";

    private const string RefusalCode = "KW-MCP-WIRE-005";

    private const string Factory = "factory";

    private const string ClaudeCode = "claude-code";

    private const string Cursor = "cursor";

    private const string VsCode = "vscode";

    private const string OpenCode = "opencode";

    private const string Codex = "codex";

    private const string ZCode = "zcode";

    private readonly TempDirectory _root = new();

    public McpWiringRepairerTests()
    {
        Directory.CreateDirectory(Repo);
        Directory.CreateDirectory(Bin);
        Directory.CreateDirectory(EmptyBin);

        // The first-party binary is present on the default search path, because R6.2 and R6.3 make
        // an unresolvable binary a refusal: without it every case about a write would be asserting
        // a write against a tree the requirements say must be refused. The refusal cases below opt
        // into EmptyBin instead — both halves of that contract are exercised, one per fixture.
        McpWiringFixture.WriteExecutable(Bin, BinaryName);
    }

    private string Repo => Path.Combine(_root.Path, "repo");

    /// <summary>Where a case puts the fake launch binaries it wants the resolver to find.</summary>
    private string Bin => Path.Combine(_root.Path, "bin");

    /// <summary>
    /// An empty search path, for every case about an absent binary. A real directory rather than a
    /// missing one, so the resolver searches and finds nothing — the situation R6 describes.
    /// </summary>
    private string EmptyBin => Path.Combine(_root.Path, "empty-bin");

    /// <summary>The receipt the design fixes at this path; asserted as a literal, not via its owner.</summary>
    private string ReceiptPath =>
        Path.Combine(Repo, ".kyber-weave", "receipts", "mcp-wiring.json");

    public void Dispose() => _root.Dispose();

    /// <summary>
    /// The five writable harnesses: the canonical file, the key path its servers live under, and
    /// the entry body the runbook documents for <c>kyber-weave</c> (R5.11).
    /// </summary>
    public static TheoryData<string, string, string, string> WritableRecipes() => new()
    {
        {
            Factory,
            ".factory/mcp.json",
            "mcpServers",
            """{"command":"kyber-weave-mcp","args":["--repo-root","."]}"""
        },
        {
            ClaudeCode,
            ".mcp.json",
            "mcpServers",
            """{"type":"stdio","command":"kyber-weave-mcp","args":["--repo-root","."]}"""
        },
        {
            Cursor,
            ".cursor/mcp.json",
            "mcpServers",
            """{"command":"kyber-weave-mcp","args":["--repo-root","."]}"""
        },
        {
            VsCode,
            ".vscode/mcp.json",
            "servers",
            """{"type":"stdio","command":"kyber-weave-mcp","args":["--repo-root","${workspaceFolder}"],"env":{"KYBER_WEAVE_REPO_ROOT":"${workspaceFolder}"}}"""
        },
        {
            OpenCode,
            "opencode.json",
            "mcp",
            """{"type":"local","command":["kyber-weave-mcp","--repo-root","."],"enabled":true,"environment":{"KYBER_WEAVE_REPO_ROOT":"."}}"""
        }
    };

    public static TheoryData<string, string> WritableConfigFiles() => new()
    {
        { Factory, ".factory/mcp.json" },
        { ClaudeCode, ".mcp.json" },
        { Cursor, ".cursor/mcp.json" },
        { VsCode, ".vscode/mcp.json" },
        { OpenCode, "opencode.json" }
    };

    /// <summary>
    /// The writable harnesses whose configuration sits in a directory of its own, paired with whether
    /// the case starts from an absent file or an existing document. Claude Code and opencode write at
    /// the repository root, where the receipt's own directory legitimately appears beside the target,
    /// so a directory listing is not about the target there.
    /// </summary>
    public static TheoryData<string, string, bool> NestedConfigFiles() => new()
    {
        { Factory, ".factory/mcp.json", false },
        { Factory, ".factory/mcp.json", true },
        { Cursor, ".cursor/mcp.json", false },
        { Cursor, ".cursor/mcp.json", true },
        { VsCode, ".vscode/mcp.json", false },
        { VsCode, ".vscode/mcp.json", true }
    };

    /// <summary>
    /// Each writable harness gets the entry body its runbook recipe documents, keyed by the
    /// declared server name at the key path that harness actually reads (R5.11). The entry is
    /// composed by the repair, so this pins composition rather than the catalog's template.
    /// </summary>
    [Theory]
    [MemberData(nameof(WritableRecipes))]
    public void EachWritableHarnessGetsTheEntryItsRunbookRecipeDocuments(
        string harness,
        string configFile,
        string key,
        string recipeEntry)
    {
        McpWiringFixture.WriteText(Repo, configFile, $$$"""{"{{{key}}}": {}}""");

        McpWiringRepairReport report = Repair(McpWiringFixture.Declare(FirstPartyServer()), ResolverOver(Bin), harness);

        McpWiringFileChange change = SingleChange(report, harness);
        Assert.Equal(McpWiringChangeOutcome.Inserted, change.Outcome);
        Assert.Equal(configFile, change.Path);

        using JsonDocument parsed = JsonDocument.Parse(File.ReadAllText(Path.Combine(Repo, configFile)));
        JsonElement entry = parsed.RootElement.GetProperty(key).GetProperty(ServerName);

        Assert.True(
            JsonNode.DeepEquals(JsonNode.Parse(entry.GetRawText()), JsonNode.Parse(recipeEntry)),
            $"{harness}: expected the runbook entry {recipeEntry}, but wrote {entry.GetRawText()}");
    }

    /// <summary>
    /// An absent file is created holding the runbook's documented example, exactly (5.4) — not an
    /// invented document, and not a re-serialization of one.
    /// </summary>
    [Theory]
    [MemberData(nameof(WritableRecipes))]
    public void AnAbsentFileIsCreatedHoldingTheCanonicalDocument(
        string harness,
        string configFile,
        string key,
        string recipeEntry)
    {
        PutInPlay(harness);

        McpWiringRepairReport report = Repair(McpWiringFixture.Declare(FirstPartyServer()), ResolverOver(Bin), harness);

        McpWiringFileChange change = SingleChange(report, harness);
        Assert.Equal(McpWiringChangeOutcome.Created, change.Outcome);
        Assert.True(change.Written, "a repair that reported a write must have written");
        Assert.Equal(configFile, change.Path);
        Assert.Null(change.Code);

        string path = Path.Combine(Repo, configFile);
        string canonical = HarnessMcpCatalog.Find(harness)?.CanonicalDocument ?? string.Empty;
        Assert.False(string.IsNullOrWhiteSpace(canonical), $"{harness} must carry a canonical document");

        // Trailing whitespace only: a final newline is a file-writing convention, not content.
        Assert.Equal(canonical.TrimEnd('\n'), File.ReadAllText(path).TrimEnd('\n'));

        using JsonDocument parsed = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement entry = parsed.RootElement.GetProperty(key).GetProperty(ServerName);
        Assert.True(
            JsonNode.DeepEquals(JsonNode.Parse(entry.GetRawText()), JsonNode.Parse(recipeEntry)),
            $"{harness}: the canonical document must carry the documented entry, but held {entry.GetRawText()}");
    }

    /// <summary>
    /// The repair is a splice, never a re-serialization (5.6): the whole original document survives
    /// as a contiguous run with exactly one insertion in it, and the BOM, the comment and the two
    /// trailing commas are still there. They are asserted by name because the comment and the
    /// trailing commas are precisely the bytes a re-serialization drops, and they are read leniently
    /// because a strict parse rejects the very document 5.6 requires.
    /// </summary>
    [Fact]
    public void RepairInsertsWithoutMovingAnyOtherByte()
    {
        byte[] original =
        [
            0xEF,
            0xBB,
            0xBF,
            .. Encoding.UTF8.GetBytes("""
                {
                  // added by hand, do not lose this
                  "servers": {
                    "other": {
                      "command": "other-mcp"
                    },
                  },
                }
                """)
        ];
        McpWiringFixture.WriteBytes(Repo, ".vscode/mcp.json", original);

        McpWiringRepairReport report = Repair(McpWiringFixture.Declare(FirstPartyServer()), ResolverOver(Bin), VsCode);

        Assert.Equal(McpWiringChangeOutcome.Inserted, SingleChange(report, VsCode).Outcome);

        byte[] after = File.ReadAllBytes(Path.Combine(Repo, ".vscode/mcp.json"));
        int shared = CommonPrefixLength(original, after);

        Assert.True(after.Length > original.Length, "the repaired file must hold the new entry");
        Assert.True(shared > 0, "the repair must not rewrite the document from its first byte");
        Assert.True(
            after.AsSpan(shared).EndsWith(original.AsSpan(shared)),
            "every byte after the insert must be the original's, in the original order (5.6)");
        Assert.Equal(0xEF, after[0]);

        string before = Encoding.UTF8.GetString(original);
        string repaired = Encoding.UTF8.GetString(after);

        // 5.6 protects the bytes the repair did not write. The two it must not normalize away are the
        // comment and the trailing commas, because both are exactly what a re-serialization silently
        // drops: naming them separately is what makes a failure point at which one was lost.
        Assert.Contains("// added by hand, do not lose this", repaired, StringComparison.Ordinal);
        string handWrittenTail = before[before.LastIndexOf("},", StringComparison.Ordinal)..];
        Assert.Contains(handWrittenTail, repaired, StringComparison.Ordinal);

        // Removing exactly the bytes the repair inserted must give the original document back, byte
        // for byte — the whole file, not the entry's neighbourhood.
        int insertedLength = after.Length - original.Length;
        byte[] aroundTheInsert = [.. after.AsSpan(0, shared), .. after.AsSpan(shared + insertedLength)];
        Assert.Equal(original, aroundTheInsert);

        // Read the way the repair reads it. The comment and the trailing commas are bytes 5.6
        // requires to survive, and a strict parse rejects both — so a strict parse cannot read a
        // document that satisfies 5.6, and the assertions below would be unreachable.
        using JsonDocument parsed = JsonDocument.Parse(
            after.AsMemory(3),
            new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
        JsonElement servers = parsed.RootElement.GetProperty("servers");
        Assert.True(servers.TryGetProperty("other", out _), "the server already in the file must survive");
        Assert.True(servers.TryGetProperty(ServerName, out _), "the declared server must be wired");
    }

    /// <summary>
    /// The entry carries what the host declared — name and argv — inside the harness's recipe
    /// shape. Writing the catalog's <c>kyber-weave</c> body for a declared <c>codegraph</c> would
    /// produce a file the check then reports as <c>KW-MCP-WIRE-002</c>.
    /// </summary>
    [Fact]
    public void TheEntryUsesTheDeclaredNameAndCommand()
    {
        McpWiringFixture.WriteText(Repo, ".mcp.json", """{"mcpServers": {}}""");
        McpWiringFixture.WriteExecutable(Bin, "codegraph");

        McpWiringRepairReport report = Repair(
            McpWiringFixture.Declare(ThirdPartyServer()),
            ResolverOver(Bin),
            ClaudeCode);

        Assert.Equal(McpWiringChangeOutcome.Inserted, SingleChange(report, ClaudeCode).Outcome);

        using JsonDocument parsed = JsonDocument.Parse(File.ReadAllText(Path.Combine(Repo, ".mcp.json")));
        JsonElement entry = parsed.RootElement.GetProperty("mcpServers").GetProperty("codegraph");

        Assert.True(
            JsonNode.DeepEquals(
                JsonNode.Parse(entry.GetRawText()),
                JsonNode.Parse("""{"type":"stdio","command":"codegraph","args":["serve","--mcp"]}""")),
            $"the entry must carry the declared argv, but held {entry.GetRawText()}");
    }

    /// <summary>
    /// An entry with the same server name is refused, never merged or overwritten (5.5), and the
    /// file is left byte-identical.
    /// </summary>
    [Fact]
    public void AnExistingServerNameIsRefusedAndTheFileIsLeftByteIdentical()
    {
        McpWiringFixture.WriteText(Repo,
            ".vscode/mcp.json",
            """{"servers": {"kyber-weave": {"command": "kyber-weave-mcp"}}}""");
        byte[] before = File.ReadAllBytes(Path.Combine(Repo, ".vscode/mcp.json"));

        McpWiringRepairReport report = Repair(McpWiringFixture.Declare(FirstPartyServer()), ResolverOver(Bin), VsCode);

        McpWiringFileChange change = SingleChange(report, VsCode);
        Assert.Equal(McpWiringChangeOutcome.Refused, change.Outcome);
        Assert.Equal(RefusalCode, change.Code);
        Assert.Equal(RefusalCode, McpWiringRepairer.RefusalCode);
        Assert.False(change.Written);
        Assert.False(string.IsNullOrWhiteSpace(change.Message), "a refusal must say why");

        Assert.Equal(before, File.ReadAllBytes(Path.Combine(Repo, ".vscode/mcp.json")));
        Assert.Null(McpWiringReceipt.Load(Repo).Find(".vscode/mcp.json"));
    }

    /// <summary>
    /// One already-wired server refuses the whole file: the repair does not write "the other half"
    /// of a request, because a partially applied write is what a receipt cannot honestly record.
    /// </summary>
    [Fact]
    public void OneAlreadyWiredServerRefusesTheWholeFile()
    {
        McpWiringFixture.WriteText(Repo, ".mcp.json", """{"mcpServers": {"codegraph": {"command": "codegraph"}}}""");
        byte[] before = File.ReadAllBytes(Path.Combine(Repo, ".mcp.json"));
        McpWiringFixture.WriteExecutable(Bin, "codegraph");

        McpWiringRepairReport report = Repair(
            McpWiringFixture.Declare(FirstPartyServer(), ThirdPartyServer()),
            ResolverOver(Bin),
            ClaudeCode);

        McpWiringFileChange change = SingleChange(report, ClaudeCode);
        Assert.Equal(McpWiringChangeOutcome.Refused, change.Outcome);
        Assert.Equal(RefusalCode, change.Code);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(Repo, ".mcp.json")));
    }

    /// <summary>
    /// A malformed or structurally foreign file is refused with its path, and nothing is written to
    /// it (5.7). Writing into a document that is not what the harness expects replaces a diagnosable
    /// refusal with a harness that silently starts with no tools.
    /// </summary>
    [Theory]
    [InlineData("""{"servers": """)]
    [InlineData("""{"servers": }""")]
    [InlineData("""{"servers": []}""")]
    [InlineData("[]")]
    public void AMalformedOrForeignFileIsRefusedAndLeftByteIdentical(string content)
    {
        McpWiringFixture.WriteText(Repo, ".vscode/mcp.json", content);
        byte[] before = File.ReadAllBytes(Path.Combine(Repo, ".vscode/mcp.json"));

        McpWiringRepairReport report = Repair(McpWiringFixture.Declare(FirstPartyServer()), ResolverOver(Bin), VsCode);

        McpWiringFileChange change = SingleChange(report, VsCode);
        Assert.Equal(McpWiringChangeOutcome.Refused, change.Outcome);
        Assert.Equal(RefusalCode, change.Code);
        Assert.Contains(".vscode/mcp.json", DiagnosticText(change), StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(Repo, ".vscode/mcp.json")));
    }

    /// <summary>
    /// A refusal is per target, not per run: the targets that can be written still are (design,
    /// error table — "other targets proceed").
    /// </summary>
    [Fact]
    public void ARefusedTargetDoesNotStopAnotherTarget()
    {
        McpWiringFixture.WriteText(Repo, ".vscode/mcp.json", """{"servers": {"kyber-weave": {}}}""");
        byte[] refusedBefore = File.ReadAllBytes(Path.Combine(Repo, ".vscode/mcp.json"));

        McpWiringRepairReport report = Repair(
            McpWiringFixture.Declare(FirstPartyServer()),
            ResolverOver(Bin),
            VsCode,
            ClaudeCode);

        Assert.Equal(McpWiringChangeOutcome.Refused, SingleChange(report, VsCode).Outcome);
        Assert.Equal(McpWiringChangeOutcome.Created, SingleChange(report, ClaudeCode).Outcome);

        Assert.Equal(refusedBefore, File.ReadAllBytes(Path.Combine(Repo, ".vscode/mcp.json")));
        Assert.True(
            File.Exists(Path.Combine(Repo, ".mcp.json")),
            "the target that could be written must still be written");
    }

    /// <summary>
    /// Codex and ZCode are checked but never written in v1 (DD11, DD12, DD18, DD19): the repair
    /// refuses the harness and no file appears for it.
    /// </summary>
    [Theory]
    [InlineData(Codex, ".codex/config.toml")]
    [InlineData(ZCode, "zcode.json")]
    public void ASnippetOnlyHarnessIsRefusedAndNoFileIsWritten(string harness, string configFile)
    {
        // In play, so the refusal under test is the writability one and not an undetected harness.
        PutInPlay(harness);

        McpWiringRepairReport report = Repair(McpWiringFixture.Declare(FirstPartyServer()), ResolverOver(Bin), harness);

        McpWiringFileChange change = SingleChange(report, harness);
        Assert.Equal(McpWiringChangeOutcome.Refused, change.Outcome);
        Assert.False(change.Written);
        Assert.False(string.IsNullOrWhiteSpace(change.Message), "a refusal must say why, and point at the snippet");
        Assert.False(File.Exists(Path.Combine(Repo, configFile)));
        Assert.False(File.Exists(ReceiptPath));
    }

    /// <summary>
    /// A non-writable harness is refused <em>with</em> the runbook snippet to paste, which is the
    /// whole remedy available for it — a pointer without the snippet sends the operator to a
    /// document that may not be open.
    /// </summary>
    [Fact]
    public void TheCodexRefusalCarriesItsRunbookSnippet()
    {
        McpWiringRepairReport report = Repair(McpWiringFixture.Declare(FirstPartyServer()), ResolverOver(Bin), Codex);

        string snippet = SingleChange(report, Codex).Snippet ?? string.Empty;

        Assert.Contains("[mcp_servers.kyber-weave]", snippet, StringComparison.Ordinal);
        Assert.Contains(BinaryName, snippet, StringComparison.Ordinal);
    }

    /// <summary>
    /// A harness with no repository-scope file at all — Copilot's coding agent — is refused rather
    /// than invented a path for (2.5).
    /// </summary>
    [Fact]
    public void AHarnessWithNoRepositoryScopeFileIsRefused()
    {
        McpWiringRepairReport report = Repair(
            McpWiringFixture.Declare(FirstPartyServer()),
            ResolverOver(Bin),
            "copilot-coding-agent");

        McpWiringFileChange change = SingleChange(report, "copilot-coding-agent");
        Assert.Equal(McpWiringChangeOutcome.Refused, change.Outcome);
        Assert.False(change.Written);
        Assert.Null(change.Path);
    }

    /// <summary>
    /// An absent first-party binary refuses the server for every harness and points at the install
    /// repair, not at harness configuration (6.3): the configuration is not what is broken.
    /// </summary>
    [Theory]
    [MemberData(nameof(WritableConfigFiles))]
    public void AnAbsentFirstPartyBinaryRefusesTheServerForEveryHarness(string harness, string configFile)
    {
        PutInPlay(harness);

        McpWiringRepairReport report = Repair(McpWiringFixture.Declare(FirstPartyServer()), ResolverOver(EmptyBin), harness);

        McpWiringFileChange change = SingleChange(report, harness);
        Assert.Equal(McpWiringChangeOutcome.Refused, change.Outcome);
        Assert.False(change.Written);
        Assert.False(File.Exists(Path.Combine(Repo, configFile)));
        Assert.Contains("kyber-weave update", DiagnosticText(change), StringComparison.Ordinal);
    }

    /// <summary>
    /// An absent third-party binary is refused too, and nothing is installed (6.1, 6.2): a written
    /// entry whose launch command cannot work turns a diagnosable absence into a harness-side
    /// startup failure that blames the configuration file.
    /// </summary>
    [Fact]
    public void AnAbsentThirdPartyBinaryIsRefusedRatherThanWrittenBroken()
    {
        McpWiringRepairReport report = Repair(McpWiringFixture.Declare(ThirdPartyServer()), ResolverOver(EmptyBin), ClaudeCode);

        McpWiringFileChange change = SingleChange(report, ClaudeCode);
        Assert.Equal(McpWiringChangeOutcome.Refused, change.Outcome);
        Assert.False(change.Written);
        Assert.Contains("codegraph", DiagnosticText(change), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(Repo, ".mcp.json")));
    }

    /// <summary>
    /// 6.2 refuses "that server's entry", which is a per-server rule, not a per-file one: when one
    /// declared server launches and another does not, the harness still gets the entry that works,
    /// the absent server's entry is left out, and the omission is named. Writing the whole
    /// declaration would hand the operator a harness that fails at startup and blames the
    /// configuration file; refusing the whole file would withhold a server that is ready to wire
    /// because a different one is not installed yet.
    /// </summary>
    /// <remarks>
    /// The other half of the same rule is checked by
    /// <see cref="ADeclarationWhereNoServerResolvesIsRefusedOutright"/>: the whole-file refusal is
    /// this rule applied where every server is excluded, not a second policy.
    /// </remarks>
    [Fact]
    public void ADeclarationWithOneUnresolvableServerWritesTheRestAndNamesWhatItOmitted()
    {
        McpWiringFixture.WriteText(Repo, ".mcp.json", """{"mcpServers": {}}""");

        // Bin holds the first-party binary and nothing else, so 'kyber-weave-mcp' resolves and
        // 'codegraph' does not: the mixed declaration 6.2 is written for, from one search path.
        McpWiringRepairReport report = Repair(
            McpWiringFixture.Declare(FirstPartyServer(), ThirdPartyServer()),
            ResolverOver(Bin),
            ClaudeCode);

        McpWiringFileChange change = SingleChange(report, ClaudeCode);

        // Not refused: one server is writable, and the file is written for it.
        Assert.Equal(McpWiringChangeOutcome.Inserted, change.Outcome);
        Assert.True(change.Written, "the resolvable server's entry was written, so bytes reached the file");
        Assert.Equal(ServerName, Assert.Single(change.Servers));

        using JsonDocument parsed = JsonDocument.Parse(File.ReadAllText(Path.Combine(Repo, ".mcp.json")));
        JsonElement servers = parsed.RootElement.GetProperty("mcpServers");

        Assert.True(
            servers.TryGetProperty(ServerName, out _),
            "the server whose binary resolves must still be wired (6.2 refuses only the unresolved one)");
        Assert.False(
            servers.TryGetProperty("codegraph", out _),
            "the entry for the server whose binary is absent must not be written, for this harness or any other");

        // The omission is reported rather than left for the operator to notice: a partial write that
        // reads as a complete one is the failure this note exists to prevent.
        string diagnostic = DiagnosticText(change);
        Assert.Contains("codegraph", diagnostic, StringComparison.Ordinal);
        Assert.Contains(".mcp.json", diagnostic, StringComparison.Ordinal);

        // The undo record names only what was written, so a removal never claims to take out an entry
        // the repair declined to insert.
        McpWiringReceiptRecord? record = McpWiringReceipt.Load(Repo).Find(".mcp.json");
        Assert.NotNull(record);
        Assert.Equal(ServerName, Assert.Single(record.Servers));
    }

    /// <summary>
    /// The whole-file refusal is the per-server rule applied where no server survives it: with every
    /// declared binary absent there is nothing 6.2 permits writing, so the file is refused outright
    /// rather than created holding an empty server map — a created file that wires nothing would be
    /// a worse answer than a refusal that says why.
    /// </summary>
    [Fact]
    public void ADeclarationWhereNoServerResolvesIsRefusedOutright()
    {
        McpWiringRepairReport report = Repair(
            McpWiringFixture.Declare(FirstPartyServer(), ThirdPartyServer()),
            ResolverOver(EmptyBin),
            ClaudeCode);

        McpWiringFileChange change = SingleChange(report, ClaudeCode);
        Assert.Equal(McpWiringChangeOutcome.Refused, change.Outcome);
        Assert.Equal(RefusalCode, change.Code);
        Assert.False(change.Written);
        Assert.False(File.Exists(Path.Combine(Repo, ".mcp.json")), "a refusal creates nothing");
        Assert.False(File.Exists(ReceiptPath), "a refusal writes no receipt either");

        // With a first-party binary missing the refusal names the broken installation (6.3), which is
        // the root cause an operator can act on; the harness file is not what is wrong.
        Assert.Contains(BinaryName, DiagnosticText(change), StringComparison.Ordinal);
    }

    /// <summary>
    /// A dry run is the same computation with the write withheld: it reports exactly the outcomes
    /// the real run reports, writes not one byte to a target, and does not create the receipt —
    /// not even the receipt (5.10).
    /// </summary>
    [Fact]
    public void ADryRunWritesNothingAndReportsWhatTheRealRunWould()
    {
        McpWiringFixture.WriteText(Repo, ".vscode/mcp.json", """{"servers": {"kyber-weave": {}}}""");
        byte[] refusedBefore = File.ReadAllBytes(Path.Combine(Repo, ".vscode/mcp.json"));

        McpConfig config = McpWiringFixture.Declare(FirstPartyServer());
        LaunchCommandResolver resolver = ResolverOver(Bin);

        McpWiringRepairReport dryRun = McpWiringRepairer.Repair(
            Repo,
            config,
            resolver,
            [VsCode, ClaudeCode],
            dryRun: true);

        Assert.True(dryRun.DryRun);
        Assert.All(dryRun.Files, change => Assert.False(change.Written, $"{change.Harness} wrote during a dry run"));
        Assert.Equal(refusedBefore, File.ReadAllBytes(Path.Combine(Repo, ".vscode/mcp.json")));
        Assert.False(File.Exists(Path.Combine(Repo, ".mcp.json")), "a dry run creates nothing");
        Assert.False(File.Exists(ReceiptPath), "a dry run writes no receipt");

        McpWiringRepairReport real = McpWiringRepairer.Repair(
            Repo,
            config,
            resolver,
            [VsCode, ClaudeCode],
            dryRun: false);

        Assert.False(real.DryRun);

        // 5.10 makes the two runs disagree on exactly one field: "writes nothing" is what Written=false
        // means, so folding it into this equality would make both readings unsatisfiable. Everything
        // else — the change set, each outcome, the servers, the diagnostics, the hint and the snippet
        // — must be identical, and Written is then asserted on each side on its own terms.
        Assert.Equal(Describe(dryRun), Describe(real));
        Assert.All(
            real.Files,
            change => Assert.Equal(
                change.Outcome is McpWiringChangeOutcome.Created or McpWiringChangeOutcome.Inserted,
                change.Written));
    }

    /// <summary>
    /// A target that cannot be written is reported with the file it names rather than thrown out of
    /// the command, and no receipt claims a write that did not happen (design, error table: "IO
    /// failure (read or write)").
    /// </summary>
    [Fact]
    public void AnUnwritableTargetIsReportedWithItsPathInsteadOfThrown()
    {
        Directory.CreateDirectory(Path.Combine(Repo, ".mcp.json"));

        McpWiringRepairReport report = Repair(McpWiringFixture.Declare(FirstPartyServer()), ResolverOver(Bin), ClaudeCode);

        McpWiringFileChange change = SingleChange(report, ClaudeCode);
        Assert.Equal(McpWiringChangeOutcome.Refused, change.Outcome);
        Assert.False(change.Written);
        Assert.Contains(".mcp.json", DiagnosticText(change), StringComparison.Ordinal);
        Assert.False(File.Exists(ReceiptPath));
    }

    /// <summary>
    /// The repair writes the file the check accepts, for every harness it writes at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two halves of this feature are the writer and the checker, and a repository only gets
    /// wired if they agree on what a wired entry looks like. They did not: the repair substituted the
    /// harness's own repository-root token — <c>${workspaceFolder}</c> for VS Code, exactly as 5.11
    /// requires — and the check then compared that against the declaration's <c>.</c> as raw text and
    /// reported <c>KW-MCP-WIRE-002</c> on the file the repair had just written. The consequences were
    /// all downstream of that one comparison: an <c>ok</c> line became a warning on the very next
    /// <c>docs init</c> (3.5), a second <c>mcp-wire</c> refused its own earlier success with
    /// <c>KW-MCP-WIRE-005</c>, and the prompt offered a repair that 5.5 guarantees will be refused.
    /// </para>
    /// <para>
    /// The case is asserted per writable harness rather than once, because it is the round trip that
    /// has to hold, not one harness's spelling: a fix that special-cased VS Code inside the inspector
    /// would pass a single assertion and leave the next harness with a substitution to do the same.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(WritableConfigFiles))]
    public void WhatTheRepairWritesIsWhatTheCheckAccepts(string harness, string configFile)
    {
        PutInPlay(harness);
        McpConfig config = McpWiringFixture.Declare(FirstPartyServer());
        LaunchCommandResolver resolver = ResolverOver(Bin);

        McpWiringRepairReport repair = Repair(config, resolver, harness);
        McpWiringFileChange change = SingleChange(repair, harness);
        Assert.Equal(McpWiringChangeOutcome.Created, change.Outcome);
        Assert.True(File.Exists(Path.Combine(Repo, configFile)), $"{harness}: nothing was written");

        McpWiringReport inspection = McpWiringInspector.Inspect(Repo, config, resolver);

        string[] found =
        [
            .. inspection.Findings
                .Where(finding => string.Equals(finding.Harness, harness, StringComparison.Ordinal))
                .Select(finding => $"{finding.Code} {finding.Message}")
        ];
        Assert.True(
            found.Length == 0,
            $"{harness}: the repair wrote {configFile}, and the check then reported: {string.Join("; ", found)}");
    }

    /// <summary>
    /// The round trip holds for the splice path too: an entry inserted into a document that already
    /// exists is accepted by the check, and a second repair over the result has nothing left to write.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="WhatTheRepairWritesIsWhatTheCheckAccepts"/> proves the property for a file the repair
    /// created. Insertion meets a different comparison inside the repair: a created document is
    /// composed from the catalog and never read back, while an insertion is planned against the
    /// operator's bytes and then inspected as they landed. A tolerance that held only on the creation
    /// path would pass that case and still reject the file written over an existing configuration —
    /// which is the ordinary state of a repository that already runs another MCP server.
    /// </para>
    /// <para>
    /// VS Code is the harness this case needs, because its recipe writes <c>${workspaceFolder}</c>
    /// where the declaration says <c>.</c> (5.11). It is the one harness whose written entry is
    /// textually different from what the host declared, so it is exactly the substitution the
    /// comparison had to learn to accept.
    /// </para>
    /// </remarks>
    [Fact]
    public void AnInsertRoundTripIsAcceptedByTheCheck()
    {
        const string ConfigFile = ".vscode/mcp.json";
        McpWiringFixture.WriteText(
            Repo,
            ConfigFile,
            """{"servers": {"other": {"command": "other-mcp"}}}""");

        byte[] original = File.ReadAllBytes(Path.Combine(Repo, ConfigFile));
        McpConfig config = McpWiringFixture.Declare(FirstPartyServer());
        LaunchCommandResolver resolver = ResolverOver(Bin);

        McpWiringFileChange change = SingleChange(Repair(config, resolver, VsCode), VsCode);
        Assert.Equal(McpWiringChangeOutcome.Inserted, change.Outcome);
        Assert.True(change.Written, "the splice must report the write that produced it");

        byte[] after = File.ReadAllBytes(Path.Combine(Repo, ConfigFile));

        // The operator's document survives as one contiguous run with a single insertion in it: drop
        // the bytes the repair added and the file comes back byte for byte (5.6).
        Assert.True(after.Length > original.Length, "the repaired file must hold the new entry");
        int shared = CommonPrefixLength(original, after);
        Assert.True(shared > 0, "the repair must not rewrite the document from its first byte");
        int insertedLength = after.Length - original.Length;
        byte[] aroundTheInsert = [.. after.AsSpan(0, shared), .. after.AsSpan(shared + insertedLength)];
        Assert.Equal(original, aroundTheInsert);

        using JsonDocument parsed = JsonDocument.Parse(after);
        JsonElement servers = parsed.RootElement.GetProperty("servers");

        // The operator's entry sits in the same map the repair inserted into, so an insert that rebuilt
        // the map rather than splicing one entry would have moved or dropped it.
        Assert.Equal("other-mcp", servers.GetProperty("other").GetProperty("command").GetString());

        // The harness's own root token, not the declaration's '.': the substitution is the thing the
        // check has to read back as the declared repository.
        JsonElement entry = servers.GetProperty(ServerName);
        Assert.Equal("${workspaceFolder}", entry.GetProperty("args")[1].GetString());

        McpWiringReport inspection = McpWiringInspector.Inspect(Repo, config, resolver);

        string[] found =
        [
            .. inspection.Findings
                .Where(finding => string.Equals(finding.Harness, VsCode, StringComparison.Ordinal))
                .Select(finding => $"{finding.Code} {finding.Message}")
        ];
        Assert.True(
            found.Length == 0,
            $"the repair inserted into {ConfigFile}, and the check then reported: {string.Join("; ", found)}");

        // "Nothing left to write" is the same condition as no finding: the check reports nothing for a
        // server wired with the declared command, and that is the decision mcp-wire reads to skip a
        // harness. The repair's own vocabulary for it is a change that wrote nothing, and identical
        // bytes are the proof the second run left the first run's success alone (5.5, 5.12).
        McpWiringFileChange second = SingleChange(Repair(config, resolver, VsCode), VsCode);
        Assert.False(
            second.Written,
            $"the second repair reported {second.Outcome} over a file that already held the entry");
        Assert.Equal(after, File.ReadAllBytes(Path.Combine(Repo, ConfigFile)));
    }

    /// <summary>
    /// A successful write leaves nothing in the target's directory that was not there before, apart
    /// from the target itself: the temporary file the atomic write renames into place is gone by the
    /// time the repair returns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The write is atomic because a harness configuration is a file the whole team inherits: a run
    /// killed between the truncation and the last byte leaves a document every teammate then commits.
    /// The rename that makes it atomic goes through a unique temporary sibling, and the observable half
    /// of that claim is the directory afterwards. A leftover <c>.tmp</c> is litter in a directory the
    /// operator never asked this tool to touch, and <c>git add .</c> commits it.
    /// </para>
    /// <para>
    /// Both write paths are covered, because a temporary file is written on the way to either — an
    /// absent file, which is created, and a document already present, which is spliced into.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(NestedConfigFiles))]
    public void ARepairLeavesNoTemporaryFileInTheTargetsDirectory(
        string harness,
        string configFile,
        bool fileExists)
    {
        PutInPlay(harness);

        // An empty document is enough to take the insert path: a missing server key is writable and
        // the splice inserts the map, so the entry's own shape is not this case's subject.
        if (fileExists)
            McpWiringFixture.WriteText(Repo, configFile, "{}");

        string fullPath = Path.Combine(Repo, configFile);
        string directory = Path.GetDirectoryName(fullPath)!;
        string[] before = FileNamesIn(directory);

        McpWiringFileChange change = SingleChange(
            Repair(McpWiringFixture.Declare(FirstPartyServer()), ResolverOver(Bin), harness),
            harness);
        Assert.True(change.Written, $"{harness}: the repair reported {change.Outcome} and wrote nothing");

        string[] expected =
            [.. before.Append(Path.GetFileName(fullPath)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        string[] after = [.. FileNamesIn(directory).Order(StringComparer.Ordinal)];

        Assert.True(
            expected.SequenceEqual(after, StringComparer.Ordinal),
            $"{harness}: {directory} must hold exactly [{string.Join(", ", expected)}] after the repair, "
                + $"but held [{string.Join(", ", after)}]");
    }

    /// <summary>
    /// A refused write leaves no temporary file behind either.
    /// </summary>
    /// <remarks>
    /// A successful write consumes the temporary file by renaming it, so a listing taken afterwards
    /// cannot tell a cleaned-up write from one that never made a temporary file. The failure path can:
    /// the target here is a directory, so the temporary file is written and the rename onto it fails —
    /// the exact window in which only the write's own cleanup removes the file. The refusal is
    /// asserted alongside so the case says which of the two outcomes it arranged, rather than leaving
    /// a passing listing to be read as "the write succeeded and tidied up".
    /// </remarks>
    [Fact]
    public void ARefusedWriteLeavesNoTemporaryFileInTheTargetsDirectory()
    {
        Directory.CreateDirectory(Path.Combine(Repo, ".mcp.json"));
        string[] before = FileNamesIn(Repo);

        McpWiringFileChange change = SingleChange(
            Repair(McpWiringFixture.Declare(FirstPartyServer()), ResolverOver(Bin), ClaudeCode),
            ClaudeCode);

        Assert.Equal(McpWiringChangeOutcome.Refused, change.Outcome);
        Assert.False(change.Written);

        string[] after = [.. FileNamesIn(Repo).Order(StringComparer.Ordinal)];

        Assert.True(
            before.Order(StringComparer.Ordinal).SequenceEqual(after, StringComparer.Ordinal),
            $"{Repo} must hold exactly [{string.Join(", ", before)}] after a refused write, "
                + $"but held [{string.Join(", ", after)}]");
    }

    /// <summary>
    /// No remedy is offered for a gap the repair would refuse to close.
    /// </summary>
    /// <remarks>
    /// <c>docs init</c> derives its repair question from the findings, so a finding that carries
    /// <see cref="McpWiringFixability.Fixable"/> is a file the operator is asked to consent to
    /// writing. A <c>CommandDiffers</c> finding is by definition about an entry that already exists,
    /// and 5.5 says the repair never modifies, merges, or overwrites one — so offering it asks the
    /// operator to approve a write that is guaranteed to come back as <c>KW-MCP-WIRE-005</c>. The
    /// assertion below is the two halves of that contradiction, in the order the operator meets them.
    /// </remarks>
    [Fact]
    public void ARemedyIsNotOfferedForAGapTheRepairWouldRefuse()
    {
        McpWiringFixture.WriteText(
            Repo,
            ".mcp.json",
            """{"mcpServers": {"kyber-weave": {"command": "kyber-weave-mcp", "args": ["--repo-root", "/somewhere/else"]}}}""");

        McpConfig config = McpWiringFixture.Declare(FirstPartyServer());
        LaunchCommandResolver resolver = ResolverOver(Bin);
        McpWiringReport inspection = McpWiringInspector.Inspect(Repo, config, resolver);

        McpWiringFinding finding = Assert.Single(inspection.Findings, candidate => candidate.Harness == ClaudeCode);
        Assert.Equal(McpWiringKind.CommandDiffers, finding.Kind);

        IReadOnlyList<McpWiringFix> offered = McpWiringFixes.Derive(inspection, Repo);
        Assert.True(
            offered.Count == 0,
            "the prompt was offered a write the repair refuses: "
                + string.Join("; ", offered.Select(fix => $"{fix.Harness} ({fix.Path}) adding {string.Join(", ", fix.Servers)}")));

        McpWiringFileChange refusal = SingleChange(Repair(config, resolver, ClaudeCode), ClaudeCode);
        Assert.Equal(McpWiringChangeOutcome.Refused, refusal.Outcome);
        Assert.False(File.Exists(ReceiptPath));
    }

    /// <summary>
    /// A configuration file that is a symbolic link is refused, and the file it points at is left
    /// byte-identical.
    /// </summary>
    /// <remarks>
    /// Requirement 2.6 confines the whole feature to the repository, and 5.2 says a write lands in a
    /// repository-scope file or nowhere. A link in the tree defeats both without any output saying
    /// so: the path is inside the repository, and the bytes land wherever the link points. The case
    /// is a developer sharing one MCP configuration across checkouts — <c>ln -s ~/.mcp.json
    /// .mcp.json</c> — which is exactly the state that turns a repository-scope write into a write to
    /// the user scope (ADR 0051).
    /// </remarks>
    [Fact]
    public void ALinkedTargetIsRefusedAndItsTargetIsLeftByteIdentical()
    {
        string outside = WriteOutsideRepository(
            "user-scope/mcp.json",
            """{"mcpServers": {"other": {"command": "other-mcp"}}}""");
        byte[] before = File.ReadAllBytes(outside);
        string link = Path.Combine(Repo, ".mcp.json");
        McpWiringFixture.CreateSymlink(link, outside);

        McpWiringRepairReport report = Repair(
            McpWiringFixture.Declare(FirstPartyServer()),
            ResolverOver(Bin),
            ClaudeCode);

        McpWiringFileChange change = SingleChange(report, ClaudeCode);
        Assert.Equal(McpWiringChangeOutcome.Refused, change.Outcome);
        Assert.False(change.Written);
        AssertRefusalNamesTheLink(change, ".mcp.json");
        Assert.Equal(RefusalCode, change.Code);
        Assert.Equal(before, File.ReadAllBytes(outside));
        Assert.NotNull(new FileInfo(link).LinkTarget);
        Assert.False(File.Exists(ReceiptPath), "a refusal writes no receipt");
    }

    /// <summary>
    /// Removal refuses a linked target too, and leaves the file it points at byte-identical.
    /// </summary>
    /// <remarks>
    /// Removal is the other half of the same boundary: it restores the pre-write bytes the receipt
    /// recorded, and restoring through a link writes those bytes into whichever file the link names.
    /// The fixture arranges the exact situation that reaches the write — the link's target holds the
    /// post-write bytes, so the digest matches and nothing but the link stands between the removal
    /// and a write outside the repository.
    /// </remarks>
    [Fact]
    public void RemovalRefusesALinkedTargetAndLeavesTheLinkedFileByteIdentical()
    {
        McpWiringFixture.WriteText(Repo, ".mcp.json", """{"mcpServers": {}}""");
        Assert.Equal(
            McpWiringChangeOutcome.Inserted,
            SingleChange(
                Repair(McpWiringFixture.Declare(FirstPartyServer()), ResolverOver(Bin), ClaudeCode),
                ClaudeCode).Outcome);

        string link = Path.Combine(Repo, ".mcp.json");
        string outside = Path.Combine(_root.Path, "user-scope", "mcp.json");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        File.Move(link, outside);
        McpWiringFixture.CreateSymlink(link, outside);
        byte[] written = File.ReadAllBytes(outside);

        McpWiringFileChange change = Assert.Single(McpWiringRepairer.Unwire(Repo, [ClaudeCode]).Files);

        Assert.Equal(McpWiringChangeOutcome.Refused, change.Outcome);
        Assert.False(change.Written);
        AssertRefusalNamesTheLink(change, ".mcp.json");
        Assert.Equal(written, File.ReadAllBytes(outside));
        Assert.NotNull(new FileInfo(link).LinkTarget);
    }

    private McpWiringRepairReport Repair(
        McpConfig config,
        LaunchCommandResolver resolver,
        params string[] targets) =>
        McpWiringRepairer.Repair(Repo, config, resolver, targets, dryRun: false);

    private LaunchCommandResolver ResolverOver(params string[] searchPaths) => new(searchPaths);

    private static RequiredMcpServer FirstPartyServer() =>
        new(ServerName, [BinaryName, "--repo-root", "."], McpProvenance.FirstParty);

    private static RequiredMcpServer ThirdPartyServer() =>
        new(
            "codegraph",
            ["codegraph", "serve", "--mcp"],
            McpProvenance.ThirdParty,
            McpRequirementLevel.Recommended,
            "codegraph init .");

    private static McpWiringFileChange SingleChange(McpWiringRepairReport report, string harness) =>
        Assert.Single(report.Files, change => change.Harness == harness);

    /// <summary>
    /// A refusal is one diagnostic rendered across message and hint; what matters is what the
    /// operator is told, not which field carries it. Separators are normalized so a path in the
    /// diagnostic is asserted by value rather than by the platform's separator.
    /// </summary>
    private static string DiagnosticText(McpWiringFileChange change) =>
        $"{change.Message} {change.Hint}".Replace('\\', '/');

    /// <summary>
    /// A change as a dry run and a real run must agree on it (5.10): everything the change says about
    /// the target and why, and nothing about whether a byte reached it. <c>Written</c> is deliberately
    /// absent — it is the one field the two runs are required to disagree on, so the caller asserts it
    /// separately in both directions rather than comparing it here.
    /// </summary>
    private static string[] Describe(McpWiringRepairReport report) =>
        [
            .. report.Files.Select(change =>
                $"{change.Harness}|{change.Path}|{change.Outcome}|{string.Join(',', change.Servers)}"
                + $"|{change.Code}|{change.Message}|{change.Hint}|{change.Snippet}")
        ];

    private static int CommonPrefixLength(byte[] left, byte[] right)
    {
        int limit = Math.Min(left.Length, right.Length);

        for (int index = 0; index < limit; index++)
        {
            if (left[index] != right[index])
                return index;
        }

        return limit;
    }

    /// <summary>
    /// The file names directly inside <paramref name="directory"/> — one level, so a name is compared
    /// as the platform stored it and no walk can leave the fixture's own temp tree. An absent
    /// directory holds nothing, which is the state a created target starts from.
    /// </summary>
    private static string[] FileNamesIn(string directory) =>
        Directory.Exists(directory)
            ? [.. new DirectoryInfo(directory).EnumerateFiles().Select(file => file.Name)]
            : [];

    /// <summary>
    /// Puts a harness in play through its marker directory, so a case is about what the repair does
    /// for a harness that <em>is</em> in play rather than about detection. A harness with no marker
    /// besides its own file (Claude Code, opencode) cannot be put in play this way, and its absent
    /// file is created anyway: for those two, absence means "unknown", not "not in play" (2.4).
    /// </summary>
    private void PutInPlay(string harness)
    {
        HarnessMcpEntry? entry = HarnessMcpCatalog.Find(harness);
        Assert.NotNull(entry);

        foreach (string marker in entry.MarkerDirectories)
            Directory.CreateDirectory(Path.Combine(Repo, marker));
    }

    /// <summary>
    /// Writes beside the repository root — where a user-scope file would sit, and where a link in
    /// the tree would point — and returns its full path.
    /// </summary>
    private string WriteOutsideRepository(string relativePath, string content)
    {
        McpWiringFixture.WriteText(_root.Path, relativePath, content);

        return Path.Combine(_root.Path, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>
    /// A refusal of a linked target has to say two things: which file, and that it is a link. Without
    /// the second the operator is told a file inside their repository was refused, which is the
    /// opposite of what happened.
    /// </summary>
    private static void AssertRefusalNamesTheLink(McpWiringFileChange change, string path)
    {
        string text = DiagnosticText(change);

        Assert.Contains(path, text, StringComparison.Ordinal);
        Assert.True(
            text.Contains("symbolic link", StringComparison.OrdinalIgnoreCase)
                || text.Contains("symlink", StringComparison.OrdinalIgnoreCase),
            $"the refusal must say {path} is a symbolic link, but said: {text}");
    }
}
