using KyberWeave.Cli.Commands.Utilities;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Processes;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Domain.StatusLine;
using KyberWeave.Tests.Fakes;
using Spectre.Console.Testing;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The four <c>utilities statusline</c> verbs write every line to the console they are given and take every fact about
/// the deployment, the <c>PATH</c> and the artifact tree from a seam, so a unit test states the operator's world in
/// memory and asserts what they read without a temporary directory or a captured process console.
/// </summary>
/// <remarks>
/// What each case proves is the seam and the command's own decision: whether a missing file fails the run, whether a
/// dry run applies nothing, and that a refusal lands on the same console as the rest of the output. The exit codes are
/// the contract the integration suite pins; these cases pin that the same codes follow from the seams alone.
/// </remarks>
public sealed class UtilitiesStatusLineCommandSeamTests
{
    /// <summary>A fully qualified anchor the path seam accepts on every platform.</summary>
    private static readonly string Anchor = OperatingSystem.IsWindows() ? @"C:\kw" : "/kw";

    private static string Home => Anchor + "/home";

    private static string Work => Anchor + "/work";

    private static string ClaudeStagingRoot => Path.Combine(Home, ".claude", "kyber", "statusline");

    private readonly TestConsole _console = new() { EmitAnsiSequences = false };
    private readonly FakeStatusLineDeploymentService _plans = new();
    private readonly FakeStatusLineHostService _host = new();
    private readonly IFileSystemPathSemantics _semantics =
        new SquadFileSystemPathSemantics(FakePathResolver.OverEmptyFileSystem());

    private static UtilitiesStatusLineProcessRunner UnreachableProbe => (_, _) =>
        throw new InvalidOperationException("This case must not probe a harness.");

    // -----------------------------------------------------------------------------------------
    // status
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// A receipt-owned file that no longer matches its digest fails the run, and the line naming it reaches the
    /// injected console rather than the process-wide one.
    /// </summary>
    [Fact]
    public void StatusReportsADriftedFileOnTheInjectedConsole()
    {
        _plans.WithReceipt(StatusLineTarget.Claude, Owned("statusline/statusline.sh"));
        _host.FileStates["statusline.sh"] = UtilitiesStatusLineFileState.Drift;

        int result = Status().Execute(null!, new UtilitiesStatusLineSettings());

        Assert.Equal(1, result);
        Assert.Contains("drift", _console.Output, StringComparison.Ordinal);
        Assert.Contains("statusline/statusline.sh", _console.Output, StringComparison.Ordinal);
        Assert.StartsWith(ClaudeStagingRoot, Assert.Single(_host.EvaluatedFiles), StringComparison.Ordinal);
    }

    /// <summary>
    /// The digest the receipt records is the one the command asks the host to compare against. A host answer scripted
    /// independently of the receipt must not hide a command that drops or truncates the digest.
    /// </summary>
    [Fact]
    public void StatusPassesTheReceiptsDigestToTheHost()
    {
        _plans.WithReceipt(StatusLineTarget.Claude, Owned("statusline/statusline.sh"));

        _ = Status().Execute(null!, new UtilitiesStatusLineSettings());

        (_, string expectedSha256) = Assert.Single(_host.Evaluations);
        Assert.Equal("0123456789abcdef", expectedSha256);
    }

