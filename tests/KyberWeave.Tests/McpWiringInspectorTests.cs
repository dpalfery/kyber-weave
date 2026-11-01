using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Mcp;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The wiring check: for every harness in play, does this repository's declared MCP server reach
/// that harness's repository-scope configuration, and would it launch if it did?
/// </summary>
/// <remarks>
/// <para>
/// Each case below builds a real repository tree in a temp folder and drives the inspector over
/// it, because the thing under test is a claim about a tree: which files exist, which key path
/// each harness uses, and which marker says a harness is worth reporting on. Nothing here reads
/// the developer's own PATH or home directory — the launch binary is resolved through injected
/// search paths (design DD5) and every repository-scope file is one this fixture wrote.
/// </para>
/// <para>
/// Two properties are the reason the check exists and are asserted as behaviour rather than as
/// shape. The first is honesty about detection: a harness with neither a configuration file nor a
/// marker is reported at <c>info</c> with the marker that was looked for, never as a warning, so a
/// repository is not failed for a harness nobody uses. The second is that an uncertain read never
/// resolves toward "wired": a Codex document the scanner refuses, or a server whose entry names a
/// binary that does not resolve, is reported as a gap rather than as a pass.
/// </para>
/// </remarks>
public sealed class McpWiringInspectorTests : IDisposable
{
    private const string ServerName = "kyber-weave";

    private const string BinaryName = "kyber-weave-mcp";

    private const string NotWiredCode = "KW-MCP-WIRE-001";

    private const string CommandDiffersCode = "KW-MCP-WIRE-002";

    private const string BinaryAbsentCode = "KW-MCP-WIRE-003";

    private const string NotDetectableCode = "KW-MCP-WIRE-004";

    private const string Factory = "factory";

    private const string ClaudeCode = "claude-code";

    private const string Cursor = "cursor";

    private const string VsCode = "vscode";

    private const string CopilotCodingAgent = "copilot-coding-agent";

    private const string Codex = "codex";

    private const string OpenCode = "opencode";

    private const string ZCode = "zcode";

    private const string TomlMultilineDelimiter = "\"\"\"";

    private readonly TempDirectory _root = new();

    public McpWiringInspectorTests()
    {
        Directory.CreateDirectory(Repo);
        Directory.CreateDirectory(Bin);
        Directory.CreateDirectory(EmptyBin);
    }

    private string Repo => Path.Combine(_root.Path, "repo");

    /// <summary>Where a case puts the fake launch binaries it wants the resolver to find.</summary>
    private string Bin => Path.Combine(_root.Path, "bin");

    /// <summary>
    /// An empty search path, used whenever the case is about an absent binary. It is a real
    /// directory rather than a missing one so the resolver is asked to search and finds nothing,
    /// which is the situation R2.7 and R6 describe.
    /// </summary>
    private string EmptyBin => Path.Combine(_root.Path, "empty-bin");

    public void Dispose() => _root.Dispose();

    public static TheoryData<string, string, string> WiredShapes() => new()
    {
        // harness, repository-scope file, a document with the declared server wired
        { Factory, ".factory/mcp.json", McpServersDocument(".") },
        { ClaudeCode, ".mcp.json", McpServersDocument(".") },
        { Cursor, ".cursor/mcp.json", McpServersDocument(".") },
        {
            VsCode,
            ".vscode/mcp.json",
            """
            {
              "servers": {
                "kyber-weave": {
                  "type": "stdio",
                  "command": "kyber-weave-mcp",
                  "args": ["--repo-root", "."]
                }
              }
            }
            """
        },
        { OpenCode, "opencode.json", OpenCodeDocument(".") }
    };

    public static TheoryData<string, string, string, string, bool> UnwiredInPlayShapes() => new()
    {
        // harness, the file that puts it in play, its content, the configuration file to name, writable
        { Factory, ".factory/.keep", string.Empty, ".factory/mcp.json", true },
        { ClaudeCode, ".mcp.json", """{ "mcpServers": {} }""", ".mcp.json", true },
        { Cursor, ".cursor/.keep", string.Empty, ".cursor/mcp.json", true },
        { VsCode, ".vscode/.keep", string.Empty, ".vscode/mcp.json", true },
        { Codex, ".codex/.keep", string.Empty, ".codex/config.toml", false },
        { OpenCode, "opencode.json", """{ "mcp": {} }""", "opencode.json", true }
    };

