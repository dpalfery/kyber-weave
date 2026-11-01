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
        WriteBinary(BinaryName);
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
        WriteInRepository(configFile, $$$"""{"{{{key}}}": {}}""");

        McpWiringRepairReport report = Repair(Declare(FirstPartyServer()), ResolverOver(Bin), harness);

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

        McpWiringRepairReport report = Repair(Declare(FirstPartyServer()), ResolverOver(Bin), harness);

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
        WriteBytes(".vscode/mcp.json", original);

        McpWiringRepairReport report = Repair(Declare(FirstPartyServer()), ResolverOver(Bin), VsCode);

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
        WriteInRepository(".mcp.json", """{"mcpServers": {}}""");
        WriteBinary("codegraph");

        McpWiringRepairReport report = Repair(
            Declare(ThirdPartyServer()),
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
        WriteInRepository(
            ".vscode/mcp.json",
            """{"servers": {"kyber-weave": {"command": "kyber-weave-mcp"}}}""");
        byte[] before = File.ReadAllBytes(Path.Combine(Repo, ".vscode/mcp.json"));

        McpWiringRepairReport report = Repair(Declare(FirstPartyServer()), ResolverOver(Bin), VsCode);

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
        WriteInRepository(".mcp.json", """{"mcpServers": {"codegraph": {"command": "codegraph"}}}""");
        byte[] before = File.ReadAllBytes(Path.Combine(Repo, ".mcp.json"));
        WriteBinary("codegraph");

        McpWiringRepairReport report = Repair(
            Declare(FirstPartyServer(), ThirdPartyServer()),
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
        WriteInRepository(".vscode/mcp.json", content);
        byte[] before = File.ReadAllBytes(Path.Combine(Repo, ".vscode/mcp.json"));

        McpWiringRepairReport report = Repair(Declare(FirstPartyServer()), ResolverOver(Bin), VsCode);

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
        WriteInRepository(".vscode/mcp.json", """{"servers": {"kyber-weave": {}}}""");
        byte[] refusedBefore = File.ReadAllBytes(Path.Combine(Repo, ".vscode/mcp.json"));

        McpWiringRepairReport report = Repair(
            Declare(FirstPartyServer()),
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

        McpWiringRepairReport report = Repair(Declare(FirstPartyServer()), ResolverOver(Bin), harness);

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
        McpWiringRepairReport report = Repair(Declare(FirstPartyServer()), ResolverOver(Bin), Codex);

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
            Declare(FirstPartyServer()),
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

        McpWiringRepairReport report = Repair(Declare(FirstPartyServer()), ResolverOver(EmptyBin), harness);

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
        McpWiringRepairReport report = Repair(Declare(ThirdPartyServer()), ResolverOver(EmptyBin), ClaudeCode);

        McpWiringFileChange change = SingleChange(report, ClaudeCode);
        Assert.Equal(McpWiringChangeOutcome.Refused, change.Outcome);
        Assert.False(change.Written);
        Assert.Contains("codegraph", DiagnosticText(change), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(Repo, ".mcp.json")));
    }

    /// <summary>
    /// A dry run is the same computation with the write withheld: it reports exactly the outcomes
    /// the real run reports, writes not one byte to a target, and does not create the receipt —
    /// not even the receipt (5.10).
    /// </summary>
    [Fact]
    public void ADryRunWritesNothingAndReportsWhatTheRealRunWould()
    {
        WriteInRepository(".vscode/mcp.json", """{"servers": {"kyber-weave": {}}}""");
        byte[] refusedBefore = File.ReadAllBytes(Path.Combine(Repo, ".vscode/mcp.json"));

        McpConfig config = Declare(FirstPartyServer());
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

        McpWiringRepairReport report = Repair(Declare(FirstPartyServer()), ResolverOver(Bin), ClaudeCode);

        McpWiringFileChange change = SingleChange(report, ClaudeCode);
        Assert.Equal(McpWiringChangeOutcome.Refused, change.Outcome);
        Assert.False(change.Written);
        Assert.Contains(".mcp.json", DiagnosticText(change), StringComparison.Ordinal);
        Assert.False(File.Exists(ReceiptPath));
    }

    private McpWiringRepairReport Repair(
        McpConfig config,
        LaunchCommandResolver resolver,
        params string[] targets) =>
        McpWiringRepairer.Repair(Repo, config, resolver, targets, dryRun: false);

    private LaunchCommandResolver ResolverOver(params string[] searchPaths) => new(searchPaths);

    private static McpConfig Declare(params RequiredMcpServer[] servers)
    {
        Dictionary<string, RequiredMcpServer> declared = new(StringComparer.Ordinal);
        foreach (RequiredMcpServer server in servers)
            declared[server.Name] = server;

        return new McpConfig { RequiredServers = declared };
    }

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

    private void WriteInRepository(string relativePath, string content) =>
        WriteBytes(relativePath, Encoding.UTF8.GetBytes(content));

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

    private void WriteBytes(string relativePath, byte[] content)
    {
        string path = Path.Combine(Repo, relativePath);
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllBytes(path, content);
    }

    /// <summary>
    /// A file, not a process: the resolver is a file-existence check, so an empty file proves no
    /// launch happened. The execute bit is set where the platform has one so the fixture looks like
    /// an installed binary; on Windows the extension PATHEXT resolution looks for is used.
    /// </summary>
    private void WriteBinary(string name)
    {
        string path = Path.Combine(Bin, OperatingSystem.IsWindows() ? name + ".exe" : name);
        File.WriteAllText(path, string.Empty);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