    /// <summary>
    /// Two owned files with the same name in different directories are distinct. A fake keyed by file name alone would
    /// report the wrong one and pass a command that lost the directory part of the path.
    /// </summary>
    [Fact]
    public void StatusTellsApartTwoOwnedFilesWithTheSameNameInDifferentDirectories()
    {
        _plans.WithReceipt(
            StatusLineTarget.Claude,
            Owned("a/statusline.sh"),
            Owned("b/statusline.sh"));
        _host.FileStates["a/statusline.sh"] = UtilitiesStatusLineFileState.Missing;

        int result = Status().Execute(null!, new UtilitiesStatusLineSettings());

        Assert.Equal(1, result);
        Assert.Contains("missing a/statusline.sh", _console.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("missing b/statusline.sh", _console.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// When the fake is given the file's bytes it compares their digest with the receipt, so a command that passes the
    /// wrong digest is reported as drift rather than as healthy.
    /// </summary>
    [Fact]
    public void StatusReportsDriftWhenTheBytesDoNotMatchTheReceipt()
    {
        _plans.WithReceipt(StatusLineTarget.Claude, Owned("statusline/statusline.sh"));
        _host.Contents[Path.Combine(ClaudeStagingRoot, "statusline", "statusline.sh")] =
            "locally edited by the operator\n"u8.ToArray();

        int result = Status().Execute(null!, new UtilitiesStatusLineSettings());

        Assert.Equal(1, result);
        Assert.Contains("drift", _console.Output, StringComparison.Ordinal);
    }

    /// <summary>No receipt at all names the deploy command and fails the run.</summary>
    [Fact]
    public void StatusWithNoReceiptNamesTheDeployCommand()
    {
        int result = Status().Execute(null!, new UtilitiesStatusLineSettings());

        Assert.Equal(1, result);
        Assert.Contains("No Kyber Utilities status-line deployment found.", _console.Output, StringComparison.Ordinal);
        Assert.Contains("kyber-weave utilities statusline deploy", _console.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A home directory that is not fully qualified is client input. Status and doctor refuse it with exit 2 on the
    /// injected console, the same line deploy and remove already print, instead of throwing out of the command.
    /// </summary>
    [Theory]
    [InlineData("status")]
    [InlineData("doctor")]
    public void AnUnusableHomeIsClientInputWithExitTwo(string verb)
    {
        FakeProcessEnvironment environment = new(currentDirectory: Work, homeDirectory: "relative-home");

        int result = verb == "status"
            ? new UtilitiesStatusLineStatusCommand(environment, _plans, _host, _semantics, _console)
                .Execute(null!, new UtilitiesStatusLineSettings())
            : new UtilitiesStatusLineDoctorCommand(environment, _plans, _host, UnreachableProbe, _semantics, _console)
                .Execute(null!, new UtilitiesStatusLineSettings());

        Assert.Equal(2, result);
        Assert.Contains("kyber-weave utilities statusline: error:", _console.Output, StringComparison.Ordinal);
    }

    /// <summary>A relative harness-root override is refused the same way, because the command checks every root first.</summary>
    [Fact]
    public void StatusRefusesARelativeHarnessRootOverrideWithExitTwo()
    {
        FakeProcessEnvironment environment = new(
            currentDirectory: Work,
            homeDirectory: Home,
            readVariable: name => name == "CLAUDE_CONFIG_DIR" ? "relative-override" : null);

        int result = new UtilitiesStatusLineStatusCommand(environment, _plans, _host, _semantics, _console)
            .Execute(null!, new UtilitiesStatusLineSettings());

        Assert.Equal(2, result);
        Assert.Contains("kyber-weave utilities statusline: error:", _console.Output, StringComparison.Ordinal);
    }

    /// <summary>A malformed receipt is reported as invalid rather than thrown out of the command.</summary>
    [Fact]
    public void StatusReportsAMalformedReceiptAsInvalid()
    {
        _plans.WithReadFailure(StatusLineTarget.Claude, new InvalidDataException("receipt is malformed"));

        int result = Status().Execute(null!, new UtilitiesStatusLineSettings());

        Assert.Equal(1, result);
        Assert.Contains("invalid", _console.Output, StringComparison.Ordinal);
        Assert.Contains("receipt is malformed", _console.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// An unreadable receipt is the same operator-facing outcome as a malformed one. The read path is a file seam that
    /// throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/>, and neither may escape the
    /// command as a crash.
    /// </summary>
    [Theory]
    [InlineData("io")]
    [InlineData("denied")]
    public void StatusReportsAnUnreadableReceiptAsInvalid(string failureKind)
    {
        _plans.WithReadFailure(StatusLineTarget.Claude, ReadFailure(failureKind));

        int result = Status().Execute(null!, new UtilitiesStatusLineSettings());

        Assert.Equal(1, result);
        Assert.Contains("invalid", _console.Output, StringComparison.Ordinal);
        Assert.Contains(ReadFailure(failureKind).Message, _console.Output, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------------------------
    // remove
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// The plan the deployment service builds is applied once, and the report separates what was removed from what was
    /// kept because it had been edited locally.
    /// </summary>
    [Fact]
    public void RemoveAppliesThePlanAndReportsTheRemovedAndKeptFiles()
    {
        _plans
            .WithReceipt(StatusLineTarget.Claude, Owned("statusline/statusline.sh"))
            .WithRemoval(
                StatusLineTarget.Claude,
                ["statusline/statusline.sh", "statusline/manifest.json"],
                Owned("statusline/local.sh"));

        int result = Remove().Execute(null!, new UtilitiesStatusLineSettings());

        Assert.Equal(0, result);
        Assert.Single(_plans.Applied);
        Assert.Contains("removed 2 file(s)", _console.Output, StringComparison.Ordinal);
        Assert.Contains("kept", _console.Output, StringComparison.Ordinal);
        Assert.Contains("statusline/local.sh", _console.Output, StringComparison.Ordinal);
    }

    /// <summary>Nothing deployed is not a failure: remove says so and exits zero.</summary>
    [Fact]
    public void RemoveWithNoReceiptSaysNothingToRemove()
    {
        int result = Remove().Execute(null!, new UtilitiesStatusLineSettings());

        Assert.Equal(0, result);
        Assert.Empty(_plans.Applied);
        Assert.Contains("Nothing to remove.", _console.Output, StringComparison.Ordinal);
    }

    /// <summary>A file system refusal while applying is reported and fails the run.</summary>
    [Fact]
    public void RemoveReportsAnApplyFailure()
    {
        _plans.WithReceipt(StatusLineTarget.Claude, Owned("statusline/statusline.sh"));
        _plans.ApplyFailure = new IOException("permission denied");

        int result = Remove().Execute(null!, new UtilitiesStatusLineSettings());

        Assert.Equal(1, result);
        Assert.Contains("permission denied", _console.Output, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------------------------
    // deploy
    // -----------------------------------------------------------------------------------------

    /// <summary>A dry run reports the plan and applies nothing, not even the executable bit.</summary>
    [Fact]
    public void DeployDryRunAppliesNothing()
    {
        _host.Artifacts.With(StatusLineTarget.Claude, Artifact("statusline/statusline.sh"));

        int result = Deploy().Execute(
            null!,
            new UtilitiesStatusLineDeploySettings { Target = "claude", DryRun = true });

        Assert.Equal(0, result);
        Assert.Empty(_plans.Applied);
        Assert.Empty(_host.ExecutableBitApplications);
        Assert.Contains("dry-run", _console.Output, StringComparison.Ordinal);
        Assert.Contains("would stage 1 file(s)", _console.Output, StringComparison.Ordinal);
    }

    /// <summary>A real deploy applies the plan once and asks the host to mark the command executable.</summary>
    [Fact]
    public void DeployAppliesThePlanAndMarksTheCommandExecutable()
    {
        _host.Artifacts.With(StatusLineTarget.Claude, Artifact("statusline/statusline.sh"));

        int result = Deploy().Execute(null!, new UtilitiesStatusLineDeploySettings { Target = "claude" });

        Assert.Equal(0, result);
        Assert.Single(_plans.Applied);
        Assert.Single(_host.ExecutableBitApplications);
        Assert.Contains("staged 1 file(s)", _console.Output, StringComparison.Ordinal);
        Assert.Contains("Apply this snippet yourself.", _console.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A deploy records the digest of the bytes it staged, and status reads that digest back. The artifact source, the
    /// deployment service and the host are all fakes, so this pins the round trip the two commands share.
    /// </summary>
    [Fact]
    public void ADeployedArtifactReadsBackHealthyOnStatus()
    {
        byte[] content = "#!/bin/sh\necho synthetic\n"u8.ToArray();
        _host.Artifacts.With(StatusLineTarget.Claude, new StatusLineDeploymentFile("statusline/statusline.sh", content));

        Assert.Equal(0, Deploy().Execute(null!, new UtilitiesStatusLineDeploySettings { Target = "claude" }));

        StatusLineReceipt receipt = Assert.Single(_plans.Applied).Receipt;
        _plans.WithReceipt(StatusLineTarget.Claude, [.. receipt.Files]);
        _host.Contents[Path.Combine(ClaudeStagingRoot, "statusline", "statusline.sh")] = content;

        int result = Status().Execute(null!, new UtilitiesStatusLineSettings());

        Assert.Equal(0, result);
        Assert.Contains("ok statusline/statusline.sh", _console.Output, StringComparison.Ordinal);
    }

    /// <summary>An unknown target is client input, refused with exit 2 on the command's own console.</summary>
    [Fact]
    public void DeployRefusesAnUnknownTargetWithExitTwo()
    {
        int result = Deploy().Execute(
            null!,
            new UtilitiesStatusLineDeploySettings { Target = "opencode" });

        Assert.Equal(2, result);
        Assert.Empty(_plans.DeployRequests);
        Assert.Contains("kyber-weave utilities statusline: error:", _console.Output, StringComparison.Ordinal);
        Assert.Contains("opencode", _console.Output, StringComparison.Ordinal);
    }

    /// <summary>A checkout without the artifact tree is reported as an error rather than an unhandled exception.</summary>
    [Fact]
    public void DeployReportsAMissingArtifactTree()
    {
        _host.ArtifactSourceFailure = new DirectoryNotFoundException("the artifact tree is missing");

        int result = Deploy().Execute(null!, new UtilitiesStatusLineDeploySettings { Target = "claude" });

        Assert.Equal(1, result);
        Assert.Contains("the artifact tree is missing", _console.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The activation path is resolved through the path policy, which refuses a traversal. An artifact set that names
    /// one is reported like any other deploy error, not thrown out of the command.
    /// </summary>
    [Fact]
    public void DeployReportsAnArtifactThatResolvesOutsideTheStagingRoot()
    {
        _host.Artifacts.With(StatusLineTarget.Claude, Artifact("../statusline.sh"));

        int result = Deploy().Execute(null!, new UtilitiesStatusLineDeploySettings { Target = "claude" });

        Assert.Equal(1, result);
        Assert.Contains("kyber-weave utilities statusline: error:", _console.Output, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------------------------
    // doctor
    // -----------------------------------------------------------------------------------------

    /// <summary>A harness that is not installed is skipped, and no version probe runs for it.</summary>
    [Fact]
    public void DoctorSkipsHarnessesThatAreNotOnPath()
    {
        int result = Doctor(UnreachableProbe).Execute(null!, new UtilitiesStatusLineSettings());

        Assert.Equal(0, result);
        Assert.Contains("skip", _console.Output, StringComparison.Ordinal);
        Assert.Contains("is not on PATH", _console.Output, StringComparison.Ordinal);
        Assert.Equal(UtilitiesStatusLineTargetCatalog.All.Count, _host.PathProbes.Count);
    }

    /// <summary>An installed harness reports its version, its prerequisites and the activation snippet.</summary>
    [Fact]
    public void DoctorReportsAnInstalledHarnessAndItsVersion()
    {
        InstallClaude();
        _plans.WithReceipt(StatusLineTarget.Claude, Owned("statusline/statusline.sh"));

        int result = Doctor((_, _) => new ProcessResult(0, "1.2.3\n", string.Empty))
            .Execute(null!, new UtilitiesStatusLineSettings());

        Assert.Equal(0, result);
        Assert.Contains("(1.2.3)", _console.Output, StringComparison.Ordinal);
        Assert.Contains("prerequisite 'jq'", _console.Output, StringComparison.Ordinal);
        Assert.Contains("Activation — claude", _console.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// An installed harness with nothing deployed is told to deploy. The activation snippet names a file the deploy
    /// would create, so printing it with "not deployed" in the same run would point the operator at an absent path.
    /// </summary>
    [Fact]
    public void DoctorWithNoDeploymentPrintsNoActivationSnippet()
    {
        InstallClaude();

        int result = Doctor((_, _) => new ProcessResult(0, "1.2.3\n", string.Empty))
            .Execute(null!, new UtilitiesStatusLineSettings());

        Assert.Equal(0, result);
        Assert.Contains("not deployed", _console.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Activation — claude", _console.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Apply this snippet yourself.", _console.Output, StringComparison.Ordinal);
    }

    /// <summary>A missing prerequisite fails the run with a remedy, while the other checks still report.</summary>
    [Fact]
    public void DoctorFailsOnAMissingPrerequisite()
    {
        InstallClaude(without: "jq");

        int result = Doctor((_, _) => new ProcessResult(0, "1.2.3\n", string.Empty))
            .Execute(null!, new UtilitiesStatusLineSettings());

        Assert.Equal(1, result);
        Assert.Contains("fail", _console.Output, StringComparison.Ordinal);
        Assert.Contains("prerequisite 'jq' is missing", _console.Output, StringComparison.Ordinal);
    }

    /// <summary>A command variant without its executable bit fails the run.</summary>
    [Fact]
    public void DoctorFailsOnAnOwnedFileWithoutTheExecutableBit()
    {
        InstallClaude();
        _plans.WithReceipt(StatusLineTarget.Claude, Owned("statusline/statusline.sh"));
        _host.FilesWithoutExecutableBit.Add("statusline.sh");

        int result = Doctor((_, _) => new ProcessResult(0, "1.2.3\n", string.Empty))
            .Execute(null!, new UtilitiesStatusLineSettings());

        Assert.Equal(1, result);
        Assert.Contains("is not executable", _console.Output, StringComparison.Ordinal);
    }

    /// <summary>An unreadable receipt is reported as a failure for that harness, not thrown out of doctor.</summary>
    [Theory]
    [InlineData("io")]
    [InlineData("denied")]
    public void DoctorReportsAnUnreadableReceiptAsAFailure(string failureKind)
    {
        InstallClaude();
        _plans.WithReadFailure(StatusLineTarget.Claude, ReadFailure(failureKind));

        int result = Doctor((_, _) => new ProcessResult(0, "1.2.3\n", string.Empty))
            .Execute(null!, new UtilitiesStatusLineSettings());

        Assert.Equal(1, result);
        Assert.Contains("fail", _console.Output, StringComparison.Ordinal);
        Assert.Contains(ReadFailure(failureKind).Message, _console.Output, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------------------------
    // constructor boundaries
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// A command resolves every owned path through the path semantics, so a missing one is a programming error at the
    /// constructor boundary rather than a null dereference deep inside the resolution.
    /// </summary>
    [Theory]
    [InlineData("status")]
    [InlineData("deploy")]
    [InlineData("doctor")]
    public void ACommandRefusesANullPathSemantics(string verb)
    {
        FakeProcessEnvironment environment = new(currentDirectory: Work, homeDirectory: Home);

        Action construct = verb switch
        {
            "status" => () => _ = new UtilitiesStatusLineStatusCommand(environment, _plans, _host, null!, _console),
            "deploy" => () => _ = new UtilitiesStatusLineDeployCommand(environment, _host, _plans, null!, _console),
            "doctor" => () => _ = new UtilitiesStatusLineDoctorCommand(
                environment, _plans, _host, UnreachableProbe, null!, _console),
            _ => throw new ArgumentOutOfRangeException(nameof(verb), verb, "No status-line command is defined.")
        };

        Assert.Throws<ArgumentNullException>(construct);
    }

    // -----------------------------------------------------------------------------------------
    // shared rendering
    // -----------------------------------------------------------------------------------------

    /// <summary>The activation snippet is written to the console the caller passes, line by line.</summary>
    [Fact]
    public void ActivationGuidanceWritesToTheGivenConsole()
    {
        string commandPath = Path.Combine(ClaudeStagingRoot, "statusline.sh");

        UtilitiesStatusLineOutput.PrintActivationGuidance(_console, StatusLineTarget.Claude, commandPath);

        Assert.Contains("Activation — claude", _console.Output, StringComparison.Ordinal);
        Assert.Contains(commandPath, _console.Output, StringComparison.Ordinal);
        Assert.Contains("Apply this snippet yourself.", _console.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The activation path is resolved through the path policy. A containment answer that refuses it is reported as a
    /// failure for that harness, not thrown out of doctor.
    /// </summary>
    [Fact]
    public void DoctorReportsAnActivationPathThePolicyRefuses()
    {
        InstallClaude();
        _plans.WithReceipt(StatusLineTarget.Claude, Owned("statusline/statusline.sh"));

        int result = DoctorOver(
                (_, _) => new ProcessResult(0, "1.2.3\n", string.Empty),
                new RefusingPathSemantics())
            .Execute(null!, new UtilitiesStatusLineSettings());

        Assert.Equal(1, result);
        Assert.Contains("fail", _console.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Activation — claude", _console.Output, StringComparison.Ordinal);
    }

    private UtilitiesStatusLineStatusCommand Status() =>
        new(new FakeProcessEnvironment(currentDirectory: Work, homeDirectory: Home), _plans, _host, _semantics, _console);

    private UtilitiesStatusLineRemoveCommand Remove() =>
        new(new FakeProcessEnvironment(currentDirectory: Work, homeDirectory: Home), _plans, _console);

    private UtilitiesStatusLineDeployCommand Deploy() =>
        new(new FakeProcessEnvironment(currentDirectory: Work, homeDirectory: Home), _host, _plans, _semantics, _console);

    private UtilitiesStatusLineDoctorCommand Doctor(UtilitiesStatusLineProcessRunner probe) =>
        new(new FakeProcessEnvironment(currentDirectory: Work, homeDirectory: Home), _plans, _host, probe, _semantics, _console);

    private UtilitiesStatusLineDoctorCommand DoctorOver(
        UtilitiesStatusLineProcessRunner probe,
        IFileSystemPathSemantics semantics) =>
        new(new FakeProcessEnvironment(currentDirectory: Work, homeDirectory: Home), _plans, _host, probe, semantics, _console);

    /// <summary>Puts Claude's harness binary and prerequisites on the fake <c>PATH</c>, optionally leaving one out.</summary>
    private void InstallClaude(string? without = null)
    {
        foreach (string executable in (string[])["claude", "bash", "jq", "git", "awk"])
            if (!string.Equals(executable, without, StringComparison.Ordinal))
                _ = _host.Executables.Add(executable);
    }

    private static StatusLineOwnedFile Owned(string relativePath) =>
        new(relativePath, "0123456789abcdef", StatusLineTarget.Claude);

    private static StatusLineDeploymentFile Artifact(string relativePath) =>
        new(relativePath, "#!/bin/sh\necho synthetic\n"u8.ToArray());

    /// <summary>A receipt read failure of the kind a file seam throws, named so a theory can state both.</summary>
    private static Exception ReadFailure(string kind) => kind switch
    {
        "io" => new IOException("the receipt is on a broken disk"),
        "denied" => new UnauthorizedAccessException("permission denied"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "No receipt read failure is defined.")
    };

    /// <summary>The path seam answering that nothing is contained, so a resolution is refused.</summary>
    private sealed class RefusingPathSemantics : IFileSystemPathSemantics
    {
        public bool AreSame(string first, string second) => false;

        public bool IsWithin(string root, string candidate) => false;

        public string Canonicalize(string path) => path;

        public string ResolvePhysicalRoot(string targetRoot) => targetRoot;
    }
}