    public static TheoryData<string, string> UndetectableMarkers() => new()
    {
        // harness, the marker the report must name
        { Factory, ".factory" },
        { ClaudeCode, ".mcp.json" },
        { Cursor, ".cursor" },
        { VsCode, ".vscode" },
        { Codex, ".codex" },
        { OpenCode, "opencode.json" },
        { ZCode, ".zcode" }
    };

    public static TheoryData<string> ZCodeProjectTierFiles() => new()
    {
        "zcode.json",
        ".zcode/config.json"
    };

    [Fact]
    public void TheResolverFindsABinaryOnAnInjectedSearchPath()
    {
        WriteBinary(BinaryName);

        LaunchCommandResolver resolver = new([Bin]);

        Assert.NotNull(resolver.Resolve(BinaryName));
    }

    [Fact]
    public void TheResolverReportsNoPathForACommandNoSearchPathHolds()
    {
        LaunchCommandResolver resolver = new([EmptyBin]);

        Assert.Null(resolver.Resolve(BinaryName));
    }

    /// <summary>
    /// PATH is ordered, and so is an injected search path: the first entry that holds the binary
    /// is the one a launch would use. A resolver that ignored the order would report a machine's
    /// wiring against a binary the harness never starts.
    /// </summary>
    [Fact]
    public void TheResolverSearchesSearchPathsInOrder()
    {
        WriteBinary(BinaryName);
        string second = Path.Combine(_root.Path, "bin-second");
        Directory.CreateDirectory(second);
        File.WriteAllText(Path.Combine(second, BinaryName), string.Empty);

        LaunchCommandResolver resolver = new([Bin, second]);

        string? resolved = resolver.Resolve(BinaryName);

        Assert.NotNull(resolved);
        Assert.StartsWith(Bin, resolved, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(WiredShapes))]
    public void AServerWiredInEachHarnessShapeReportsNoGap(string harness, string path, string document)
    {
        WriteInRepository(path, document);
        WriteBinary(BinaryName);

        McpWiringReport report = Inspect(Declare(FirstPartyServer()), Bin);

        AssertNoFindingFor(report, harness);
    }

    [Theory]
    [MemberData(nameof(UnwiredInPlayShapes))]
    public void AnInPlayHarnessWithoutTheServerReportsARequiredGap(
        string harness,
        string inPlayFile,
        string inPlayContent,
        string configurationFile,
        bool writable)
    {
        WriteInRepository(inPlayFile, inPlayContent);
        WriteBinary(BinaryName);

        McpWiringReport report = Inspect(Declare(FirstPartyServer()), Bin);

        McpWiringFinding finding = SingleFindingFor(report, harness);
        Assert.Equal(McpWiringKind.NotWired, finding.Kind);
        Assert.Equal(NotWiredCode, finding.Code);
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Equal(ServerName, finding.Server);
        Assert.Equal(
            writable ? McpWiringFixability.Fixable : McpWiringFixability.NotFixable,
            finding.Fixable);
        Assert.Equal(configurationFile, RelativePath(finding));
    }

    /// <summary>
    /// The remedy is the command itself, not a description of one — the same discipline the
    /// existing cleanup warnings follow, so the report can be read and acted on in one keystroke.
    /// </summary>
    [Fact]
    public void ANotWiredFindingCarriesTheCopyReadyRepairCommand()
    {
        WriteInRepository(".factory/.keep", string.Empty);
        WriteBinary(BinaryName);

        McpWiringFinding finding = SingleFindingFor(Inspect(Declare(FirstPartyServer()), Bin), Factory);

        Assert.Contains("kyber-weave docs mcp-wire --target factory", finding.Hint, StringComparison.Ordinal);
    }

