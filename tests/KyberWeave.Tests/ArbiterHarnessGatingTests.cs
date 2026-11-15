using System.Text.RegularExpressions;
using KyberWeave.Cli.Commands.Arbiter;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Squad.Deployment;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Contract: <c>arbiter doctor</c> and <c>arbiter status</c> give every installed harness its own
/// gating line. A harness is installed when the global targets list it, the global Squad receipt
/// owns an Arbiter hook for it, or a project Arbiter deployment reaches it. A project that enables
/// Arbiter must never be reported as gated just because some other harness has a project hook: that
/// is how a Claude project went ungated without anyone seeing it.
/// </summary>
/// <remarks>
/// Every test isolates the home directory and the environment reader, so the real user home and
/// the real environment are never read or written. Global state lives only in the temp home.
/// </remarks>
[Collection(ArbiterCommandCompositionCollection.Name)]
public sealed class ArbiterHarnessGatingTests : IDisposable
{
    private const string ZeroDigest = "0000000000000000000000000000000000000000000000000000000000000000";
    private const string ClaudeHookCommand = "kyber-weave-arbiter hook --harness claude --scope global";
    private const string ClaudeProjectAgentPath = ".claude/agents/csharp-dev.md";

    private readonly TempDirectory _home = new();
    private readonly TempDirectory _repo = new();
    private readonly TempDirectory _userPaths = new();
    private readonly Func<string> _savedHome;
    private readonly Func<string, string?> _savedEnvironment;

    public ArbiterHarnessGatingTests()
    {
        _savedHome = ArbiterCommandComposition.HomeDirectory;
        _savedEnvironment = ArbiterCommandComposition.GetEnvironmentVariable;

        ArbiterCommandComposition.HomeDirectory = () => _home.Path;
        ArbiterCommandComposition.GetEnvironmentVariable = name =>
            name is "HOME" or "USERPROFILE" ? _home.Path : null;
    }

    public void Dispose()
    {
        ArbiterCommandComposition.HomeDirectory = _savedHome;
        ArbiterCommandComposition.GetEnvironmentVariable = _savedEnvironment;
        _home.Dispose();
        _repo.Dispose();
        _userPaths.Dispose();
    }

    /// <summary>
    /// Two harnesses listed globally, with a project that enables Arbiter and no hook of any kind. Both
    /// are installed and neither is gated, so each gets an ungated line with its own fix command, and
    /// each raises exactly one project diagnostic. Claude is not installed here and must not appear.
    /// </summary>
    [Fact]
    public void Doctor_ListsEachUngatedInstalledHarnessWithItsFixCommand_AndNeverNamesAnAbsentClaude()
    {
        UserHostConfig.WriteGlobalTargets(_home.Path, ["antigravity", "copilot"]);
        WriteProjectConfig(EnabledYaml);

        string output = RunDoctor().Output;

        AssertNoLineNames(output, "Claude");
        Assert.DoesNotContain("Project hooks: installed", output, StringComparison.Ordinal);
        AssertLineNames(output, "Antigravity", "ungated", "kyber-weave arbiter install --project --target antigravity");
        AssertLineNames(output, "Copilot", "ungated", "kyber-weave arbiter install --project --target copilot");
        // One printed warning and one reported finding per ungated harness. The finding is a whole line now that
        // redirected output is not a wrapped table.
        Assert.Equal(2, CountOf(output, "warn KW-ARB-PROJECT-001"));
        Assert.Equal(2, CountOf(output, "WARNING KW-ARB-PROJECT-001"));
    }

