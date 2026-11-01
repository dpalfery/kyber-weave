using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text;
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
        Directory.CreateDirectory(Bin);
        string path = Path.Combine(Bin, OperatingSystem.IsWindows() ? binaryName + ".exe" : binaryName);
        File.WriteAllText(path, string.Empty);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

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

        return ProcessConsoleCapture.Run(() => DocsApp().Run(arguments.ToArray()));
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

    private static CommandApp DocsApp()
    {
        CommandApp app = new();
        app.Configure(config =>
        {
            config.SetApplicationName("kyber-weave");
            config.AddBranch("docs", docs => { docs.AddCommand<DocsInitCommand>("init"); });
        });

        return app;
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