    /// <summary>
    /// A <c>recommended</c> server reports at <c>info</c> and keeps the same rule id, so the
    /// declared level is what decides severity rather than the gap kind (design DD7).
    /// </summary>
    [Fact]
    public void ARecommendedServerReportsAtInfoAndStillCarriesItsRuleId()
    {
        WriteInRepository(".factory/.keep", string.Empty);
        WriteBinary(BinaryName);

        McpWiringReport report = Inspect(Declare(FirstPartyServer(level: McpRequirementLevel.Recommended)), Bin);

        McpWiringFinding finding = SingleFindingFor(report, Factory);
        Assert.Equal(McpWiringKind.NotWired, finding.Kind);
        Assert.Equal(NotWiredCode, finding.Code);
        Assert.Equal(Severity.Info, finding.Severity);
    }

    /// <summary>
    /// Both commands are named, so the operator can see what the declaration asks for and what the
    /// file actually says without opening either.
    /// </summary>
    [Fact]
    public void AWiredEntryWithADifferentLaunchCommandNamesBothCommands()
    {
        WriteInRepository(".factory/mcp.json", McpServersDocument("/wired/repo"));
        WriteBinary(BinaryName);

        McpWiringReport report = Inspect(Declare(FirstPartyServer("/declared/repo")), Bin);

        McpWiringFinding finding = SingleFindingFor(report, Factory);
        Assert.Equal(McpWiringKind.CommandDiffers, finding.Kind);
        Assert.Equal(CommandDiffersCode, finding.Code);
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Contains("/declared/repo", finding.Message, StringComparison.Ordinal);
        Assert.Contains("/wired/repo", finding.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// opencode holds the executable and its arguments in one array. Comparing only the server
    /// name would call this wired and leave the agent pointed at another checkout.
    /// </summary>
    [Fact]
    public void TheSingleArrayCommandFormIsComparedAgainstTheDeclaration()
    {
        WriteInRepository("opencode.json", OpenCodeDocument("/wired/repo"));
        WriteBinary(BinaryName);

        McpWiringReport report = Inspect(Declare(FirstPartyServer("/declared/repo")), Bin);

        McpWiringFinding finding = SingleFindingFor(report, OpenCode);
        Assert.Equal(McpWiringKind.CommandDiffers, finding.Kind);
        Assert.Equal(CommandDiffersCode, finding.Code);
        Assert.Contains("/declared/repo", finding.Message, StringComparison.Ordinal);
        Assert.Contains("/wired/repo", finding.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A first-party binary that does not resolve means this installation is broken, so the hint
    /// is the install repair and never a harness configuration edit (R6.3).
    /// </summary>
    [Fact]
    public void AWiredFirstPartyEntryWithAMissingBinaryPointsAtTheInstallRepair()
    {
        WriteInRepository(".factory/mcp.json", McpServersDocument("."));

        McpWiringReport report = Inspect(Declare(FirstPartyServer()), EmptyBin);

        McpWiringFinding finding = SingleFindingFor(report, Factory);
        Assert.Equal(McpWiringKind.BinaryAbsent, finding.Kind);
        Assert.Equal(BinaryAbsentCode, finding.Code);
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Equal(McpWiringFixability.NotFixable, finding.Fixable);
        Assert.Contains("kyber-weave update", finding.Hint, StringComparison.Ordinal);
    }

    /// <summary>
    /// A third-party binary is reported with the manual command the declaration carries and is
    /// never installed, so the check prints the operator's own install line verbatim (R6.1, R6.4).
    /// </summary>
    [Fact]
    public void AWiredThirdPartyEntryWithAMissingBinaryPrintsTheDeclaredInstallText()
    {
        WriteInRepository(
            ".factory/mcp.json",
            """
            {
              "mcpServers": {
                "codegraph": {
                  "command": "codegraph",
                  "args": ["serve", "--mcp"]
                }
              }
            }
            """);
        RequiredMcpServer codegraph = new(
            "codegraph",
            ["codegraph", "serve", "--mcp"],
            McpProvenance.ThirdParty,
            Install: "codegraph init .");

        McpWiringReport report = Inspect(Declare(codegraph), EmptyBin);

        McpWiringFinding finding = SingleFindingFor(report, Factory);
        Assert.Equal(McpWiringKind.BinaryAbsent, finding.Kind);
        Assert.Equal("codegraph", finding.Server);
        Assert.Equal(McpWiringFixability.NotFixable, finding.Fixable);
        Assert.Contains("codegraph init .", finding.Hint, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(UndetectableMarkers))]
    public void AnUndetectableHarnessReportsOneInfoLineNamingItsMarker(string harness, string marker)
    {
        McpWiringReport report = Inspect(Declare(FirstPartyServer()), EmptyBin);

        McpWiringFinding finding = SingleFindingFor(report, harness);
        Assert.Equal(McpWiringKind.NotDetectable, finding.Kind);
        Assert.Equal(NotDetectableCode, finding.Code);
        Assert.Equal(Severity.Info, finding.Severity);
        Assert.Null(finding.Path);
        Assert.Contains(marker, finding.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Claude Code has no marker besides <c>.mcp.json</c>, so a repository without one cannot
    /// tell us whether Claude Code is used. That is an unknown, not a failure, and the line says
    /// which file was looked for (R2.4).
    /// </summary>
    [Fact]
    public void ClaudeCodeWithoutAnMcpJsonReportsInfoAndNeverAWarning()
    {
        McpWiringReport report = Inspect(Declare(FirstPartyServer()), EmptyBin);

        IReadOnlyList<McpWiringFinding> findings = FindingsFor(report, ClaudeCode);
        Assert.All(findings, finding => Assert.Equal(Severity.Info, finding.Severity));
        Assert.All(findings, finding => Assert.Equal(McpWiringKind.NotDetectable, finding.Kind));
        McpWiringFinding finding = SingleFindingFor(report, ClaudeCode);
        Assert.Contains(".mcp.json", finding.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The Copilot coding agent is configured in repository settings rather than a file, so the
    /// check can never wire it: it reports the snippet and the setup-step requirement, and counts
    /// it as not fixable (R2.5).
    /// </summary>
    [Fact]
    public void TheCopilotCodingAgentReportsItsSnippetAndTheSetupStepItNeeds()
    {
        McpWiringReport report = Inspect(Declare(FirstPartyServer()), EmptyBin);

        McpWiringFinding finding = SingleFindingFor(report, CopilotCodingAgent);
        Assert.Equal(McpWiringKind.NotDetectable, finding.Kind);
        Assert.Equal(NotDetectableCode, finding.Code);
        Assert.Equal(Severity.Info, finding.Severity);
        Assert.Null(finding.Path);
        Assert.Equal(McpWiringFixability.NotFixable, finding.Fixable);
        Assert.Contains("mcpServers", finding.Hint, StringComparison.Ordinal);
        Assert.Contains("copilot-setup-steps", finding.Hint, StringComparison.Ordinal);
    }

    /// <summary>
    /// "Cannot detect" is one line per harness, not one per declared server: a repository that
    /// declares five servers must not print five copies of the same unknown.
    /// </summary>
    [Fact]
    public void AnUndetectableHarnessReportsOneInfoLineForEveryDeclaredServer()
    {
        RequiredMcpServer codegraph = new("codegraph", ["codegraph", "serve", "--mcp"], McpProvenance.ThirdParty);

        McpWiringReport report = Inspect(Declare(FirstPartyServer(), codegraph), EmptyBin);

        Assert.Single(FindingsFor(report, Factory));
    }

    [Fact]
    public void AnEmptyOrAbsentDeclarationProducesNoFindings()
    {
        WriteInRepository(".factory/.keep", string.Empty);
        WriteInRepository(".vscode/.keep", string.Empty);
        WriteInRepository("opencode.json", """{ "mcp": {} }""");

        AssertNoFindings(Inspect(McpConfig.ProductDefaults, EmptyBin));
        AssertNoFindings(Inspect(new McpConfig(), EmptyBin));
    }

    /// <summary>
    /// The check runs on every <c>docs init</c>, so the report a repository sees must be the
    /// report it saw last time — no dependence on enumeration order, clock, or locale.
    /// </summary>
    [Fact]
    public void TheSameTreeYieldsTheSameReportTwice()
    {
        WriteInRepository(".factory/.keep", string.Empty);
        WriteInRepository("opencode.json", """{ "mcp": {} }""");
        WriteInRepository(".codex/config.toml", "[mcp_servers.other]\ncommand = \"other\"\n");
        WriteBinary(BinaryName);
        RequiredMcpServer codegraph = new(
            "codegraph",
            ["codegraph", "serve", "--mcp"],
            McpProvenance.ThirdParty,
            McpRequirementLevel.Recommended,
            "codegraph init .");
        McpConfig config = Declare(FirstPartyServer(), codegraph);

        string[] first = DescribeAll(Inspect(config, Bin));
        string[] second = DescribeAll(Inspect(config, Bin));

        Assert.Equal(first, second);
    }

    /// <summary>
    /// Findings are ordered by harness in catalog order, then by server name, so the output reads
    /// the same way every time and a diff of two runs means something (R2.8).
    /// </summary>
    [Fact]
    public void FindingsAreOrderedByHarnessInCatalogOrder()
    {
        McpWiringReport report = Inspect(Declare(FirstPartyServer()), EmptyBin);

        string[] expected = [Factory, ClaudeCode, Cursor, VsCode, CopilotCodingAgent, Codex, OpenCode, ZCode];
        string[] actual = [.. report.Findings.Select(finding => finding.Harness)];

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void FindingsForOneHarnessAreOrderedByServerName()
    {
        WriteInRepository(".factory/.keep", string.Empty);
        WriteBinary(BinaryName);
        WriteBinary("codegraph");
        RequiredMcpServer codegraph = new("codegraph", ["codegraph", "serve", "--mcp"], McpProvenance.ThirdParty);

        McpWiringReport report = Inspect(Declare(FirstPartyServer(), codegraph), Bin);

        string[] expected = ["codegraph", ServerName];
        string[] actual = [.. FindingsFor(report, Factory).Select(finding => finding.Server)];
        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// The check is a read: it runs inside <c>docs init</c>, which writes only what scaffolding
    /// asks for. A repaired file appearing from the check itself would be an unwritten consent.
    /// </summary>
    [Fact]
    public void TheCheckWritesNothingToTheTree()
    {
        WriteInRepository(".factory/.keep", string.Empty);
        WriteInRepository(".cursor/mcp.json", McpServersDocument("."));
        WriteBinary(BinaryName);
        TreeSnapshot before = DirectoryTreeSnapshot.SnapshotTree(_root.Path);

        _ = Inspect(Declare(FirstPartyServer()), Bin);

        DirectoryTreeSnapshot.AssertTreeUnchanged(_root.Path, before);
    }

    /// <summary>The <c>.zcode/</c> marker alone puts ZCode in play, as it does for Squad.</summary>
    [Fact]
    public void ZCodeInPlayByItsMarkerAloneReportsTheServerNotWired()
    {
        WriteInRepository(".zcode/.keep", string.Empty);
        WriteBinary(BinaryName);

        McpWiringFinding finding = SingleFindingFor(Inspect(Declare(FirstPartyServer()), Bin), ZCode);

        Assert.Equal(McpWiringKind.NotWired, finding.Kind);
        Assert.Equal(NotWiredCode, finding.Code);
        Assert.NotNull(finding.Path);
    }

    [Theory]
    [MemberData(nameof(ZCodeProjectTierFiles))]
    public void AZCodeServerInAProjectTierIsWired(string tierFile)
    {
        WriteInRepository(tierFile, ZCodeServersDocument("."));
        WriteBinary(BinaryName);

        McpWiringReport report = Inspect(Declare(FirstPartyServer()), Bin);

        AssertNoFindingFor(report, ZCode);
    }

    /// <summary>
    /// ZCode's user tier lives outside the repository and is never read (R2.6). A server wired
    /// only there is not wired for the repository, which is the right report for a committed
    /// configuration — a teammate's checkout would not have it.
    /// </summary>
    [Fact]
    public void AZCodeUserTierConfigurationIsNeverRead()
    {
        WriteInRepository(".zcode/.keep", string.Empty);
        WriteInRepository("zcode.json", """{ "mcp": { "servers": {} } }""");
        WriteOutsideRepository("zcode-storage/cli/config.json", McpServersDocument("."));
        WriteBinary(BinaryName);

        McpWiringFinding finding = SingleFindingFor(Inspect(Declare(FirstPartyServer()), Bin), ZCode);

        Assert.Equal(McpWiringKind.NotWired, finding.Kind);
    }

    /// <summary>
    /// ZCode's project tiers are walked from the working directory up to the git worktree root,
    /// and no further. A <c>.zcode/config.json</c> in a parent directory belongs to another
    /// repository, and reporting it as wired would confirm a configuration this checkout cannot
    /// load.
    /// </summary>
    [Fact]
    public void AZCodeConfigurationAboveTheWorktreeRootIsNeverRead()
    {
        Directory.CreateDirectory(Path.Combine(Repo, ".git"));
        WriteInRepository("zcode.json", """{ "mcp": { "servers": {} } }""");
        WriteOutsideRepository(".zcode/config.json", McpServersDocument("."));
        WriteBinary(BinaryName);

        McpWiringFinding finding = SingleFindingFor(Inspect(Declare(FirstPartyServer()), Bin), ZCode);

        Assert.Equal(McpWiringKind.NotWired, finding.Kind);
    }

    [Fact]
    public void ACodexHeaderOutsideAStringRegionIsWired()
    {
        WriteInRepository(
            ".codex/config.toml",
            string.Join(
                '\n',
                "[mcp_servers.kyber-weave]",
                "command = \"kyber-weave-mcp\"",
                "args = [\"--repo-root\", \".\"]"));
        WriteBinary(BinaryName);

        McpWiringReport report = Inspect(Declare(FirstPartyServer()), Bin);

        AssertNoFindingFor(report, Codex);
    }

    /// <summary>
    /// A header quoted inside a <c>"""</c> note is prose, so Codex is not wired. Codex is
    /// snippet-only in v1, and the finding says so rather than offering a repair that cannot
    /// write that harness (design DD19).
    /// </summary>
    [Fact]
    public void ACodexHeaderInsideAMultilineStringIsNotWired()
    {
        WriteInRepository(
            ".codex/config.toml",
            string.Join(
                '\n',
                $"note = {TomlMultilineDelimiter}",
                "[mcp_servers.kyber-weave]",
                TomlMultilineDelimiter,
                "command = \"kyber-weave-mcp\""));
        WriteBinary(BinaryName);

        McpWiringFinding finding = SingleFindingFor(Inspect(Declare(FirstPartyServer()), Bin), Codex);

        Assert.Equal(McpWiringKind.NotWired, finding.Kind);
        Assert.Equal(McpWiringFixability.NotFixable, finding.Fixable);
    }

    /// <summary>
    /// A document the scanner cannot classify is never reported as wired. Nothing may read as a
    /// pass when the file was not understood (design DD10).
    /// </summary>
    [Fact]
    public void ACodexDocumentThatCannotBeClassifiedIsNotReportedAsWired()
    {
        WriteInRepository(
            ".codex/config.toml",
            string.Join(
                '\n',
                "[mcp_servers.kyber-weave]",
                $"note = {TomlMultilineDelimiter}",
                "command = \"kyber-weave-mcp\""));
        WriteBinary(BinaryName);

        McpWiringFinding finding = SingleFindingFor(Inspect(Declare(FirstPartyServer()), Bin), Codex);

        Assert.Equal(McpWiringKind.NotWired, finding.Kind);
    }

    private McpWiringReport Inspect(McpConfig config, params string[] searchPaths) =>
        McpWiringInspector.Inspect(Repo, config, new LaunchCommandResolver(searchPaths));

    private static McpConfig Declare(params RequiredMcpServer[] servers)
    {
        Dictionary<string, RequiredMcpServer> declared = new(StringComparer.Ordinal);
        foreach (RequiredMcpServer server in servers)
            declared[server.Name] = server;

        return new McpConfig { RequiredServers = declared };
    }

    private static RequiredMcpServer FirstPartyServer(
        string repoRootArgument = ".",
        McpRequirementLevel level = McpRequirementLevel.Required) =>
        new(
            ServerName,
            [BinaryName, "--repo-root", repoRootArgument],
            McpProvenance.FirstParty,
            level);

    private static string McpServersDocument(string repoRootArgument) => $$"""
        {
          "mcpServers": {
            "kyber-weave": {
              "command": "kyber-weave-mcp",
              "args": ["--repo-root", "{{repoRootArgument}}"]
            }
          }
        }
        """;

    private static string OpenCodeDocument(string repoRootArgument) => $$"""
        {
          "mcp": {
            "kyber-weave": {
              "type": "local",
              "command": ["kyber-weave-mcp", "--repo-root", "{{repoRootArgument}}"],
              "enabled": true
            }
          }
        }
        """;

    /// <summary>
    /// ZCode's own shape: servers live under <c>mcp.servers</c>, not under the
    /// <c>mcpServers</c> key every other JSON harness uses (design §2, cited from
    /// <c>ZCodeMcpConfiguration</c>, verified against zai-org/ZCode 3.14.0 on 2026-09-21).
    /// </summary>
    private static string ZCodeServersDocument(string repoRootArgument) => $$"""
        {
          "mcp": {
            "servers": {
              "kyber-weave": {
                "command": "kyber-weave-mcp",
                "args": ["--repo-root", "{{repoRootArgument}}"]
              }
            }
          }
        }
        """;

    private static IReadOnlyList<McpWiringFinding> FindingsFor(McpWiringReport report, string harness) =>
        [.. report.Findings.Where(finding => finding.Harness == harness)];

    private static McpWiringFinding SingleFindingFor(McpWiringReport report, string harness) =>
        Assert.Single(report.Findings, finding => finding.Harness == harness);

    private static void AssertNoFindingFor(McpWiringReport report, string harness)
    {
        string[] found = [.. FindingsFor(report, harness).Select(Describe)];
        Assert.True(
            found.Length == 0,
            $"expected no finding for {harness}, but reported: {string.Join("; ", found)}");
    }

    private static void AssertNoFindings(McpWiringReport report)
    {
        Assert.True(
            report.Findings.Count == 0,
            $"expected no findings at all, but reported: {string.Join("; ", report.Findings.Select(Describe))}");
    }

    private static string Describe(McpWiringFinding finding) =>
        $"{finding.Code} {finding.Harness}/{finding.Server} {finding.Kind} ({finding.Severity}): {finding.Message}";

    private static string[] DescribeAll(McpWiringReport report) =>
        [
            .. report.Findings.Select(finding =>
                $"{finding.Code}|{finding.Harness}|{finding.Server}|{finding.Kind}|{finding.Severity}|{finding.Path}|{finding.Fixable}")
        ];

    private static string? RelativePath(McpWiringFinding finding) =>
        finding.Path?.Replace('\\', '/');

    private void WriteInRepository(string relativePath, string content)
    {
        string path = Path.Combine(Repo, relativePath);
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(path, content);
    }

    /// <summary>
    /// Writes beside the repository root — the place a user-scope file would sit if the check
    /// ever walked out of the tree it was given.
    /// </summary>
    private void WriteOutsideRepository(string relativePath, string content)
    {
        string path = Path.Combine(_root.Path, relativePath);
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(path, content);
    }

    /// <summary>
    /// A file, not a process: the resolver is a file-existence check, and an empty file proves
    /// nothing was launched. The execute bit is set where the platform has one so the fixture
    /// looks like an installed binary rather than a stray text file; on Windows the binary is
    /// written with the extension PATHEXT resolution looks for.
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
