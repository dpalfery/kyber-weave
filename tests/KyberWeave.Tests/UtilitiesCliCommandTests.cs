using System.Diagnostics;
using System.Text;
using KyberWeave.Core.Processes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Contract suite for the <c>kyber-weave utilities statusline</c> command surface (development mode:
/// test-first, docs/archive/plans/2026-09-28-kyber-utilities-status-line-slice.md, Test contract row T6).
/// Pins the four verbs — <c>deploy</c>, <c>status</c>, <c>doctor</c>, and <c>remove</c> — as an
/// operator reaches them: an argv in, an exit code and rendered output out. It pins the exit-2
/// client-input refusal for the dropped OpenCode target (D9) and for an unknown target, that a deploy
/// with no <c>--target</c> covers Claude Code, <c>agy</c>, and Pi while a targeted deploy covers
/// exactly one, that activation guidance names absolute paths and states the operator applies the
/// edit, that <c>--dry-run</c> writes nothing, and that no verb ever creates, names, or modifies a
/// harness settings file (A4, C2, D5).
/// </summary>
/// <remarks>
/// <para>
/// Every case drives the built CLI as a child process rather than calling a command type, because the
/// contract is the command surface and not a type shape. That is also what makes the RED run honest:
/// this suite references no Kyber Utilities type, so it compiles against a tree where the branch does
/// not exist and fails there at run time — the program answers "Unknown command" — instead of at
/// compile time. A compile error is not valid RED evidence for a CLI contract.
/// </para>
/// <para>
/// Each case runs in an isolated temporary home with <c>HOME</c>, <c>USERPROFILE</c>, and the three
/// documented harness-root overrides pointed into it, and with <c>PATH</c> replaced by a stub
/// directory this suite owns, so doctor's harness and prerequisite probes are deterministic and no
/// case can read or write the operator's real harness configuration. The deployable artifacts come
/// from a synthetic product tree inside a temporary checkout, which is the seam this implementation
/// offers a process host: <c>UtilitiesStatusLineProductLocator</c> resolves
/// <c>products/kyber-utilities/statusline</c> from the process working directory. The fixture is
/// synthetic (C3); nothing here reads the real artifact tree.
/// </para>
/// <para>
/// Containment checks compare against the output with every whitespace character removed. Spectre
/// wraps rendered lines at the console width, and a temporary home path is long enough that a wrap
/// can split an absolute path in the middle, so a raw substring check would fail on formatting
/// rather than on behaviour. The paths, JSON snippets, and phrases this suite pins contain no
/// whitespace, so removing it from both sides is lossless for them.
/// </para>
/// </remarks>
public sealed class UtilitiesCliCommandTests : IDisposable
{
    private const string ClaudeConfigDirectoryVariable = "CLAUDE_CONFIG_DIR";
    private const string AgyConfigDirectoryVariable = "AGY_CONFIG_DIR";
    private const string PiCodingAgentDirectoryVariable = "PI_CODING_AGENT_DIR";

    /// <summary>
    /// The Kyber-owned staging segment beneath every harness root. A literal, so this suite pins
    /// where a deployment lands rather than restating whatever the implementation returns.
    /// </summary>
    private const string StagingSegment = "kyber/statusline";

    private const string SolutionMarkerFileName = "KyberWeave.sln";
    private const string ProductRelativePath = "products/kyber-utilities/statusline";
    private const string CliHostFileName = "kyber-weave";
    private const string OpenCodeDeferralIssueNumber = "165";
    private const string ApplyYourselfLine = "Apply this snippet yourself.";
    private const string SettingsSentinelContent = "{\"sentinel\":\"never-touched-by-kyber\"}\n";
    private const string EditedFileContent = "locally edited by the user\n";

    /// <summary>How long a CLI invocation may run before the case fails as hung.</summary>
    private static readonly TimeSpan CliTimeout = TimeSpan.FromSeconds(60);

    /// <summary>The four verbs the branch must register.</summary>
    private static readonly string[] Verbs = ["deploy", "status", "doctor", "remove"];

    /// <summary>The three harnesses this slice keeps (D9), as the CLI's own target tokens.</summary>
    private static readonly string[] HarnessTokens = ["claude", "agy", "pi"];

    /// <summary>
    /// The two harnesses whose artifact is a command their host runs directly, so the deployed file
    /// must carry the executable bit. Pi's artifact is a module Node loads, and is not one of these.
    /// </summary>
    private static readonly string[] CommandVariantTokens = ["claude", "agy"];

    /// <summary>Every executable doctor probes, other than the optional <c>kyberdash</c> (C8).</summary>
    private static readonly string[] StubExecutables =
        ["claude", "agy", "pi", "bash", "jq", "git", "awk", "python3", "node"];

    /// <summary>Every runtime prerequisite the T0 inventory declares, for the healthy-doctor case.</summary>
    private static readonly string[] DeclaredPrerequisites = ["bash", "jq", "git", "awk", "python3", "node"];

    /// <summary>
    /// The harness settings files this slice must never create, open, or modify, relative to the
    /// isolated home. The same three literals the deployment suite pins, kept literal here too, so
    /// the guarantee is asserted against the operator's real settings locations rather than against
    /// whatever the implementation happens to name.
    /// </summary>
    private static readonly string[] ConfirmedSettingsRelativePaths =
    [
        ".claude/settings.json",
        ".gemini/antigravity-cli/settings.json",
        ".pi/agent/settings.json"
    ];

    private readonly TempDirectory _temp = new();
    private readonly string _checkout;
    private readonly string _home;
    private readonly string _stubBin;

    public UtilitiesCliCommandTests()
    {
        _checkout = Path.Combine(_temp.Path, "checkout");
        _home = Path.Combine(_temp.Path, "home");
        _stubBin = Path.Combine(_temp.Path, "stub-bin");

        Directory.CreateDirectory(_home);
        WriteProductTree();
        WriteStubExecutables(_stubBin, StubExecutables);
    }

    public void Dispose()
    {
        _temp.Dispose();
    }

    // -----------------------------------------------------------------------------------------
    // Registration: the branch and its four verbs, reached through the program entry point.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void TheStatusLineBranchRegistersDeployStatusDoctorAndRemove()
    {
        CliResult run = Run("utilities", "statusline", "--help");

        AssertExitCode(0, run);
        foreach (string verb in Verbs)
            Assert.Contains(verb, run.Output, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------------------------
    // Target selection: the D9 three-harness scope, and the exit-2 client-input refusals.
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("--target")]
    [InlineData("-t")]
    public void DeployRefusesTheDroppedOpenCodeTargetWithExitTwoAndADeferralHint(string targetOption)
    {
        CliResult run = Run("utilities", "statusline", "deploy", targetOption, "opencode");

        string output = Compact(run.Output);
        AssertExitCode(2, run);
        Assert.Contains("opencode", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(OpenCodeDeferralIssueNumber, output, StringComparison.Ordinal);
        Assert.Contains("error", output, StringComparison.OrdinalIgnoreCase);
        // Exit 2 is the client-input code, so the refusal must read as a refusal and not as a crash.
        Assert.DoesNotContain("unhandledexception", output, StringComparison.OrdinalIgnoreCase);
        AssertNoHarnessRootExists();
    }

    [Theory]
    [InlineData("windows")]
    [InlineData("gemini")]
    public void DeployRefusesAnUnknownTargetWithExitTwoAndNamesTheSupportedOnes(string target)
    {
        CliResult run = Run("utilities", "statusline", "deploy", "--target", target);

        string output = Compact(run.Output);
        AssertExitCode(2, run);
        Assert.Contains(target, output, StringComparison.OrdinalIgnoreCase);
        foreach (string token in HarnessTokens)
            Assert.Contains(token, output, StringComparison.Ordinal);
        AssertNoHarnessRootExists();
    }

    [Fact]
    public void DeployWithNoTargetStagesAllThreeHarnesses()
    {
        CliResult run = Run("utilities", "statusline", "deploy");

        AssertExitCode(0, run);
        string output = Compact(run.Output);
        foreach (string token in HarnessTokens)
        {
            AssertFileBytes(DeployedPath(token), ArtifactContent(token));
            Assert.Contains(Compact(StagingRoot(token)), output, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("--target", "claude")]
    [InlineData("-t", "claude")]
    [InlineData("--target", "agy")]
    [InlineData("-t", "agy")]
    [InlineData("--target", "pi")]
    [InlineData("-t", "pi")]
    public void DeployWithATargetStagesExactlyThatHarness(string targetOption, string token)
    {
        CliResult run = Run("utilities", "statusline", "deploy", targetOption, token);

        AssertExitCode(0, run);
        AssertFileBytes(DeployedPath(token), ArtifactContent(token));
        foreach (string other in HarnessTokens.Where(candidate =>
                     !string.Equals(candidate, token, StringComparison.Ordinal)))
            Assert.False(
                Directory.Exists(HarnessRoot(other)),
                $"A '{token}' deploy also created the {other} harness root '{HarnessRoot(other)}'.");
    }

    // -----------------------------------------------------------------------------------------
    // Activation guidance: absolute paths, the operator's own edit, and no settings path.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void DeployPrintsAbsoluteActivationSnippetsAndSaysTheOperatorAppliesTheEdit()
    {
        CliResult run = Run("utilities", "statusline", "deploy");

        AssertExitCode(0, run);
        string output = Compact(run.Output);

        // The snippet values, not merely the paths somewhere in the output: this is what the operator
        // copies into their own settings, so it is the value that must be absolute.
        Assert.Contains(Compact($"\"command\": \"{DeployedPath("claude")}\""), output, StringComparison.Ordinal);
        Assert.Contains(Compact($"\"command\": \"{DeployedPath("agy")}\""), output, StringComparison.Ordinal);
        Assert.Contains(Compact($"\"extensions\": [\"{DeployedPath("pi")}\"]"), output, StringComparison.Ordinal);
        Assert.Contains(Compact(ApplyYourselfLine), output, StringComparison.Ordinal);
        Assert.DoesNotContain("\"command\":\"~", output, StringComparison.Ordinal);
        Assert.DoesNotContain("\"extensions\":[\"~", output, StringComparison.Ordinal);
    }

    [Fact]
    public void DeployMarksTheCommandVariantsExecutableForTheirHarness()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("Kyber Utilities status-line artifacts are macOS and Linux only (D4).");

        AssertExitCode(0, Run("utilities", "statusline", "deploy"));

        foreach (string token in CommandVariantTokens)
        {
            string script = DeployedPath(token);
            Assert.True(
                HasExecutableBit(script),
                $"'{script}' is not executable, but the {token} harness runs it directly.");
        }
    }

    // -----------------------------------------------------------------------------------------
    // The settings guarantee: no verb names one, modifies one, or creates one (A4, C2, D5).
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("deploy")]
    [InlineData("status")]
    [InlineData("doctor")]
    [InlineData("remove")]
    public void EveryVerbNamesNoSettingsPathAndLeavesEveryConfirmedSettingsFileByteIdentical(string verb)
    {
        Dictionary<string, byte[]> sentinels = SeedConfirmedSettingsSentinels();

        CliResult deploy = Run("utilities", "statusline", "deploy");
        AssertExitCode(0, deploy);
        AssertNoSettingsPathAppears(deploy.Output);
        AssertSettingsSentinelsUnchanged(sentinels);

        CliResult run = Run("utilities", "statusline", verb);

        AssertNoSettingsPathAppears(run.Output);
        AssertSettingsSentinelsUnchanged(sentinels);
    }

    [Fact]
    public void NoVerbCreatesAConfirmedSettingsFileThatWasNotAlreadyThere()
    {
        foreach (string verb in Verbs)
        {
            CliResult run = Run("utilities", "statusline", verb);
            Assert.True(
                run.ExitCode is 0 or 1,
                $"'utilities statusline {verb}' exited {run.ExitCode}, which is neither a healthy " +
                $"result nor a reported issue: {run.Output}");
        }

        AssertNoSettingsFileExists();
    }

    // -----------------------------------------------------------------------------------------
    // Dry run: the guidance without the writes.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void DeployDryRunWritesNothingAndStillPrintsTheAbsoluteActivationPath()
    {
        Dictionary<string, byte[]> before = SnapshotHome();

        CliResult run = Run("utilities", "statusline", "deploy", "--dry-run");

        AssertExitCode(0, run);
        AssertHomeUnchanged(before);
        string output = Compact(run.Output);
        foreach (string token in HarnessTokens)
        {
            Assert.False(File.Exists(DeployedPath(token)), $"--dry-run wrote '{DeployedPath(token)}'.");
            Assert.Contains(Compact(DeployedPath(token)), output, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void DeployOutsideACheckoutExitsOneWithAHintRatherThanCrashing()
    {
        string notACheckout = Path.Combine(_temp.Path, "not-a-checkout");
        Directory.CreateDirectory(notACheckout);

        CliResult run = RunIn(notACheckout, _stubBin, "utilities", "statusline", "deploy");

        string output = Compact(run.Output);
        AssertExitCode(1, run);
        Assert.Contains(Compact(ProductRelativePath), output, StringComparison.Ordinal);
        Assert.DoesNotContain("unhandledexception", output, StringComparison.OrdinalIgnoreCase);
    }

    // -----------------------------------------------------------------------------------------
    // Status: ok, missing, or drift for every owned file, with the matching exit code.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void StatusExitsOneWhenNothingIsDeployed()
    {
        CliResult run = Run("utilities", "statusline", "status");

        AssertExitCode(1, run);
    }

    [Fact]
    public void StatusReportsEveryOwnedFileAsOkAndExitsZero()
    {
        AssertExitCode(0, Run("utilities", "statusline", "deploy"));

        CliResult run = Run("utilities", "statusline", "status");

        AssertExitCode(0, run);
        string output = Compact(run.Output);
        foreach (string token in HarnessTokens)
        {
            Assert.Contains(Compact(StagingRoot(token)), output, StringComparison.Ordinal);
            Assert.Contains(PrimaryFileName(token), output, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("missing", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("drift", output, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("drift")]
    public void StatusExitsOneAndNamesTheStateOfAnOwnedFileThatIsGoneOrEdited(string state)
    {
        AssertExitCode(0, Run("utilities", "statusline", "deploy"));
        string owned = DeployedPath("pi");
        if (string.Equals(state, "missing", StringComparison.Ordinal))
            File.Delete(owned);
        else
            File.WriteAllText(owned, EditedFileContent);

        CliResult run = Run("utilities", "statusline", "status");

        AssertExitCode(1, run);
        Assert.Contains(state, Compact(run.Output), StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------------------------
    // Doctor: owned files, the executable bit, prerequisites, and the optional recorder.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void DoctorExitsZeroForAHealthyDeploymentAndWarnsOnlyAboutTheOptionalRecorder()
    {
        AssertExitCode(0, Run("utilities", "statusline", "deploy"));

        CliResult run = Run("utilities", "statusline", "doctor");

        AssertExitCode(0, run);
        string output = Compact(run.Output);
        Assert.Contains("kyberdash", output, StringComparison.Ordinal);
        Assert.Contains("warn", output, StringComparison.Ordinal);
        foreach (string prerequisite in DeclaredPrerequisites)
            Assert.Contains(prerequisite, output, StringComparison.Ordinal);
        // Doctor prints the activation snippet too, so the operator can act on what it reports.
        foreach (string token in HarnessTokens)
            Assert.Contains(Compact(DeployedPath(token)), output, StringComparison.Ordinal);
    }

    [Fact]
    public void DoctorExitsOneAndNamesAPrerequisiteThatIsMissing()
    {
        AssertExitCode(0, Run("utilities", "statusline", "deploy"));

        CliResult run = RunIn(_checkout, StubBinWithout("jq"), "utilities", "statusline", "doctor");

        AssertExitCode(1, run);
        string output = Compact(run.Output);
        Assert.Contains("jq", output, StringComparison.Ordinal);
        Assert.Contains("fail", output, StringComparison.Ordinal);
    }

    [Fact]
    public void DoctorExitsOneWhenAnOwnedCommandScriptLostItsExecutableBit()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("Kyber Utilities status-line artifacts are macOS and Linux only (D4).");

        AssertExitCode(0, Run("utilities", "statusline", "deploy"));
        ClearExecutableBit(DeployedPath("claude"));

        CliResult run = Run("utilities", "statusline", "doctor");

        AssertExitCode(1, run);
        Assert.Contains("notexecutable", Compact(run.Output), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DoctorSkipsAHarnessThatIsNotInstalledInsteadOfFailingIt()
    {
        AssertExitCode(0, Run("utilities", "statusline", "deploy"));

        CliResult run = RunIn(
            _checkout,
            StubBinWithout("agy", "pi", "python3", "node"),
            "utilities",
            "statusline",
            "doctor");

        AssertExitCode(0, run);
        string output = Compact(run.Output);
        Assert.Contains("skip", output, StringComparison.Ordinal);
        Assert.Contains("notonPATH", output, StringComparison.OrdinalIgnoreCase);
    }

    // -----------------------------------------------------------------------------------------
    // Remove: only what the receipt owns, and only when it still holds the deployed bytes.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void RemoveDeletesEveryOwnedFileAndExitsZero()
    {
        AssertExitCode(0, Run("utilities", "statusline", "deploy"));

        CliResult run = Run("utilities", "statusline", "remove");

        AssertExitCode(0, run);
        foreach (string token in HarnessTokens)
            Assert.False(File.Exists(DeployedPath(token)),
                $"Remove left the owned file '{DeployedPath(token)}' behind.");
        AssertExitCode(1, Run("utilities", "statusline", "status"));
    }

    [Fact]
    public void RemoveWithNothingDeployedExitsZeroAndWritesNothing()
    {
        Dictionary<string, byte[]> before = SnapshotHome();

        CliResult run = Run("utilities", "statusline", "remove");

        AssertExitCode(0, run);
        AssertHomeUnchanged(before);
    }

    [Fact]
    public void RemoveExitsOneWhenAnOwnedFileCannotBeDeleted()
    {
        AssertExitCode(0, Run("utilities", "statusline", "deploy"));
        string stagingRoot = StagingRoot("pi");
        string probe = Path.Combine(stagingRoot, "deletion-probe.txt");
        File.WriteAllText(probe, "probe\n");

        if (!TryRemoveDeletePermission(stagingRoot, out UnixFileMode originalMode))
            Assert.Skip("Kyber Utilities status-line artifacts are macOS and Linux only (D4).");

        try
        {
            if (CanDeleteIn(stagingRoot, probe))
                Assert.Skip("A privileged process ignores the directory mode, so this case cannot be arranged.");

            CliResult run = Run("utilities", "statusline", "remove");

            AssertExitCode(1, run);
            Assert.True(
                File.Exists(DeployedPath("pi")),
                $"Remove exited {run.ExitCode} but the undeletable owned file is gone anyway.");
        }
        finally
        {
            RestoreDirectoryMode(stagingRoot, originalMode);
        }
    }

    // -----------------------------------------------------------------------------------------
    // Helpers: the fixture, the CLI child process, and the pinned paths.
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Writes a synthetic checkout: the solution marker the artifact locator requires beside the
    /// product tree, and one deployable artifact per harness (C3).
    /// </summary>
    private void WriteProductTree()
    {
        Directory.CreateDirectory(_checkout);
        File.WriteAllText(
            Path.Combine(_checkout, SolutionMarkerFileName),
            "Microsoft Visual Studio Solution File\n");
        foreach (string token in HarnessTokens)
        {
            string directory = Path.Combine(_checkout, ToNative(ProductRelativePath), ProductDirectory(token));
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, PrimaryFileName(token)), ArtifactContent(token));
        }
    }

    /// <summary>Writes one stub per executable, so doctor's probes answer without a real toolchain.</summary>
    private static void WriteStubExecutables(string directory, IEnumerable<string> executables)
    {
        Directory.CreateDirectory(directory);
        foreach (string executable in executables)
        {
            string path = Path.Combine(directory, executable);
            File.WriteAllText(path, $"#!/bin/sh\necho \"{executable} 9.9.9\"\n");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(
                    path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>A stub <c>PATH</c> holding every probe target except the named ones.</summary>
    private string StubBinWithout(params string[] omitted)
    {
        string directory = Path.Combine(_temp.Path, "stub-bin-without-" + string.Join('-', omitted));
        WriteStubExecutables(
            directory,
            StubExecutables.Where(candidate => !omitted.Contains(candidate, StringComparer.Ordinal)));
        return directory;
    }

    /// <summary>The CLI host this suite drives, copied beside the tests by the project reference.</summary>
    private static string CliHostPath()
    {
        string hostName = OperatingSystem.IsWindows() ? CliHostFileName + ".exe" : CliHostFileName;
        string host = Path.Combine(AppContext.BaseDirectory, hostName);
        Assert.True(
            File.Exists(host),
            $"The kyber-weave host '{host}' is missing from the test output directory. The test project " +
            "references the CLI project, so its absence is a build fault rather than the behaviour under test.");
        return host;
    }

    /// <summary>Runs the CLI from the synthetic checkout, where the artifacts are found.</summary>
    private CliResult Run(params string[] args)
    {
        return RunIn(_checkout, _stubBin, args);
    }

    /// <summary>
    /// Runs the CLI as a child process in the isolated home, with the given working directory and
    /// <c>PATH</c>, and captures its exit code and both output streams.
    /// </summary>
    private CliResult RunIn(string workingDirectory, string pathVariable, params string[] args)
    {
        ProcessStartInfo startInfo = new(CliHostPath())
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in args)
            startInfo.ArgumentList.Add(argument);

        startInfo.Environment["HOME"] = _home;
        startInfo.Environment["USERPROFILE"] = _home;
        startInfo.Environment[ClaudeConfigDirectoryVariable] = Path.Combine(_home, ".claude");
        startInfo.Environment[AgyConfigDirectoryVariable] = Path.Combine(_home, ".gemini", "antigravity-cli");
        startInfo.Environment[PiCodingAgentDirectoryVariable] = Path.Combine(_home, ".pi", "agent");
        startInfo.Environment["PATH"] = pathVariable;

        ProcessResult result = ProcessRunner.Run(startInfo, string.Empty, CliTimeout);
        return new CliResult(result.ExitCode, result.StandardOutput + "\n" + result.StandardError);
    }

    private string HarnessRoot(string token)
    {
        return Path.Combine(_home, ToNative(HarnessRootRelativePath(token)));
    }

    private string StagingRoot(string token)
    {
        return Path.Combine(HarnessRoot(token), ToNative(StagingSegment));
    }

    private string DeployedPath(string token)
    {
        return Path.Combine(StagingRoot(token), PrimaryFileName(token));
    }

    private static string HarnessRootRelativePath(string token)
    {
        return token switch
        {
            "claude" => ".claude",
            "agy" => ".gemini/antigravity-cli",
            "pi" => ".pi/agent",
            _ => throw new ArgumentOutOfRangeException(
                nameof(token),
                token,
                "No pinned status-line root exists for this harness token.")
        };
    }

    private static string ProductDirectory(string token)
    {
        return token switch
        {
            "claude" => "claude",
            "agy" => "antigravity",
            "pi" => "pi",
            _ => throw new ArgumentOutOfRangeException(
                nameof(token),
                token,
                "No status-line product directory exists for this harness token.")
        };
    }

    private static string PrimaryFileName(string token)
    {
        return token switch
        {
            "claude" => "statusline.sh",
            "agy" => "statusline.py",
            "pi" => "statusbar.ts",
            _ => throw new ArgumentOutOfRangeException(
                nameof(token),
                token,
                "No primary status-line artifact name exists for this harness token.")
        };
    }

    private static string ArtifactContent(string token)
    {
        return token switch
        {
            "claude" => "#!/bin/sh\necho synthetic-claude-statusline\n",
            "agy" => "#!/usr/bin/env python3\nprint(\"synthetic-agy-statusline\")\n",
            "pi" => "export default function activate() { /* synthetic pi status bar */ }\n",
            _ => throw new ArgumentOutOfRangeException(
                nameof(token),
                token,
                "No synthetic status-line artifact exists for this harness token.")
        };
    }

    private static string ToNative(string relativePath)
    {
        return relativePath.Replace('/', Path.DirectorySeparatorChar);
    }

    /// <summary>
    /// The text with every whitespace character removed, so a containment check survives Spectre's
    /// line wrapping.
    /// </summary>
    private static string Compact(string text)
    {
        StringBuilder builder = new(text.Length);
        foreach (char character in text)
        {
            if (!char.IsWhiteSpace(character))
                builder.Append(character);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Asserts the CLI's exit code, reporting the transcript when it differs. A bare integer
    /// comparison would say only that two numbers differ, which is not enough to tell a refusal from
    /// a crash — and the transcript is what makes a RED run legible as "Unknown command".
    /// </summary>
    private static void AssertExitCode(int expected, CliResult run)
    {
        Assert.True(
            expected == run.ExitCode,
            $"Expected exit code {expected}, got {run.ExitCode}. Transcript:\n{run.Output}");
    }

    /// <summary>Asserts no harness root was created, which a refused selection must not do.</summary>
    private void AssertNoHarnessRootExists()
    {
        foreach (string token in HarnessTokens)
            Assert.False(
                Directory.Exists(HarnessRoot(token)),
                $"'{HarnessRoot(token)}' was created for a target selection that was refused.");
    }

    /// <summary>Asserts no confirmed settings path appears in a command's output.</summary>
    private void AssertNoSettingsPathAppears(string output)
    {
        string compact = Compact(output);
        foreach (string relativePath in ConfirmedSettingsRelativePaths)
        {
            Assert.DoesNotContain(
                Compact(SettingsPath(relativePath)),
                compact,
                StringComparison.Ordinal);
            Assert.DoesNotContain(Compact(relativePath), compact, StringComparison.Ordinal);
        }
    }

    private string SettingsPath(string settingsRelativePath)
    {
        return Path.Combine(_home, ToNative(settingsRelativePath));
    }

    private Dictionary<string, byte[]> SeedConfirmedSettingsSentinels()
    {
        Dictionary<string, byte[]> sentinels = new(StringComparer.Ordinal);
        foreach (string relativePath in ConfirmedSettingsRelativePaths)
        {
            string settingsPath = SettingsPath(relativePath);
            Directory.CreateDirectory(
                Path.GetDirectoryName(settingsPath) ??
                throw new InvalidOperationException($"Settings path '{settingsPath}' has no parent directory."));
            byte[] sentinel = Encoding.UTF8.GetBytes(SettingsSentinelContent);
            File.WriteAllBytes(settingsPath, sentinel);
            sentinels[settingsPath] = sentinel;
        }

        return sentinels;
    }

    private static void AssertSettingsSentinelsUnchanged(IReadOnlyDictionary<string, byte[]> sentinels)
    {
        foreach ((string settingsPath, byte[] expected) in sentinels)
        {
            Assert.True(File.Exists(settingsPath), $"Settings sentinel '{settingsPath}' was deleted.");
            byte[] actual = File.ReadAllBytes(settingsPath);
            Assert.True(
                expected.AsSpan().SequenceEqual(actual),
                $"Settings file '{settingsPath}' was modified, which Kyber Utilities never does.");
        }
    }

    private void AssertNoSettingsFileExists()
    {
        foreach (string relativePath in ConfirmedSettingsRelativePaths)
        {
            string settingsPath = SettingsPath(relativePath);
            Assert.False(
                File.Exists(settingsPath),
                $"'{settingsPath}' was created, but Kyber Utilities never owns a settings file, not " +
                "even by create-if-missing.");
        }
    }

    private Dictionary<string, byte[]> SnapshotHome()
    {
        Dictionary<string, byte[]> snapshot = new(StringComparer.Ordinal);
        foreach (string file in EnumerateFilesSafely(_home))
            snapshot[file] = File.ReadAllBytes(file);
        return snapshot;
    }

    private void AssertHomeUnchanged(IReadOnlyDictionary<string, byte[]> before)
    {
        string[] expected = [.. before.Keys.Order(StringComparer.Ordinal)];
        string[] actual = [.. EnumerateFilesSafely(_home).Order(StringComparer.Ordinal)];
        Assert.True(
            expected.SequenceEqual(actual, StringComparer.Ordinal),
            $"The home directory holds [{Join(actual)}], not the [{Join(expected)}] it held before.");
        foreach ((string path, byte[] expectedBytes) in before)
        {
            byte[] actualBytes = File.ReadAllBytes(path);
            Assert.True(
                expectedBytes.AsSpan().SequenceEqual(actualBytes),
                $"'{path}' was rewritten by a command that must write nothing.");
        }
    }

    /// <summary>
    /// Every file beneath <paramref name="root"/>, walked one level at a time so a symlink cannot
    /// carry the walk outside the directory it started in (C# coding standard, "Safe filesystem
    /// enumeration").
    /// </summary>
    private static IEnumerable<string> EnumerateFilesSafely(string root)
    {
        if (!Directory.Exists(root))
            yield break;

        Stack<string> pending = new();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            foreach (FileSystemInfo entry in new DirectoryInfo(directory).EnumerateFileSystemInfos(
                         "*",
                         new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = 0 }))
            {
                if (entry.LinkTarget is not null)
                    continue;

                switch (entry)
                {
                    case DirectoryInfo child:
                        pending.Push(child.FullName);
                        break;
                    case FileInfo file:
                        yield return file.FullName;
                        break;
                }
            }
        }
    }

    /// <summary>
    /// Whether this process can delete a file inside a directory that carries no write bit. A
    /// privileged process can, and then the undeletable-file case cannot be arranged at all.
    /// </summary>
    private static bool CanDeleteIn(string directory, string fileName)
    {
        string candidate = Path.Combine(directory, fileName);
        try
        {
            File.Delete(candidate);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether a deployed file carries the user-execute bit. Answers <see langword="true"/> on a
    /// platform with no such bit, so the check is skipped rather than failed there (D4).
    /// </summary>
    private static bool HasExecutableBit(string path)
    {
        if (OperatingSystem.IsWindows())
            return true;

        return (File.GetUnixFileMode(path) & UnixFileMode.UserExecute) != 0;
    }

    /// <summary>Drops a deployed file's execute bits, as a locally edited script would.</summary>
    private static void ClearExecutableBit(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        File.SetUnixFileMode(
            path,
            File.GetUnixFileMode(path) &
            ~(UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute));
    }

    /// <summary>
    /// Clears the write bits from a directory so nothing in it can be deleted. Answers
    /// <see langword="false"/> on a platform with no such bit, leaving the directory alone; the caller
    /// restores the mode with <see cref="RestoreDirectoryMode"/> after the assertion runs.
    /// </summary>
    private static bool TryRemoveDeletePermission(string directory, out UnixFileMode originalMode)
    {
        originalMode = default;
        if (OperatingSystem.IsWindows())
            return false;

        originalMode = File.GetUnixFileMode(directory);
        File.SetUnixFileMode(
            directory,
            originalMode & ~(UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite));
        return true;
    }

    /// <summary>Puts a directory's original mode back, so the temporary tree can be disposed.</summary>
    private static void RestoreDirectoryMode(string directory, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows())
            return;

        File.SetUnixFileMode(directory, mode);
    }

    /// <summary>
    /// Asserts the bytes at <paramref name="path"/> are exactly <paramref name="expected"/>, reporting
    /// both lengths rather than letting a read throw when the deployment wrote nothing.
    /// </summary>
    private static void AssertFileBytes(string path, string expected)
    {
        byte[] expectedBytes = Encoding.UTF8.GetBytes(expected);
        byte[] actual = File.Exists(path) ? File.ReadAllBytes(path) : [];
        Assert.True(
            expectedBytes.AsSpan().SequenceEqual(actual),
            $"'{path}' holds {actual.Length} byte(s), not the {expectedBytes.Length} byte(s) the " +
            "deployment must have written there.");
    }

    private static string Join(IEnumerable<string> values)
    {
        return string.Join(", ", values.Select(value => $"'{value}'"));
    }

    /// <summary>One CLI invocation: its exit code, and stdout and stderr as one rendered transcript.</summary>
    private readonly record struct CliResult(int ExitCode, string Output);
}
