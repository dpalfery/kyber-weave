using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text;
using System.Text.Json;
using KyberWeave.Cli.Commands.Docs;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Mcp;
using KyberWeave.Core.Processes;
using Spectre.Console.Cli;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The MCP wiring check <c>docs init</c> runs after the skill deploy and before its <c>Next:</c>
/// block: what it reports, the opt-out, and the terminal-gated repair prompt.
/// </summary>
/// <remarks>
/// <para>
/// Every case scaffolds a real repository in a temp folder and drives <c>docs init</c> end to end,
/// because what is under test is a sequence — scaffold, deploy, check, offer the repair — and the
/// failures worth catching are orderings (a question asked after a failed scaffold, an opt-out that
/// skips less than it claims) rather than one return value.
/// </para>
/// <para>
/// Two process-global resources are involved, and this class owns both. The prompt is two static
/// seams, which must be reset on dispose or every later test in the process inherits the last
/// case's answer. The launch binary is resolved against this process's <c>PATH</c>, so a case that
/// wants a server to look launchable prepends a fake binary directory and restores the previous
/// value afterwards. The collection therefore disables parallelization: a test rewriting
/// <c>PATH</c> while another resolves against it would make both reports describe a machine
/// neither of them arranged.
/// </para>
/// </remarks>
[Collection(DocsInitMcpCheckEnvironmentCollection.Name)]
public sealed class DocsInitMcpCheckTests : IDisposable
{
    private const string KyberWeaveServer = "kyber-weave";

    private const string McpBinary = "kyber-weave-mcp";

    private const string Cursor = "cursor";

    private const string Factory = "factory";

    private const string CursorConfigRelativePath = ".cursor/mcp.json";

    private const string CursorRemedyCommand = "kyber-weave docs mcp-wire --target cursor";

    /// <summary>
    /// A third-party launch command no machine has: the name is this fixture's own, so "the binary
    /// is absent" is a property of the tree rather than of whatever the developer happens to have
    /// installed.
    /// </summary>
    private const string AbsentThirdPartyBinary = "kw-t6-absent-third-party-server";

    /// <summary>
    /// A declared server name long enough that the finding naming it, its harness and its rule id
    /// cannot fit a redirected console's 80 columns, so a folded render splits the name itself.
    /// </summary>
    private const string LongServerName =
        "kyber-weave-enterprise-documentation-index-server-for-the-monorepo-pipeline-alpha-beta";

    private readonly TempDirectory _temp = new();

    private string? _pathBeforeFakeBinaries;

    /// <summary>
    /// Where a case puts the fake launch binaries the check resolves against <c>PATH</c>.
    /// </summary>
    private string Bin => Path.Combine(_temp.Path, "bin");

    public void Dispose()
    {
        // A seam left set is inherited by every test the process runs afterwards, and the damage it
        // does (a prompt answered, a file written) looks nothing like the test that caused it.
        DocsInitCommand.InteractiveCheckOverride = null;
        DocsInitCommand.ConfirmFixOverride = null;

        if (_pathBeforeFakeBinaries is not null)
        {
            Environment.SetEnvironmentVariable("PATH", _pathBeforeFakeBinaries);
            _pathBeforeFakeBinaries = null;
        }

        _temp.Dispose();
    }