    /// <summary>
    /// The user-level Claude hook is written with the PascalCase <c>PreToolUse</c> key and a receipt
    /// block exists for Claude, so Claude is live under the global hook for this project's configuration.
    /// </summary>
    [Fact]
    public void Doctor_NamesClaudeAsGatedByTheGlobalHookUnderThisProjectsConfiguration_WhenTheUserHookIsLive()
    {
        SetUpLiveClaudeHook("PreToolUse");
        WriteProjectConfig(EnabledYaml);

        string output = RunDoctor().Output;

        AssertLineNames(output, "Claude", "gated by the global hook under this project's configuration");
        Assert.DoesNotContain("KW-ARB-PROJECT-001", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same user-level hook under the lower-case <c>preToolUse</c> key is not read by Claude, so it
    /// does not gate anything: Claude is reported ungated with the Claude fix command.
    /// </summary>
    [Fact]
    public void Doctor_ReportsClaudeUngated_WhenTheUserHookSitsOnlyUnderLowerCasePreToolUse()
    {
        SetUpLiveClaudeHook("preToolUse");
        WriteProjectConfig(EnabledYaml);

        string output = RunDoctor().Output;

        AssertLineNames(output, "Claude", "ungated", "kyber-weave arbiter install --target claude");
        Assert.Contains("KW-ARB-PROJECT-001", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A project agent file carries its own Arbiter hook for Claude. That is the project source that
    /// gates Claude, and the Claude line must name the file so the operator can see where it comes from.
    /// </summary>
    [Fact]
    public void Doctor_NamesTheClaudeProjectAgentFile_WhenProjectHooksGateClaude()
    {
        SetUpLiveClaudeHook("PreToolUse");
        WriteProjectConfig(EnabledYaml);
        WriteFile(
            Path.Combine(_repo.Path, ClaudeProjectAgentPath),
            """
            ---
            name: csharp-dev
            hooks:
              PreToolUse:
                - matcher: "^(Agent|Task)$"
                  hooks:
                    - type: command
                      command: kyber-weave-arbiter hook --harness claude --caller csharp-dev
            ---
            """);

        string output = RunDoctor().Output;

        AssertLineNames(output, "Claude", ClaudeProjectAgentPath);
    }

    /// <summary>
    /// The global receipt owns a Claude hook block, but Claude is not in the global targets. The
    /// receipt says Squad installed it, so the global targets file is out of step with the install.
    /// </summary>
    [Fact]
    public void Doctor_WarnsGlobalWhenTheClaudeReceiptBlockIsNotInGlobalTargets()
    {
        WriteGlobalReceipt(
            files: [],
            blocks: [ClaudeBlock()]);
        UserHostConfig.WriteGlobalTargets(_home.Path, ["copilot"]);
        WriteProjectConfig(EnabledYaml);

        string output = RunDoctor().Output;

        Assert.Contains("KW-ARB-GLOBAL-001", output, StringComparison.Ordinal);
        Assert.Contains("kyber-weave arbiter install --target claude", output, StringComparison.Ordinal);
    }

    /// <summary>The same receipt block with Claude listed in the global targets is consistent, so no global warning.</summary>
    [Fact]
    public void Doctor_DoesNotWarnGlobalWhenTheClaudeReceiptBlockIsListedInGlobalTargets()
    {
        WriteGlobalReceipt(
            files: [],
            blocks: [ClaudeBlock()]);
        UserHostConfig.WriteGlobalTargets(_home.Path, ["claude"]);
        WriteProjectConfig(EnabledYaml);

        string output = RunDoctor().Output;

        Assert.DoesNotContain("KW-ARB-GLOBAL-001", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// An ordinary agent or skill file owned by the receipt is not an Arbiter hook, so it does not make
    /// Codex installed. Codex must not get a line and must not raise either diagnostic.
    /// </summary>
    [Fact]
    public void Doctor_IgnoresOrdinaryAgentFilesInTheGlobalReceipt_ForCodex()
    {
        WriteGlobalReceipt(
            files: [OwnedFile("agents/architect/references/arbiter-escalation.md", "codex")],
            blocks: []);
        WriteProjectConfig(EnabledYaml);

        string output = RunDoctor().Output;

        AssertNoGatingLineNames(output, "Codex");
        Assert.DoesNotContain("KW-ARB-GLOBAL-001", output, StringComparison.Ordinal);
        Assert.DoesNotContain("KW-ARB-PROJECT-001", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Copilot's own shim hook file is an Arbiter hook owned by the global receipt. Copilot is installed
    /// but not in the global targets, so the global warning names it and gives its install command.
    /// </summary>
    [Fact]
    public void Doctor_WarnsGlobalWhenTheCopilotShimHookFileIsOwnedButCopilotIsNotListed()
    {
        WriteGlobalReceipt(
            files: [OwnedFile("hooks/kyber-arbiter.json", "copilot")],
            blocks: []);
        UserHostConfig.WriteGlobalTargets(_home.Path, ["claude"]);
        WriteProjectConfig(EnabledYaml);

        string output = RunDoctor().Output;

        Assert.Contains("KW-ARB-GLOBAL-001", output, StringComparison.Ordinal);
        AssertLineNames(output, "Copilot", "kyber-weave arbiter install --target copilot");
    }

    /// <summary>
    /// Warp has no hook and no fix. It is fallback-only by design, so it neither gets a fix command nor
    /// raises a project diagnostic.
    /// </summary>
    [Fact]
    public void Doctor_ReportsWarpAsFallbackOnlyWithoutAFixOrADiagnostic()
    {
        UserHostConfig.WriteGlobalTargets(_home.Path, ["warp"]);
        WriteProjectConfig(EnabledYaml);

        string output = RunDoctor().Output;

        AssertLineNames(output, "Warp", "fallback-only");
        Assert.DoesNotContain(
            LinesNaming(output, "Warp"),
            line => line.Contains("kyber-weave arbiter install", StringComparison.Ordinal));
        Assert.DoesNotContain("KW-ARB-PROJECT-001", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The project turns Arbiter off. Each installed harness is opted out, not ungated, so the project
    /// diagnostic stays silent.
    /// </summary>
    [Fact]
    public void Doctor_ReportsEachInstalledHarnessOptedOut_WhenTheProjectDisablesArbiter()
    {
        UserHostConfig.WriteGlobalTargets(_home.Path, ["antigravity", "copilot"]);
        WriteProjectConfig(
            """
            arbiter:
              enabled: false
            """);

        string output = RunDoctor().Output;

        AssertLineNames(output, "Antigravity", "opted out");
        AssertLineNames(output, "Copilot", "opted out");
        Assert.DoesNotContain("KW-ARB-PROJECT-001", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>arbiter status</c> uses the same per-harness lines as the doctor, and never reports a project
    /// override that hides a harness nothing gates.
    /// </summary>
    [Fact]
    public void Status_NamesClaudeGatedByTheGlobalHook_AndNeverReportsAProjectOverride()
    {
        SetUpLiveClaudeHook("PreToolUse");
        WriteProjectConfig(EnabledYaml);

        CommandExecution execution = Capture(() => new ArbiterStatusCommand().Execute(
            null!,
            new ArbiterSettings { Path = _repo.Path }));

        Assert.Equal(0, execution.ExitCode);
        Assert.DoesNotContain("project overrides global", execution.Output, StringComparison.OrdinalIgnoreCase);
        AssertLineNames(execution.Output, "Claude", "gated by the global hook under this project's configuration");
    }

    private const string EnabledYaml =
        """
        arbiter:
          enabled: true
        """;

    /// <summary>
    /// Installs the user-level Claude hook under <paramref name="eventKey"/> in the temp home's
    /// <c>settings.json</c> and writes a global receipt block for Claude, so Claude is installed.
    /// </summary>
    private void SetUpLiveClaudeHook(string eventKey)
    {
        UserHostConfig.WriteGlobalTargets(_home.Path, ["claude"]);
        WriteFile(Path.Combine(_home.Path, ".claude", "settings.json"), UserClaudeSettings(eventKey));
        WriteGlobalReceipt(files: [], blocks: [ClaudeBlock()]);
    }

    private static string UserClaudeSettings(string eventKey) =>
        """
        {
          "hooks": {
            "EVENT": [
              {
                "matcher": "^(Agent|Task)$",
                "hooks": [
                  { "type": "command", "command": "COMMAND" }
                ]
              }
            ]
          }
        }
        """
            .Replace("EVENT", eventKey, StringComparison.Ordinal)
            .Replace("COMMAND", ClaudeHookCommand, StringComparison.Ordinal);

    private static SquadOwnedBlock ClaudeBlock() =>
        new(
            "settings.json",
            "claude",
            CreatedFile: false,
            Entries: [new SquadOwnedBlockEntry("/hooks/PreToolUse/0", ZeroDigest)]);

    /// <summary>User paths whose home is the temp home, so the store's home-level state lands beside the doctor's reads.</summary>
    private sealed class TempHomeUserPaths(string homeDirectory, string applicationDataDirectory) : ISquadUserPaths
    {
        public string ApplicationDataDirectory { get; } = applicationDataDirectory;

        public string HomeDirectory { get; } = homeDirectory;
    }

    private static SquadOwnedFile OwnedFile(string relativePath, string target) =>
        new(relativePath, ZeroDigest, target, Adopted: false);

    /// <summary>
    /// Writes the global Squad receipt where a real install for the temp home writes it, and where the
    /// gating code reads it back: under the home directory itself. Global state is home-anchored only
    /// when the store's user paths report that home, so the store is built from user paths whose home
    /// is the temp home, as the production reader builds its own.
    /// </summary>
    private void WriteGlobalReceipt(IReadOnlyList<SquadOwnedFile> files, IReadOnlyList<SquadOwnedBlock> blocks)
    {
        SquadStateStore store = SquadCommandComposition.ResolveStateStore(
            new TempHomeUserPaths(_home.Path, _userPaths.Path));
        string path = store.ResolveReceiptPath(_home.Path, SquadDeploymentScope.Global);
        string schema = blocks.Count > 0 ? "kyber-squad.receipt/v3" : "kyber-squad.receipt/v1";
        WriteFile(
            path,
            store.SerializeReceipt(new SquadReceipt(
                schema,
                SquadDeploymentScope.Global,
                _home.Path,
                DateTimeOffset.UtcNow,
                [],
                files)
            {
                Blocks = blocks,
            }));
    }

    private void WriteProjectConfig(string yaml)
    {
        WriteFile(Path.Combine(_repo.Path, ".kyber-weave", "kyber-weave.yml"), yaml);
    }

    private static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private CommandExecution RunDoctor() =>
        Capture(() => new ArbiterDoctorCommand().Execute(
            null!,
            new ArbiterSettings { Path = _repo.Path }));

    private static IEnumerable<string> LinesNaming(string output, string label) =>
        output.Split('\n').Where(line => Regex.IsMatch(line, $@"\b{label}\b"));

    /// <summary>Asserts one line names <paramref name="label"/> and carries every fragment.</summary>
    private static void AssertLineNames(string output, string label, params string[] fragments)
    {
        bool found = LinesNaming(output, label).Any(line => fragments.All(fragment => line.Contains(fragment, StringComparison.Ordinal)));
        Assert.True(
            found,
            $"Expected a line naming '{label}' with [{string.Join(", ", fragments)}]. Output:\n{output}");
    }

    private static void AssertNoLineNames(string output, string label)
    {
        IEnumerable<string> named = LinesNaming(output, label);
        Assert.True(!named.Any(), $"Expected no line naming '{label}', found: {string.Join(" | ", named)}");
    }

    /// <summary>
    /// Asserts no line naming <paramref name="label"/> is a gating verdict or a fix command. An advisory
    /// info line about post-dispatch rendering may still name the harness: it is not a gating state.
    /// </summary>
    private static void AssertNoGatingLineNames(string output, string label)
    {
        string[] verdicts = ["ungated", "gated by", "opted out", "fallback-only", "kyber-weave arbiter install"];
        IEnumerable<string> offending = LinesNaming(output, label)
            .Where(line => verdicts.Any(verdict => line.Contains(verdict, StringComparison.Ordinal)));
        Assert.True(
            !offending.Any(),
            $"Expected no gating line naming '{label}', found: {string.Join(" | ", offending)}");
    }

    private static int CountOf(string output, string value) =>
        Regex.Count(output, Regex.Escape(value));

    private static CommandExecution Capture(Func<int> execute)
    {
        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(execute);
        return new CommandExecution(execution.Result, execution.Output);
    }

    private sealed record CommandExecution(int ExitCode, string Output);
}
