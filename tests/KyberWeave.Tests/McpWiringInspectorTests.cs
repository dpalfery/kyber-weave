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
        McpWiringFixture.WriteExecutable(Bin, BinaryName);

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
        McpWiringFixture.WriteExecutable(Bin, BinaryName);
        string second = Path.Combine(_root.Path, "bin-second");
        Directory.CreateDirectory(second);
        File.WriteAllText(Path.Combine(second, BinaryName), string.Empty);

        LaunchCommandResolver resolver = new([Bin, second]);

        string? resolved = resolver.Resolve(BinaryName);

        Assert.NotNull(resolved);
        Assert.StartsWith(Bin, resolved, StringComparison.Ordinal);
    }

    /// <summary>
    /// A launch command this platform cannot spell as a path is reported as a missing binary, not
    /// thrown out of the check.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A harness entry whose command is a URL — <c>https://localhost:8765/mcp</c> — is how a path
    /// shape the platform rejects arrives here in practice: it holds the separator a path is
    /// recognized by, and on Windows the colon outside the volume position makes
    /// <see cref="Path.GetFullPath(string, string)"/> answer <see cref="NotSupportedException"/>
    /// rather than a path. The resolver caught only the argument and length failures, so that
    /// answer escaped out of <see cref="McpWiringInspector.Inspect(string, McpConfig, LaunchCommandResolver)"/>
    /// instead of becoming the <c>KW-MCP-WIRE-003</c> gap an absent binary produces.
    /// </para>
    /// <para>
    /// The assertion holds on every platform, and only the arm that reaches it differs: on Unix a
    /// colon is an ordinary file-name character, so the command resolves to a path under the base
    /// directory that holds nothing and is refused for that reason. That is why this case needs no
    /// platform guard — a gap is the answer either way, and an exception is the answer neither
    /// platform may give.
    /// </para>
    /// <para>
    /// The resolver is asked directly as well as through the check, because an exception is thrown
    /// before any report exists: the resolver-level assertion names the call that threw even if the
    /// check's wording moves.
    /// </para>
    /// </remarks>
    [Fact]
    public void ALaunchCommandThePlatformCannotSpellAsAPathIsReportedAsAMissingBinary()
    {
        const string UrlCommand = "https://localhost:8765/mcp";

        McpWiringFixture.WriteText(Repo, ".mcp.json", McpServersDocument(".", UrlCommand));

        Assert.Null(new LaunchCommandResolver([EmptyBin]).Resolve(UrlCommand));

        // Third-party, because a server Kyber-Weave ships is one whose command runs its own binary:
        // this case is about the shape of a name, not about which side installed it.
        McpWiringReport report = Inspect(McpWiringFixture.Declare(
            new RequiredMcpServer(
                ServerName,
                [UrlCommand, "--repo-root", "."],
                McpProvenance.ThirdParty)));

        Assert.Contains(
            FindingsFor(report, ClaudeCode),
            finding => finding.Code == BinaryAbsentCode);
    }

    /// <summary>
    /// A declared launch command spelled as a repository-relative path resolves against the
    /// repository under inspection, so the tree that launches from the repository root is never
    /// reported as one whose binary is missing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>./tools/kyber-weave-mcp</c> is how a repository points at the binary it builds, and it is not
    /// something <c>PATH</c> answers: the resolver takes it as a path and resolves it against the
    /// root it was handed. Resolving it against the process's working directory instead made the
    /// same tree wired from the repository root and unwired from anywhere else — the check and the
    /// launch that followed it disagreed about one entry, and the operator was told to install a
    /// binary that was sitting in their checkout.
    /// </para>
    /// <para>
    /// The compiled <see cref="McpWiringInspector.Inspect(string, McpConfig)"/> overload is driven
    /// rather than an injected resolver, because the root it passes down is the whole behaviour
    /// under test. It reads nothing of the machine: a path-shaped command is resolved against the
    /// root and never searched for on <c>PATH</c>.
    /// </para>
    /// <para>
    /// The rootless contrast is asserted for the same command so the case has teeth: reverting the
    /// overload to <see cref="LaunchCommandResolver.FromEnvironment()"/> restores
    /// <c>KW-MCP-WIRE-003</c> here, and the resolver-level assertion names what the revert broke
    /// even if the inspector's wording moves.
    /// </para>
    /// </remarks>
    [Fact]
    public void ARepositoryRelativeLaunchCommandIsResolvedAgainstTheRepositoryRoot()
    {
        string relativeCommand = "./tools/kyber-weave-mcp";
        McpWiringFixture.WriteExecutable(Path.Combine(Repo, "tools"), BinaryName);
        McpWiringFixture.WriteText(Repo, ".mcp.json", McpServersDocument(".", relativeCommand));

        // The fixture is a temp directory, so the process cannot be standing in it. Without this
        // the contrast below would prove nothing on a machine that happened to be rooted there.
        Assert.NotEqual(Repo, Directory.GetCurrentDirectory());
        Assert.Null(LaunchCommandResolver.FromEnvironment().Resolve(relativeCommand));

        McpConfig declared = McpWiringFixture.Declare(RepositoryRelativeServer(relativeCommand));

        McpWiringReport rootless = McpWiringInspector.Inspect(
            Repo,
            declared,
            LaunchCommandResolver.FromEnvironment());
        Assert.Contains(
            FindingsFor(rootless, ClaudeCode),
            finding => finding.Code == BinaryAbsentCode);

        McpWiringReport report = McpWiringInspector.Inspect(Repo, declared);

        AssertNoFindingFor(report, ClaudeCode);
    }

    [Theory]
    [MemberData(nameof(WiredShapes))]
    public void AServerWiredInEachHarnessShapeReportsNoGap(string harness, string path, string document)
    {
        McpWiringFixture.WriteText(Repo, path, document);
        McpWiringFixture.WriteExecutable(Bin, BinaryName);

        McpWiringReport report = Inspect(McpWiringFixture.Declare(FirstPartyServer()), Bin);

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
        McpWiringFixture.WriteText(Repo, inPlayFile, inPlayContent);
        McpWiringFixture.WriteExecutable(Bin, BinaryName);

        McpWiringReport report = Inspect(McpWiringFixture.Declare(FirstPartyServer()), Bin);

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
        McpWiringFixture.WriteText(Repo, ".factory/.keep", string.Empty);
        McpWiringFixture.WriteExecutable(Bin, BinaryName);

        McpWiringFinding finding = SingleFindingFor(Inspect(McpWiringFixture.Declare(FirstPartyServer()), Bin), Factory);

        Assert.Contains("kyber-weave docs mcp-wire --target factory", finding.Hint, StringComparison.Ordinal);
    }

    /// <summary>
    /// A <c>recommended</c> server reports at <c>info</c> and keeps the same rule id, so the
    /// declared level is what decides severity rather than the gap kind (design DD7).
    /// </summary>
    [Fact]
    public void ARecommendedServerReportsAtInfoAndStillCarriesItsRuleId()
    {
        McpWiringFixture.WriteText(Repo, ".factory/.keep", string.Empty);
        McpWiringFixture.WriteExecutable(Bin, BinaryName);

        McpWiringReport report = Inspect(McpWiringFixture.Declare(FirstPartyServer(level: McpRequirementLevel.Recommended)), Bin);

        McpWiringFinding finding = SingleFindingFor(report, Factory);
        Assert.Equal(McpWiringKind.NotWired, finding.Kind);
        Assert.Equal(NotWiredCode, finding.Code);
        Assert.Equal(Severity.Info, finding.Severity);
    }

    /// <summary>
    /// A wired command that differs from the declaration in anything but the harness's own
    /// repository-root spelling is still a difference.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The check has to treat <c>${workspaceFolder}</c> (VS Code) and <c>.</c> as naming the same
    /// repository root, because the repair writes the harness's own token and 5.11 requires it to.
    /// That is the one tolerance, and this test is the boundary of it: a tolerance wide enough to
    /// swallow these three would report another checkout, another executable, or another harness's
    /// substitution as wired, and the agent would start pointed at the wrong place with a confirming
    /// <c>ok</c> line above it.
    /// </para>
    /// <para>
    /// The second row is the one that is easiest to get wrong. <c>${workspaceFolder}</c> in a Claude
    /// Code entry is not a repository-root spelling at all — that harness substitutes nothing — so the
    /// token cannot be normalised globally; it has to be the token <em>that harness</em> documents.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(CommandDifferencesThatSurviveTheRootToken))]
    public void AWiredCommandThatDiffersBeyondTheRootTokenIsStillADifference(
        string harness,
        string configFile,
        string document,
        string wiredCommand)
    {
        McpWiringFixture.WriteText(Repo, configFile, document);
        McpWiringFixture.WriteExecutable(Bin, BinaryName);
        McpWiringFixture.WriteExecutable(Bin, "other-mcp");

        McpWiringReport report = Inspect(McpWiringFixture.Declare(FirstPartyServer()), Bin);

        McpWiringFinding finding = SingleFindingFor(report, harness);
        Assert.Equal(McpWiringKind.CommandDiffers, finding.Kind);
        Assert.Equal(CommandDiffersCode, finding.Code);
        Assert.Contains($"{BinaryName} --repo-root .", finding.Message, StringComparison.Ordinal);
        Assert.Contains(wiredCommand, finding.Message, StringComparison.Ordinal);
    }

    public static TheoryData<string, string, string, string> CommandDifferencesThatSurviveTheRootToken() => new()
    {
        // harness, repository-scope file, the document, the launch command it actually names
        {
            VsCode,
            ".vscode/mcp.json",
            VsCodeServersDocument("/somewhere/else"),
            $"{BinaryName} --repo-root /somewhere/else"
        },
        {
            ClaudeCode,
            ".mcp.json",
            McpServersDocument("${workspaceFolder}"),
            $"{BinaryName} --repo-root ${{workspaceFolder}}"
        },
        {
            Factory,
            ".factory/mcp.json",
            McpServersDocument(".", command: "other-mcp"),
            "other-mcp --repo-root ."
        }
    };

    /// <summary>
    /// Both commands are named, so the operator can see what the declaration asks for and what the
    /// file actually says without opening either.
    /// </summary>
    [Fact]
    public void AWiredEntryWithADifferentLaunchCommandNamesBothCommands()
    {
        McpWiringFixture.WriteText(Repo, ".factory/mcp.json", McpServersDocument("/wired/repo"));
        McpWiringFixture.WriteExecutable(Bin, BinaryName);

        McpWiringReport report = Inspect(McpWiringFixture.Declare(FirstPartyServer("/declared/repo")), Bin);

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
        McpWiringFixture.WriteText(Repo, "opencode.json", OpenCodeDocument("/wired/repo"));
        McpWiringFixture.WriteExecutable(Bin, BinaryName);

        McpWiringReport report = Inspect(McpWiringFixture.Declare(FirstPartyServer("/declared/repo")), Bin);

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
        McpWiringFixture.WriteText(Repo, ".factory/mcp.json", McpServersDocument("."));

        McpWiringReport report = Inspect(McpWiringFixture.Declare(FirstPartyServer()), EmptyBin);

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
        McpWiringFixture.WriteText(Repo,
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

        McpWiringReport report = Inspect(McpWiringFixture.Declare(codegraph), EmptyBin);

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
        McpWiringReport report = Inspect(McpWiringFixture.Declare(FirstPartyServer()), EmptyBin);

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
        McpWiringReport report = Inspect(McpWiringFixture.Declare(FirstPartyServer()), EmptyBin);

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
        McpWiringReport report = Inspect(McpWiringFixture.Declare(FirstPartyServer()), EmptyBin);

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
    public void AnUndetectableHarnessReportsOneInfoLineHoweverManyServersAreDeclared()
    {
        RequiredMcpServer codegraph = new("codegraph", ["codegraph", "serve", "--mcp"], McpProvenance.ThirdParty);

        McpWiringReport report = Inspect(McpWiringFixture.Declare(FirstPartyServer(), codegraph), EmptyBin);

        Assert.Single(FindingsFor(report, Factory));
    }

    [Fact]
    public void AnEmptyOrAbsentDeclarationProducesNoFindings()
    {
        McpWiringFixture.WriteText(Repo, ".factory/.keep", string.Empty);
        McpWiringFixture.WriteText(Repo, ".vscode/.keep", string.Empty);
        McpWiringFixture.WriteText(Repo, "opencode.json", """{ "mcp": {} }""");

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
        McpWiringFixture.WriteText(Repo, ".factory/.keep", string.Empty);
        McpWiringFixture.WriteText(Repo, "opencode.json", """{ "mcp": {} }""");
        McpWiringFixture.WriteText(Repo, ".codex/config.toml", "[mcp_servers.other]\ncommand = \"other\"\n");
        McpWiringFixture.WriteExecutable(Bin, BinaryName);
        RequiredMcpServer codegraph = new(
            "codegraph",
            ["codegraph", "serve", "--mcp"],
            McpProvenance.ThirdParty,
            McpRequirementLevel.Recommended,
            "codegraph init .");
        McpConfig config = McpWiringFixture.Declare(FirstPartyServer(), codegraph);

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
        McpWiringReport report = Inspect(McpWiringFixture.Declare(FirstPartyServer()), EmptyBin);

        string[] expected = [Factory, ClaudeCode, Cursor, VsCode, CopilotCodingAgent, Codex, OpenCode, ZCode];
        string[] actual = [.. report.Findings.Select(finding => finding.Harness)];

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void FindingsForOneHarnessAreOrderedByServerName()
    {
        McpWiringFixture.WriteText(Repo, ".factory/.keep", string.Empty);
        McpWiringFixture.WriteExecutable(Bin, BinaryName);
        McpWiringFixture.WriteExecutable(Bin, "codegraph");
        RequiredMcpServer codegraph = new("codegraph", ["codegraph", "serve", "--mcp"], McpProvenance.ThirdParty);

        McpWiringReport report = Inspect(McpWiringFixture.Declare(FirstPartyServer(), codegraph), Bin);

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
        McpWiringFixture.WriteText(Repo, ".factory/.keep", string.Empty);
        McpWiringFixture.WriteText(Repo, ".cursor/mcp.json", McpServersDocument("."));
        McpWiringFixture.WriteExecutable(Bin, BinaryName);
        TreeSnapshot before = DirectoryTreeSnapshot.SnapshotTree(_root.Path);

        _ = Inspect(McpWiringFixture.Declare(FirstPartyServer()), Bin);

        DirectoryTreeSnapshot.AssertTreeUnchanged(_root.Path, before);
    }

    /// <summary>The <c>.zcode/</c> marker alone puts ZCode in play, as it does for Squad.</summary>
    [Fact]
    public void ZCodeInPlayByItsMarkerAloneReportsTheServerNotWired()
    {
        McpWiringFixture.WriteText(Repo, ".zcode/.keep", string.Empty);
        McpWiringFixture.WriteExecutable(Bin, BinaryName);

        McpWiringFinding finding = SingleFindingFor(Inspect(McpWiringFixture.Declare(FirstPartyServer()), Bin), ZCode);

        Assert.Equal(McpWiringKind.NotWired, finding.Kind);
        Assert.Equal(NotWiredCode, finding.Code);
        Assert.NotNull(finding.Path);
    }

    [Theory]
    [MemberData(nameof(ZCodeProjectTierFiles))]
    public void AZCodeServerInAProjectTierIsWired(string tierFile)
    {
        McpWiringFixture.WriteText(Repo, tierFile, ZCodeServersDocument("."));
        McpWiringFixture.WriteExecutable(Bin, BinaryName);

        McpWiringReport report = Inspect(McpWiringFixture.Declare(FirstPartyServer()), Bin);

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
        McpWiringFixture.WriteText(Repo, ".zcode/.keep", string.Empty);
        McpWiringFixture.WriteText(Repo, "zcode.json", """{ "mcp": { "servers": {} } }""");
        WriteOutsideRepository("zcode-storage/cli/config.json", McpServersDocument("."));
        McpWiringFixture.WriteExecutable(Bin, BinaryName);

        McpWiringFinding finding = SingleFindingFor(Inspect(McpWiringFixture.Declare(FirstPartyServer()), Bin), ZCode);

        Assert.Equal(McpWiringKind.NotWired, finding.Kind);
    }

    /// <summary>
    /// A configuration file that is a symbolic link is not read through, and the line says so.
    /// </summary>
    /// <remarks>
    /// A link in the tree makes a repository-scope path name a file outside the repository, and
    /// requirement 2.6 confines this check to the repository: no parent directory, no user-scope
    /// configuration. Reading through the link would report a teammate's own <c>~/.mcp.json</c> as
    /// this repository's wiring — a confirming pass for a configuration a fresh checkout does not
    /// have. The finding's kind is deliberately not pinned: what the operator needs is to be told the
    /// file is a link, and which one.
    /// </remarks>
    [Fact]
    public void ALinkedConfigurationFileIsNotRead()
    {
        string outside = Path.Combine(_root.Path, "user-scope", "mcp.json");
        McpWiringFixture.WriteText(_root.Path, "user-scope/mcp.json", McpServersDocument("."));
        McpWiringFixture.WriteExecutable(Bin, BinaryName);
        string link = Path.Combine(Repo, ".mcp.json");
        McpWiringFixture.CreateSymlink(link, outside);

        McpWiringReport report = Inspect(McpWiringFixture.Declare(FirstPartyServer()), Bin);

        McpWiringFinding finding = SingleFindingFor(report, ClaudeCode);
        string text = $"{finding.Message} {finding.Hint}";

        Assert.Contains(".mcp.json", text, StringComparison.Ordinal);
        Assert.True(
            text.Contains("symbolic link", StringComparison.OrdinalIgnoreCase)
                || text.Contains("symlink", StringComparison.OrdinalIgnoreCase),
            $"{ClaudeCode}: the report must say .mcp.json is a symbolic link, but said: {text}");
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
        McpWiringFixture.WriteText(Repo, "zcode.json", """{ "mcp": { "servers": {} } }""");
        WriteOutsideRepository(".zcode/config.json", McpServersDocument("."));
        McpWiringFixture.WriteExecutable(Bin, BinaryName);

        McpWiringFinding finding = SingleFindingFor(Inspect(McpWiringFixture.Declare(FirstPartyServer()), Bin), ZCode);

        Assert.Equal(McpWiringKind.NotWired, finding.Kind);
    }

    [Fact]
    public void ACodexHeaderOutsideAStringRegionIsWired()
    {
        McpWiringFixture.WriteText(Repo,
            ".codex/config.toml",
            string.Join(
                '\n',
                "[mcp_servers.kyber-weave]",
                "command = \"kyber-weave-mcp\"",
                "args = [\"--repo-root\", \".\"]"));
        McpWiringFixture.WriteExecutable(Bin, BinaryName);

        McpWiringReport report = Inspect(McpWiringFixture.Declare(FirstPartyServer()), Bin);

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
        McpWiringFixture.WriteText(Repo,
            ".codex/config.toml",
            string.Join(
                '\n',
                $"note = {TomlMultilineDelimiter}",
                "[mcp_servers.kyber-weave]",
                TomlMultilineDelimiter,
                "command = \"kyber-weave-mcp\""));
        McpWiringFixture.WriteExecutable(Bin, BinaryName);

        McpWiringFinding finding = SingleFindingFor(Inspect(McpWiringFixture.Declare(FirstPartyServer()), Bin), Codex);

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
        McpWiringFixture.WriteText(Repo,
            ".codex/config.toml",
            string.Join(
                '\n',
                "[mcp_servers.kyber-weave]",
                $"note = {TomlMultilineDelimiter}",
                "command = \"kyber-weave-mcp\""));
        McpWiringFixture.WriteExecutable(Bin, BinaryName);

        McpWiringFinding finding = SingleFindingFor(Inspect(McpWiringFixture.Declare(FirstPartyServer()), Bin), Codex);

        Assert.Equal(McpWiringKind.NotWired, finding.Kind);
    }

    /// <summary>
    /// A Codex entry that names another binary is a difference, not a pass.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The entry body is read — the TOML reader reads the table's <c>command</c> — so the comparison
    /// the specification asks for applies: name the declaration and the wired command and report
    /// <c>KW-MCP-WIRE-002</c>. Skipping it because the catalog marks the row's command shape
    /// undetermined reported a server wired to <c>ls</c> as <c>ok</c>: the executable that check
    /// looked at was the wired entry's own, and it resolved, so nothing was reported at all.
    /// </para>
    /// <para>
    /// Arguments are not compared here, because this reader does not read them. The wired executable
    /// is the whole of what the body established, and it is what the difference is about.
    /// </para>
    /// </remarks>
    [Fact]
    public void ACodexEntryNamingAnotherBinaryIsADifference()
    {
        McpWiringFixture.WriteText(Repo,
            ".codex/config.toml",
            string.Join('\n', "[mcp_servers.kyber-weave]", "command = \"ls\""));
        McpWiringFixture.WriteExecutable(Bin, "ls");

        McpWiringReport report = Inspect(McpWiringFixture.Declare(FirstPartyServer()), Bin);

        McpWiringFinding finding = SingleFindingFor(report, Codex);
        Assert.Equal(McpWiringKind.CommandDiffers, finding.Kind);
        Assert.Equal(CommandDiffersCode, finding.Code);
        Assert.Contains($"'{BinaryName} --repo-root .'", finding.Message, StringComparison.Ordinal);
        Assert.Contains("'ls'", finding.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A ZCode entry body is compared by name alone, however its command reads.
    /// </summary>
    /// <remarks>
    /// This is the other half of the rule above: the comparison happens when the body was read, and
    /// a body the catalog pins nowhere is never read. ZCode's entry body inside <c>mcp.servers</c> is
    /// recorded as pinned nowhere in this repository (the archived design's DG3, decided as DD18), so
    /// comparing an argv nobody verified against the declaration would report a difference the
    /// specification does not ask for.
    /// </remarks>
    [Fact]
    public void AZCodeEntryBodyIsComparedByNameOnly()
    {
        McpWiringFixture.WriteText(Repo, "zcode.json", ZCodeServersDocument("/somewhere/else"));
        McpWiringFixture.WriteExecutable(Bin, BinaryName);

        McpWiringReport report = Inspect(McpWiringFixture.Declare(FirstPartyServer()), Bin);

        AssertNoFindingFor(report, ZCode);
    }

    private McpWiringReport Inspect(McpConfig config, params string[] searchPaths) =>
        McpWiringInspector.Inspect(Repo, config, new LaunchCommandResolver(searchPaths));

    private static RequiredMcpServer FirstPartyServer(
        string repoRootArgument = ".",
        McpRequirementLevel level = McpRequirementLevel.Required) =>
        new(
            ServerName,
            [BinaryName, "--repo-root", repoRootArgument],
            McpProvenance.FirstParty,
            level);

    /// <summary>
    /// The declared server whose launch command is a repository-relative path rather than a name.
    /// Its argument list matches <see cref="McpServersDocument"/>, so a case built from both compares
    /// the wired entry against the declaration and finds no difference to report.
    /// </summary>
    private static RequiredMcpServer RepositoryRelativeServer(string command) =>
        new(ServerName, [command, "--repo-root", "."], McpProvenance.FirstParty);

    private static string McpServersDocument(string repoRootArgument, string command = BinaryName) => $$"""
        {
          "mcpServers": {
            "kyber-weave": {
              "command": "{{command}}",
              "args": ["--repo-root", "{{repoRootArgument}}"]
            }
          }
        }
        """;

    /// <summary>
    /// VS Code's own shape: the server map is keyed <c>servers</c>, not <c>mcpServers</c>, and the
    /// entry is typed. The wrong key makes the harness ignore the file silently, which is why the
    /// row exists separately rather than as a shared document.
    /// </summary>
    private static string VsCodeServersDocument(string repoRootArgument) => $$"""
        {
          "servers": {
            "kyber-weave": {
              "type": "stdio",
              "command": "{{BinaryName}}",
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
}