    /// <summary>
    /// A finding is actionable only if the operator can see which rule raised it and copy the
    /// command that closes it. Both travel on the finding's own line, unfolded, in the same
    /// discipline as the retired-skill cleanup command.
    /// </summary>
    [Fact]
    public void FindingsPrintTheirRuleIdentifiersAndTheCopyReadyRemedyCommand()
    {
        string repository = NewRepository();
        PutLaunchBinaryOnPath(McpBinary);
        Directory.CreateDirectory(Path.Combine(repository, ".cursor"));

        CapturedConsoleExecution<int> execution = RunDocsInit(repository);

        AssertSucceeded(execution);
        string finding = TheLineContaining(execution.Output, McpWiringInspector.NotWiredCode);
        Assert.Contains(Cursor, finding, StringComparison.Ordinal);
        Assert.Contains(CursorConfigRelativePath, finding, StringComparison.Ordinal);
        Assert.Contains(KyberWeaveServer, finding, StringComparison.Ordinal);
        Assert.Contains(CursorRemedyCommand, execution.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// R3.4: a wiring finding is a warning, not a failure. The corpus was scaffolded, and a
    /// harness the operator has not wired yet is the normal state of a fresh adoption — failing
    /// here would block the one thing the command exists to do.
    /// </summary>
    [Fact]
    public void FindingsDoNotMoveTheExitCode()
    {
        string repository = NewRepository();
        PutLaunchBinaryOnPath(McpBinary);
        Directory.CreateDirectory(Path.Combine(repository, ".cursor"));
        Directory.CreateDirectory(Path.Combine(repository, ".factory"));

        // A required first-party server and a recommended third party one, so the run has both a
        // warning and an information-level gap to report.
        DeclareServers(
            repository,
            $"""
            {KyberWeaveServer}:
              command: [kyber-weave-mcp, --repo-root, .]
              provenance: first-party
              level: required
            {AbsentThirdPartyBinary}:
              command: [{AbsentThirdPartyBinary}, serve, --mcp]
              provenance: third-party
              level: recommended
            """);

        CapturedConsoleExecution<int> execution = RunDocsInit(repository);

        AssertSucceeded(execution);
        Assert.Contains(McpWiringInspector.NotWiredCode, execution.Output, StringComparison.Ordinal);
        Assert.Contains(McpWiringInspector.BinaryAbsentCode, execution.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The opt-out is a bound command option that defaults to checking, following the shape of
    /// <c>--no-skill</c> and <c>--no-review-gates</c>.
    /// </summary>
    [Fact]
    public void TheOptOutIsABoundFlagThatDefaultsToChecking()
    {
        PropertyInfo? property = typeof(DocsInitSettings).GetProperty(nameof(DocsInitSettings.NoMcpCheck));
        Assert.NotNull(property);
        Assert.Equal(typeof(bool), property.PropertyType);
        Assert.False(new DocsInitSettings().NoMcpCheck);

        CommandOptionAttribute? option = property
            .GetCustomAttributes(typeof(CommandOptionAttribute), inherit: true)
            .Cast<CommandOptionAttribute>()
            .FirstOrDefault();
        Assert.NotNull(option);
        Assert.Contains("no-mcp-check", option.LongNames);
    }

    /// <summary>
    /// R4.6: the opt-out skips the check, the question and any repair, and says so — an operator
    /// who asked for none of it must not find a harness file written afterwards.
    /// </summary>
    [Fact]
    public void TheOptOutSkipsTheCheckThePromptAndAnyRepairAndSaysSo()
    {
        bool offered = false;

        // The seams are armed for the tempting answer: a run that reaches either one has skipped
        // less than the flag promises.
        DocsInitCommand.InteractiveCheckOverride = static () => true;
        DocsInitCommand.ConfirmFixOverride = _ =>
        {
            offered = true;
            return true;
        };

        string repository = NewRepository();
        PutLaunchBinaryOnPath(McpBinary);
        Directory.CreateDirectory(Path.Combine(repository, ".cursor"));

        CapturedConsoleExecution<int> execution = RunDocsInit(repository, "--no-mcp-check");

        AssertSucceeded(execution);
        Assert.Contains(
            OutputLines(execution.Output),
            line => line.Contains("--no-mcp-check", StringComparison.Ordinal));
        Assert.DoesNotContain("KW-MCP-WIRE-", execution.Output, StringComparison.Ordinal);
        Assert.False(offered, "The opt-out must skip the check, so the repair must never be offered.");
        Assert.False(
            File.Exists(CursorConfigPath(repository)),
            $"{CursorConfigRelativePath} was written despite --no-mcp-check.");
        Assert.False(
            File.Exists(McpWiringReceipt.PathFor(repository)),
            "A receipt was written despite --no-mcp-check.");
    }

    /// <summary>
    /// R4.4: CI redirects stdin, so the question is never asked, nothing is written, and the printed
    /// manual command is what the operator is left with. The confirm seam is armed and must not be
    /// reached: a run that asked anyway would block on input it cannot receive.
    /// </summary>
    [Fact]
    public void ARedirectedTerminalNeitherPromptsNorWritesAndLeavesTheManualCommand()
    {
        bool confirmed = false;
        DocsInitCommand.InteractiveCheckOverride = static () => false;
        DocsInitCommand.ConfirmFixOverride = _ =>
        {
            confirmed = true;
            return true;
        };

        string repository = NewRepository();
        PutLaunchBinaryOnPath(McpBinary);
        Directory.CreateDirectory(Path.Combine(repository, ".cursor"));

        CapturedConsoleExecution<int> execution = RunDocsInit(repository);

        AssertSucceeded(execution);
        Assert.Contains(McpWiringInspector.NotWiredCode, execution.Output, StringComparison.Ordinal);
        Assert.Contains(CursorRemedyCommand, execution.Output, StringComparison.Ordinal);
        Assert.False(confirmed, "A non-interactive run must not ask the repair question.");
        Assert.False(
            File.Exists(CursorConfigPath(repository)),
            $"{CursorConfigRelativePath} was written by a non-interactive run.");
        Assert.False(
            File.Exists(McpWiringReceipt.PathFor(repository)),
            "A receipt was written by a non-interactive run.");
    }

    /// <summary>
    /// R4.1 and R4.2: the question names the files and servers it would write, and confirming runs
    /// the same bounded write path as the dedicated repair command — the file appears and the
    /// receipt records what was written.
    /// </summary>
    [Fact]
    public void ConfirmingThePromptWritesTheHarnessFileAndRecordsTheReceipt()
    {
        IReadOnlyList<McpWiringFix> offered = [];
        DocsInitCommand.InteractiveCheckOverride = static () => true;
        DocsInitCommand.ConfirmFixOverride = fixes =>
        {
            offered = fixes;
            return true;
        };

        string repository = NewRepository();
        PutLaunchBinaryOnPath(McpBinary);
        Directory.CreateDirectory(Path.Combine(repository, ".cursor"));

        CapturedConsoleExecution<int> execution = RunDocsInit(repository);

        AssertSucceeded(execution);
        Assert.NotEmpty(offered);

        McpWiringFix fix = Assert.Single(offered);
        Assert.Equal(Cursor, fix.Harness);
        Assert.Equal(CursorConfigRelativePath, fix.Path);
        Assert.Contains(KyberWeaveServer, fix.Servers);
        Assert.True(
            fix.Create,
            "The fixture has no cursor configuration file, so the repair the question offers creates one.");

        string written = CursorConfigPath(repository);
        Assert.True(
            File.Exists(written),
            $"The confirmed repair wrote no {CursorConfigRelativePath}. Output:{Environment.NewLine}{execution.Output}");
        Assert.Contains(KyberWeaveServer, File.ReadAllText(written), StringComparison.Ordinal);

        Assert.True(
            File.Exists(McpWiringReceipt.PathFor(repository)),
            $"The confirmed repair recorded no receipt at {McpWiringReceipt.RelativePath}.");

        // R4.2: the run reports what it wrote, per file, and where the receipt is.
        Assert.Contains(CursorConfigRelativePath, execution.Output, StringComparison.Ordinal);
        Assert.Contains("mcp-wiring.json", execution.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// R4.3: declining writes nothing. The findings and the manual command stay in the output,
    /// because declining the repair is not declining the diagnosis.
    /// </summary>
    [Fact]
    public void DecliningThePromptWritesNothingAndKeepsTheFindings()
    {
        DocsInitCommand.InteractiveCheckOverride = static () => true;
        DocsInitCommand.ConfirmFixOverride = static _ => false;

        string repository = NewRepository();
        PutLaunchBinaryOnPath(McpBinary);
        Directory.CreateDirectory(Path.Combine(repository, ".cursor"));

        CapturedConsoleExecution<int> execution = RunDocsInit(repository);

        AssertSucceeded(execution);
        Assert.Contains(McpWiringInspector.NotWiredCode, execution.Output, StringComparison.Ordinal);
        Assert.Contains(CursorRemedyCommand, execution.Output, StringComparison.Ordinal);
        Assert.False(
            File.Exists(CursorConfigPath(repository)),
            $"{CursorConfigRelativePath} was written after the repair was declined.");
        Assert.False(
            File.Exists(McpWiringReceipt.PathFor(repository)),
            "A receipt was written after the repair was declined.");
    }

    /// <summary>
    /// R4.1: the question <em>is</em> the consent, so the scope it names is the message rather than
    /// detail beneath one. A repository using several harnesses produces a scope wider than the 512
    /// characters a single rendered line is capped at, and a consent cut short asks the operator to
    /// approve a write they cannot see — the one thing a consent prompt may not do.
    /// </summary>
    /// <remarks>
    /// The scope is printed a file to a line, so asserting per line is what proves every file is
    /// still there with its own servers after it. A case that only joined the output and searched it
    /// would pass on a prompt that had dropped a file, dropped a server, or attributed one harness's
    /// servers to another.
    /// </remarks>
    [Fact]
    public void TheConsentScopeNamesEveryFileAndServerEvenWhenItExceedsOneLine()
    {
        // Five declared servers on six harnesses is an ordinary adoption, and it is already wider
        // than one capped line — which is the whole point: the cap is not a hypothetical hazard.
        string[] servers = [KyberWeaveServer, "codegraph", "documentation-index", "playwright", "github"];

        IReadOnlyList<McpWiringFix> fixes =
        [
            new("claude-code", ".mcp.json", servers, Create: false),
            new(Cursor, CursorConfigRelativePath, servers, Create: true),
            new("vscode", ".vscode/mcp.json", servers, Create: true),
            new("opencode", "opencode.json", servers, Create: true),
            new(Factory, ".factory/mcp.json", servers, Create: false),
            new("codex", ".codex/config.toml", servers, Create: true)
        ];

        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(() =>
        {
            DocsInitCommand.PrintRepairScope(fixes);
            return 0;
        });

        // The fixture has to be wider than the cap this change removes, or it proves nothing about a
        // scope the operator could not see.
        Assert.True(
            execution.Output.Length > 512,
            $"The fixture renders {execution.Output.Length} characters, which the 512-character cap would not have cut.");

        string[] lines = execution.Output.Split(
            Environment.NewLine,
            StringSplitOptions.RemoveEmptyEntries);

        foreach (McpWiringFix fix in fixes)
        {
            string owner = Assert.Single(
                lines,
                line => line.Contains(fix.Path, StringComparison.Ordinal));

            Assert.Contains(fix.Harness, owner, StringComparison.Ordinal);

            foreach (string server in fix.Servers)
            {
                Assert.Contains(server, owner, StringComparison.Ordinal);
            }
        }

        Assert.DoesNotContain("…", execution.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// R4.7: the question sits behind a successful scaffold. A failed scaffold exits 1 with the
    /// reason and never reaches an interactive prompt — the operator is being told the command did
    /// not do its job, and a question about wiring would bury that.
    /// </summary>
    [Fact]
    public void AFailedScaffoldNeverReachesThePrompt()
    {
        bool asked = false;
        DocsInitCommand.InteractiveCheckOverride = static () => true;
        DocsInitCommand.ConfirmFixOverride = _ =>
        {
            asked = true;
            return true;
        };

        string repository = NewRepository();
        PutLaunchBinaryOnPath(McpBinary);
        Directory.CreateDirectory(Path.Combine(repository, ".cursor"));

        // A file where the scaffolder must create its documentation root fails the scaffold on its
        // first write.
        File.WriteAllText(Path.Combine(repository, "docs"), "not a directory");

        CapturedConsoleExecution<int> execution = RunDocsInit(repository);

        Assert.NotEqual(0, execution.Result);
        Assert.Contains("Could not scaffold", execution.Output, StringComparison.Ordinal);
        Assert.False(asked, "A failed scaffold must not reach the repair question.");
        Assert.DoesNotContain("KW-MCP-WIRE-", execution.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// R3.5: when every declared server is wired and launchable for every harness in play, the
    /// section confirms it instead of warning. A report that can only ever complain is one an
    /// operator stops reading.
    /// </summary>
    [Fact]
    public void AnAllWiredTreeIsConfirmedRatherThanWarnedAbout()
    {
        string repository = NewRepository();
        PutLaunchBinaryOnPath(McpBinary);
        Directory.CreateDirectory(Path.Combine(repository, ".factory"));
        WriteFile(
            repository,
            ".factory/mcp.json",
            """
            {
              "mcpServers": {
                "kyber-weave": {
                  "command": "kyber-weave-mcp",
                  "args": ["--repo-root", "."]
                }
              }
            }
            """);

        CapturedConsoleExecution<int> execution = RunDocsInit(repository);

        AssertSucceeded(execution);
        Assert.DoesNotContain(McpWiringInspector.NotWiredCode, execution.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(McpWiringInspector.CommandDiffersCode, execution.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(McpWiringInspector.BinaryAbsentCode, execution.Output, StringComparison.Ordinal);
        Assert.Contains(
            OutputLines(execution.Output),
            line => line.Contains("ok", StringComparison.Ordinal)
                && line.Contains(Factory, StringComparison.Ordinal)
                && line.Contains(KyberWeaveServer, StringComparison.Ordinal));
    }

    /// <summary>
    /// The section is written through the same unfolded-write idiom as the retired-skill cleanup
    /// command. A redirected console folds at 80 columns, so a folded finding would split the rule
    /// id, the server name and the path across lines — unreadable, and un-copyable when the line
    /// carries a command. The declaration here is long on purpose: the line naming it cannot fit
    /// 80 columns, so a folded render is unmistakable.
    /// </summary>
    [Fact]
    public void TheSectionIsPrintedUnfoldedOnARedirectedConsole()
    {
        string repository = NewRepository();
        PutLaunchBinaryOnPath(McpBinary);
        Directory.CreateDirectory(Path.Combine(repository, ".cursor"));
        DeclareServers(
            repository,
            $"""
            {LongServerName}:
              command: [kyber-weave-mcp, --repo-root, .]
              provenance: first-party
              level: required
            """);

        CapturedConsoleExecution<int> execution = RunDocsInit(repository);

        AssertSucceeded(execution);
        string finding = TheLineContaining(execution.Output, McpWiringInspector.NotWiredCode);
        Assert.Contains(LongServerName, finding, StringComparison.Ordinal);
        Assert.True(
            finding.Length > 80,
            $"The finding line rendered {finding.Length} columns wide, so it was folded: {finding}");
    }

    /// <summary>
    /// R3.6: a declaration the loader rejects surfaces through the existing <c>KW-CONFIG-001</c>
    /// path and fails the command, exactly as it does today. The check must never be the reason an
    /// invalid declaration is tolerated.
    /// </summary>
    [Fact]
    public void AMalformedDeclarationFailsAsAConfigurationErrorRatherThanBeingIgnored()
    {
        string repository = NewRepository();
        PutLaunchBinaryOnPath(McpBinary);
        Directory.CreateDirectory(Path.Combine(repository, ".cursor"));
        DeclareServers(
            repository,
            $"""
            {KyberWeaveServer}:
              command: []
              provenance: first-party
            """);

        CapturedConsoleExecution<int> execution = RunDocsInit(repository);

        Assert.NotEqual(0, execution.Result);
        Assert.Contains(KyberWeaveConfigLoader.ConfigLoadErrorCode, execution.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("KW-MCP-WIRE-", execution.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// R2.5: the Copilot coding agent has no configuration file in the tree, so the only way to wire
    /// it is by hand. The snippet and the workflow step are the whole of its report, which means the
    /// operator's console — not the finding object — has to carry them.
    /// </summary>
    /// <remarks>
    /// This is the test the earlier one was not. Asserting the hint on the finding proves the data
    /// exists and says nothing about the operator ever seeing it, and the section suppressed every
    /// not-detectable hint: three executed runs reported the harness and then stopped, leaving an
    /// operator told to configure something with no statement of how.
    /// </remarks>
    [Fact]
    public void TheCopilotCodingAgentsSnippetAndWorkflowStepReachTheConsole()
    {
        string repository = NewRepository();
        PutLaunchBinaryOnPath(McpBinary);
        Directory.CreateDirectory(Path.Combine(repository, ".factory"));
        WriteFile(
            repository,
            ".factory/mcp.json",
            """
            {
              "mcpServers": {
                "kyber-weave": {
                  "command": "kyber-weave-mcp",
                  "args": ["--repo-root", "."]
                }
              }
            }
            """);

        CapturedConsoleExecution<int> execution = RunDocsInit(repository);

        AssertSucceeded(execution);

        string[] lines = OutputLines(execution.Output);
        int copilot = Array.FindIndex(
            lines,
            line => line.Contains("copilot-coding-agent", StringComparison.Ordinal));
        Assert.True(
            copilot >= 0,
            $"No output line named the Copilot coding agent. Output:{Environment.NewLine}{execution.Output}");

        // The snippet says which key the entry goes under; the workflow step says which file has to
        // install the server before the agent starts. Both belong to that harness's report, so both
        // are looked for after its line rather than anywhere in the run.
        int snippet = Array.FindIndex(
            lines,
            line => line.Contains("mcpServers", StringComparison.Ordinal));
        Assert.True(
            snippet > copilot,
            $"The Copilot report never reached the operator: no line after it named the 'mcpServers' "
                + $"entry it needs. Output:{Environment.NewLine}{execution.Output}");

        int workflowStep = Array.FindIndex(
            lines,
            line => line.Contains("copilot-setup-steps.yml", StringComparison.Ordinal));
        Assert.True(
            workflowStep > copilot,
            $"The Copilot report never reached the operator: no line after it named the "
                + $"copilot-setup-steps.yml workflow step it needs. Output:{Environment.NewLine}{execution.Output}");
    }

    /// <summary>
    /// R2.4: a harness with neither a configuration file nor a marker is one <c>info</c> line naming
    /// what was looked for — one line, whatever the number of declared servers, and with no advice
    /// attached.
    /// </summary>
    /// <remarks>
    /// This is the constraint the Copilot report must not break. Teaching the section to print a
    /// not-detectable hint would print one for every harness in the catalog, so a repository that
    /// uses none of them would be told, at length, to wire six harnesses it does not have. Counting
    /// the lines that mention the harness is what distinguishes "one report" from "one report and its
    /// advice".
    /// </remarks>
    [Fact]
    public void AnUndetectableHarnessIsOneInfoLineAndNoAdvice()
    {
        string repository = NewRepository();
        PutLaunchBinaryOnPath(McpBinary);

        // Two declared servers, so a section that printed per server would print twice.
        DeclareServers(
            repository,
            $"""
            {KyberWeaveServer}:
              command: [kyber-weave-mcp, --repo-root, .]
              provenance: first-party
              level: required
            {AbsentThirdPartyBinary}:
              command: [{AbsentThirdPartyBinary}, serve, --mcp]
              provenance: third-party
              level: recommended
            """);

        CapturedConsoleExecution<int> execution = RunDocsInit(repository);

        AssertSucceeded(execution);

        string[] factoryLines =
        [
            .. OutputLines(execution.Output).Where(line => line.Contains(Factory, StringComparison.Ordinal))
        ];
        string line = Assert.Single(factoryLines);

        Assert.Contains(McpWiringInspector.NotDetectableCode, line, StringComparison.Ordinal);
        Assert.Contains(".factory", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// R4.1: the question offers writes the repair will actually perform. A gap the repair refuses —
    /// here an entry that already exists with a different launch command, which 5.5 forbids the
    /// repair from modifying — must not be offered, because a question is a request for consent to a
    /// specific write and consent to a refusal is not consent to anything.
    /// </summary>
    [Fact]
    public void ThePromptIsNotOfferedForAGapTheRepairWouldRefuse()
    {
        bool offered = false;
        DocsInitCommand.InteractiveCheckOverride = static () => true;
        DocsInitCommand.ConfirmFixOverride = _ =>
        {
            offered = true;
            return true;
        };

        string repository = NewRepository();
        PutLaunchBinaryOnPath(McpBinary);
        WriteFile(
            repository,
            CursorConfigRelativePath,
            """
            {
              "mcpServers": {
                "kyber-weave": {
                  "command": "kyber-weave-mcp",
                  "args": ["--repo-root", "/somewhere/else"]
                }
              }
            }
            """);

        CapturedConsoleExecution<int> execution = RunDocsInit(repository);

        AssertSucceeded(execution);
        Assert.Contains(McpWiringInspector.CommandDiffersCode, execution.Output, StringComparison.Ordinal);
        Assert.False(
            offered,
            "docs init offered a repair for a file the repair refuses to modify (5.5).");
    }

    /// <summary>
    /// R3.4: a declaration that names no server has nothing to check, so no wiring finding is printed
    /// and the run that scaffolded the corpus still exits 0. A <c>mcp:</c> key with nothing under it is
    /// the state a repository reaches by editing the scaffolded servers out, and the one that says
    /// "this file declares no dependency" while still loading.
    /// </summary>
    [Fact]
    public void ADeclarationNamingNoServerPrintsNoWiringFindingAndStaysAtExitZero()
    {
        string repository = NewRepository();
        WriteFile(
            repository,
            ".kyber-weave/kyber-weave.yml",
            "ontology:\n  docs-root: docs\nmcp:\n");

        CapturedConsoleExecution<int> execution = RunDocsInit(repository);

        AssertSucceeded(execution);
        Assert.DoesNotContain(McpWiringInspector.NotWiredCode, execution.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(McpWiringInspector.BinaryAbsentCode, execution.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The repair <c>docs init</c> offers resolves a repository-relative launch command against the
    /// repository root, the same way the dedicated repair command does.
    /// </summary>
    /// <remarks>
    /// Two servers are declared so the case can tell the two resolvers apart: one is named by an
    /// absolute path, so it resolves wherever the command was run from and is what makes the prompt
    /// appear at all; the other is named relative to the repository root, so only a resolver given
    /// that root finds it. The file the repair writes is the assertion — a repair whose resolver
    /// read the process's working directory would write the absolute server and skip the relative
    /// one, which is exactly the asymmetry between the two verbs this case exists to rule out.
    /// </remarks>
    [Fact]
    public void TheOfferedRepairResolvesARepositoryRelativeCommandAgainstTheRoot()
    {
        string repository = NewRepository();
        Directory.CreateDirectory(Path.Combine(repository, ".cursor"));
        string absolute = McpWiringFixture.WriteExecutable(Bin, "kw-t6-absolute-server");

        DeclareServers(
            repository,
            $"""
            absolute-server:
              command: ['{absolute}', --repo-root, .]
              provenance: third-party
              level: required
            relative-server:
              command: [tools/serve-mcp, --repo-root, .]
              provenance: third-party
              level: required
            """);
        McpWiringFixture.WriteExecutable(Path.Combine(repository, "tools"), "serve-mcp");

        DocsInitCommand.InteractiveCheckOverride = static () => true;
        DocsInitCommand.ConfirmFixOverride = _ => true;

        CapturedConsoleExecution<int> execution = RunDocsInit(repository);

        AssertSucceeded(execution);

        string path = CursorConfigPath(repository);
        Assert.True(
            File.Exists(path),
            $"the confirmed repair wrote nothing to {path}. Output:{Environment.NewLine}{execution.Output}");

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.True(
            document.RootElement.GetProperty("mcpServers").TryGetProperty("relative-server", out _),
            $"the repair skipped the command that is relative to the repository root. File:"
                + $"{Environment.NewLine}{File.ReadAllText(path)}");
    }

    /// <summary>
    /// An empty repository, git-initialized so the check's worktree walk stops at this root instead
    /// of climbing into the temp directory's ancestors.
    /// </summary>
    private string NewRepository()
    {
        string repository = Path.Combine(_temp.Path, "repo-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(repository);
        InitializeWorktree(repository);
        return repository;
    }

    /// <summary>
    /// Puts a fake launch binary at the front of this process's <c>PATH</c>. <c>docs init</c> builds
    /// its inspector from the environment, so the only way to make a declared server look
    /// launchable end to end is a real file where a launch would find it. The previous
    /// <c>PATH</c> is restored on dispose.
    /// </summary>
    /// <remarks>
    /// A file, not a process: the resolver is an existence check, and an empty file proves nothing
    /// was launched. The execute bit is set where the platform has one so the fixture looks like an
    /// installed binary; on Windows the file carries the extension PATHEXT resolution looks for.
    /// </remarks>
    private void PutLaunchBinaryOnPath(string binaryName)
    {
        McpWiringFixture.WriteExecutable(Bin, binaryName);

        _pathBeforeFakeBinaries ??= Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", Bin + Path.PathSeparator + _pathBeforeFakeBinaries);

        string? resolved = LaunchCommandResolver.FromEnvironment().Resolve(binaryName);
        Assert.True(
            resolved is not null,
            $"The fixture's '{binaryName}' is not resolvable on PATH, so this case would prove nothing.");
    }

    /// <summary>
    /// Writes a host configuration declaring servers before the scaffold runs. <c>docs init</c>
    /// never rewrites an existing <c>mcp:</c> section, so the fixture's declaration is what the
    /// check reads. <paramref name="serverEntries"/> is indented one level beneath
    /// <c>required-servers</c>.
    /// </summary>
    private static void DeclareServers(string repository, string serverEntries)
    {
        StringBuilder yaml = new();
        yaml.AppendLine("ontology:");
        yaml.AppendLine("  docs-root: docs");
        yaml.AppendLine("mcp:");
        yaml.AppendLine("  required-servers:");

        foreach (string line in serverEntries.Split('\n'))
        {
            string entry = line.TrimEnd();
            if (entry.Length == 0)
                continue;

            yaml.Append("    ").AppendLine(entry);
        }

        WriteFile(repository, ".kyber-weave/kyber-weave.yml", yaml.ToString());
    }

    private static void WriteFile(string repository, string relativePath, string content)
    {
        string path = Path.Combine(repository, relativePath.Replace('/', Path.DirectorySeparatorChar));
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(path, content);
    }

    private static void InitializeWorktree(string repository)
    {
        ProcessStartInfo startInfo = new("git")
        {
            WorkingDirectory = repository,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("init");
        startInfo.ArgumentList.Add("--quiet");

        ProcessResult result = ProcessRunner.Run(startInfo, string.Empty, TimeSpan.FromSeconds(60));
        Assert.True(
            result.ExitCode == 0,
            $"git init failed in '{repository}': {result.StandardError}");
    }

    private static string CursorConfigPath(string repository) =>
        Path.Combine(repository, ".cursor", "mcp.json");

    private static CapturedConsoleExecution<int> RunDocsInit(
        string repository,
        params string[] extraArguments)
    {
        List<string> arguments = ["docs", "init", repository, "--no-skill"];
        arguments.AddRange(extraArguments);

        return ProcessConsoleCapture.Run(() => CommandAppFixture.DocsInit().Run(arguments.ToArray()));
    }

    private static void AssertSucceeded(CapturedConsoleExecution<int> execution) =>
        Assert.True(
            execution.Result == 0,
            $"docs init exited {execution.Result}. Output:{Environment.NewLine}{execution.Output}");

    private static string[] OutputLines(string output) =>
        [.. output.Split('\n').Select(line => line.TrimEnd('\r'))];

    /// <summary>The one output line carrying <paramref name="token"/>, so a case can assert on it.</summary>
    private static string TheLineContaining(string output, string token)
    {
        string[] matching =
        [
            .. OutputLines(output).Where(line => line.Contains(token, StringComparison.Ordinal))
        ];

        Assert.True(
            matching.Length > 0,
            $"No output line carried '{token}'. Output:{Environment.NewLine}{output}");

        return matching[0];
    }
}

/// <summary>
/// The collection the wiring-check cases run in. Both resources they mutate — the prompt seams and
/// this process's <c>PATH</c> — are process-global, so no other test may observe them mid-change.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
[SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "xUnit collection definitions are named <Name>Collection by convention; the suffix "
        + "is the framework idiom, not a claim to be a collection type.")]
public sealed class DocsInitMcpCheckEnvironmentCollection
{
    /// <summary>The collection's name, as the test class refers to it.</summary>
    public const string Name = "docs init MCP wiring check environment";
}
