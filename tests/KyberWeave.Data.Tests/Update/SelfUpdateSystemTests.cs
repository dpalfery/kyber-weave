using System.Diagnostics;
using System.Formats.Tar;
using KyberWeave.Cli.Update;
using KyberWeave.Core.Processes;
using KyberWeave.Testing.Update;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The self-update system over its seams: archive extraction, binary replacement, the probe and the process bounds,
/// all on in-memory fakes. Nothing here reads the disk or starts a program.
/// </summary>
public sealed class SelfUpdateSystemTests
{
    private static readonly string Root = new InMemoryFileSystem().Root;
    private static readonly string Kw = Path.Combine(Root, "kw");
    private static readonly string Work = Path.Combine(Kw, "work");
    private static readonly string Install = Path.Combine(Kw, "install");
    private static readonly string Outside = Path.Combine(Kw, "outside");
    private static readonly string Destination = Path.Combine(Install, "kyber-weave");

    private readonly InMemoryFileSystem _files = new(caseSensitive: true);
    private readonly FakeUpdateFileModes _modes = new();
    private readonly FakeConsoleProcessLauncher _console = new();
    private readonly ScriptedProcessExecutor _processes = new(_ => new ProcessResult(0, "out", string.Empty));
    private readonly SelfUpdateSystem _system;

    public SelfUpdateSystemTests()
    {
        _system = new SelfUpdateSystem(
            _files,
            new FakePathResolver(_files),
            _processes,
            _console,
            _modes);
        _files.CreateDirectory(Work);
        _files.CreateDirectory(Install);
    }

    [Fact]
    public void ATarArchiveExtractsItsPlainFilesWithTheirPermissionBits()
    {
        string archive = Stage("release.tar.gz", ReleaseArchiveBuilder.TarGz(
            new ReleaseArchiveEntry("bin/", Type: TarEntryType.Directory),
            new ReleaseArchiveEntry("bin/kyber-weave", "new image")));

        _system.ExtractArchive(archive, Path.Combine(Work, "extract"), windows: false);

        Assert.Equal("new image", _files.ReadAllText(Path.Combine(Work, "extract", "bin", "kyber-weave")));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute,
            _modes.Modes[Path.Combine(Work, "extract", "bin", "kyber-weave")]);
    }

    [Fact]
    public void AZipArchiveExtractsItsFilesOverWhatIsThere()
    {
        _files.CreateDirectory(Path.Combine(Work, "extract"));
        _files.WriteAllText(Path.Combine(Work, "extract", "kyber-weave.exe"), "stale");
        string archive = Stage("release.zip", ReleaseArchiveBuilder.Zip(
            new ReleaseArchiveEntry("kyber-weave.exe", "new image"),
            new ReleaseArchiveEntry("docs/", Type: TarEntryType.Directory),
            new ReleaseArchiveEntry("docs/notice.txt", "notice")));

        _system.ExtractArchive(archive, Path.Combine(Work, "extract"), windows: true);

        Assert.Equal("new image", _files.ReadAllText(Path.Combine(Work, "extract", "kyber-weave.exe")));
        Assert.Equal("notice", _files.ReadAllText(Path.Combine(Work, "extract", "docs", "notice.txt")));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("nested/../../escape")]
    [InlineData("<root>kw/absolute-escape")]
    [InlineData("..\\escape")]
    [InlineData("C:/escape")]
    public void AnEntryThatEscapesTheDestinationIsRefusedAndNothingIsWrittenOutside(string name)
    {
        string archive = Stage("hostile.tar.gz", ReleaseArchiveBuilder.TarGz(
            new ReleaseArchiveEntry("kyber-weave", "fine"),
            new ReleaseArchiveEntry(name.Replace("<root>", Root, StringComparison.Ordinal), "pwned")));

        SelfUpdateException refusal = Assert.Throws<SelfUpdateException>(
            () => _system.ExtractArchive(archive, Path.Combine(Work, "extract"), windows: false));

        Assert.Contains("was refused", refusal.Message, StringComparison.Ordinal);
        Assert.False(_files.PathExists(Path.Combine(Kw, "escape")));
        Assert.False(_files.PathExists(Path.Combine(Kw, "absolute-escape")));
        Assert.False(_files.PathExists(Path.Combine(Work, "escape")));
        Assert.Equal(
            [Path.Combine(Work, "extract", "kyber-weave")],
            _files.EnumerateFiles(Kw, "*", recursive: true).Where(path => !path.EndsWith(".tar.gz", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("../escape.exe")]
    [InlineData("\\absolute.exe")]
    [InlineData("C:\\escape.exe")]
    public void AZipEntryThatEscapesTheDestinationIsRefused(string name)
    {
        string archive = Stage("hostile.zip", ReleaseArchiveBuilder.Zip(new ReleaseArchiveEntry(name, "pwned")));

        _ = Assert.Throws<SelfUpdateException>(() => _system.ExtractArchive(archive, Path.Combine(Work, "extract"), windows: true));

        Assert.Equal([archive], _files.EnumerateFiles(Kw, "*", recursive: true));
    }

    [Theory]
    [InlineData(TarEntryType.SymbolicLink)]
    [InlineData(TarEntryType.HardLink)]
    public void ALinkEntryIsRefusedBecauseAReleaseHoldsPlainFilesOnly(TarEntryType type)
    {
        string archive = Stage("links.tar.gz", ReleaseArchiveBuilder.TarGz(
            new ReleaseArchiveEntry("kyber-weave", Type: type, LinkTarget: "/etc/passwd")));

        SelfUpdateException refusal = Assert.Throws<SelfUpdateException>(
            () => _system.ExtractArchive(archive, Path.Combine(Work, "extract"), windows: false));

        Assert.Contains("plain files only", refusal.Message, StringComparison.Ordinal);
        Assert.Empty(_files.EnumerateEntries(Path.Combine(Work, "extract")));
    }

    [Fact]
    public void ANestedRelativeEntryIsNotMistakenForAnEscape()
    {
        string archive = Stage("nested.tar.gz", ReleaseArchiveBuilder.TarGz(
            new ReleaseArchiveEntry("a/b/c/kyber-weave", "deep")));

        _system.ExtractArchive(archive, Path.Combine(Work, "extract"), windows: false);

        Assert.Equal("deep", _files.ReadAllText(Path.Combine(Work, "extract", "a", "b", "c", "kyber-weave")));
    }

    [Fact]
    public void AnEntryNamedThroughALinkedDirectoryIsRefusedWhenTheLinkLeavesTheDestination()
    {
        _files.CreateDirectory(Outside);
        _files.CreateDirectory(Path.Combine(Work, "extract"));
        _files.CreateDirectoryLink(Path.Combine(Work, "extract", "linked"), Outside);
        string archive = Stage("through-link.tar.gz", ReleaseArchiveBuilder.TarGz(
            new ReleaseArchiveEntry("linked/kyber-weave", "pwned")));

        _ = Assert.Throws<SelfUpdateException>(() => _system.ExtractArchive(archive, Path.Combine(Work, "extract"), windows: false));

        Assert.Empty(_files.EnumerateFiles(Outside, "*", recursive: true));
    }

    [Fact]
    public void ReplacingABinaryLeavesTheNewImageAndNoStagingFile()
    {
        _files.WriteAllText(Destination, "old");
        _files.WriteAllText(Path.Combine(Work, "kyber-weave"), "new");

        _system.ReplaceBinary(Path.Combine(Work, "kyber-weave"), Destination, windows: false, macOs: false);

        Assert.Equal("new", _files.ReadAllText(Destination));
        Assert.Equal([Destination], _files.EnumerateFiles(Install, "*", recursive: true));
        Assert.Empty(_processes.Calls);
        Assert.True(_modes.Modes[Path.Combine(Install, ".kyber-weave.new")].HasFlag(UnixFileMode.UserExecute));
    }

    [Fact]
    public void ReplacingOnWindowsKeepsNoExecutableBitAndLeavesNoOldImage()
    {
        _files.WriteAllText(Destination, "old");
        _files.WriteAllText(Path.Combine(Work, "kyber-weave"), "new");

        _system.ReplaceBinary(Path.Combine(Work, "kyber-weave"), Destination, windows: true, macOs: false);

        Assert.Equal("new", _files.ReadAllText(Destination));
        Assert.Equal([Destination], _files.EnumerateFiles(Install, "*", recursive: true));
        Assert.Empty(_modes.Modes);
    }

    [Fact]
    public void AFailedMoveOfTheNewImageOnWindowsPutsTheOldImageBack()
    {
        _files.WriteAllText(Destination, "old");
        _files.WriteAllText(Path.Combine(Work, "kyber-weave"), "new");
        _files.FailWhen(
            operation => operation.Name == nameof(_files.MoveFile) && operation.Path?.EndsWith(".new", StringComparison.Ordinal) == true,
            new IOException("locked"));

        _ = Assert.Throws<IOException>(
            () => _system.ReplaceBinary(Path.Combine(Work, "kyber-weave"), Destination, windows: true, macOs: false));

        Assert.Equal("old", _files.ReadAllText(Destination));
        Assert.Equal([Destination], _files.EnumerateFiles(Install, "*", recursive: true));
    }

    [Fact]
    public void AFailedMoveOffTheWindowsPathLeavesNoStagingFileEither()
    {
        _files.WriteAllText(Destination, "old");
        _files.WriteAllText(Path.Combine(Work, "kyber-weave"), "new");
        _files.FailWhen(
            operation => operation.Name == nameof(_files.MoveFile) && operation.Path?.EndsWith(".new", StringComparison.Ordinal) == true,
            new IOException("denied"));

        _ = Assert.Throws<IOException>(
            () => _system.ReplaceBinary(Path.Combine(Work, "kyber-weave"), Destination, windows: false, macOs: false));

        Assert.Equal("old", _files.ReadAllText(Destination));
        Assert.Equal([Destination], _files.EnumerateFiles(Install, "*", recursive: true));
    }

    [Fact]
    public void ARecoveryAfterAStopBetweenTheTwoWindowsMovesInstallsTheNewImageAndKeepsTheOldOneForTheNextUpdate()
    {
        _files.WriteAllText(Destination + ".old", "old");
        _files.WriteAllText(Path.Combine(Work, "kyber-weave"), "new");

        _system.ReplaceBinary(Path.Combine(Work, "kyber-weave"), Destination, windows: true, macOs: false);

        Assert.Equal("new", _files.ReadAllText(Destination));
        Assert.Equal("old", _files.ReadAllText(Destination + ".old"));
    }

    [Fact]
    public void ARecoveryWhoseCommitFailsStillLeavesTheUserAnImage()
    {
        _files.WriteAllText(Destination + ".old", "old");
        _files.WriteAllText(Path.Combine(Work, "kyber-weave"), "new");
        _files.FailWhen(
            operation => operation.Name == nameof(_files.MoveFile) && operation.Path?.EndsWith(".new", StringComparison.Ordinal) == true,
            new IOException("denied"));

        _ = Assert.Throws<IOException>(
            () => _system.ReplaceBinary(Path.Combine(Work, "kyber-weave"), Destination, windows: true, macOs: false));

        Assert.Equal("old", _files.ReadAllText(Destination + ".old"));
        Assert.Equal([Destination + ".old"], _files.EnumerateFiles(Install, "*", recursive: true));
    }

    [Fact]
    public void ANormalWindowsUpdateAfterARecoveryRemovesTheOldImage()
    {
        _files.WriteAllText(Destination, "current");
        _files.WriteAllText(Destination + ".old", "stray");
        _files.WriteAllText(Path.Combine(Work, "kyber-weave"), "new");

        _system.ReplaceBinary(Path.Combine(Work, "kyber-weave"), Destination, windows: true, macOs: false);

        Assert.Equal([Destination], _files.EnumerateFiles(Install, "*", recursive: true));
    }

    [Fact]
    public void AFailedCopyOrFinalMoveLeavesNoStagingFile()
    {
        _files.WriteAllText(Path.Combine(Install, "source"), "source");
        _files.WriteAllText(Path.Combine(Work, "destination"), "destination");
        _files.FailWhen(
            operation => operation.Name == nameof(_files.MoveFile) && operation.Path?.EndsWith(".new", StringComparison.Ordinal) == true,
            new IOException("denied"));

        _ = Assert.Throws<IOException>(() => _system.CopyFile(Path.Combine(Install, "source"), Path.Combine(Work, "destination"), overwrite: true));

        Assert.Equal("destination", _files.ReadAllText(Path.Combine(Work, "destination")));
        Assert.DoesNotContain(Path.Combine(Work, ".destination.new"), _files.EnumerateFiles(Work, "*", recursive: false));
    }

    [Fact]
    public void ACopyThatFailsWhileWritingLeavesNoStagingFile()
    {
        _files.WriteAllText(Path.Combine(Install, "source"), "source");
        _files.FailWhen(
            operation => operation.Name == nameof(_files.OpenWriteNew),
            new IOException("disk full"));

        _ = Assert.Throws<IOException>(() => _system.CopyFile(Path.Combine(Install, "source"), Path.Combine(Work, "destination"), overwrite: true));

        Assert.Empty(_files.EnumerateFiles(Work, "*", recursive: false));
    }

    [Fact]
    public void AnOldImageThatCannotBeRemovedStaysAndTheUpdateStillSucceeds()
    {
        _files.WriteAllText(Destination, "old");
        _files.WriteAllText(Path.Combine(Work, "kyber-weave"), "new");
        int deletesOfTheOldImage = 0;
        _files.FailWhen(
            operation => operation.Name == nameof(_files.DeleteFile) && operation.Path == Destination + ".old" && ++deletesOfTheOldImage == 2,
            new IOException("locked"));

        _system.ReplaceBinary(Path.Combine(Work, "kyber-weave"), Destination, windows: true, macOs: false);

        Assert.Equal("new", _files.ReadAllText(Destination));
        Assert.Equal("old", _files.ReadAllText(Destination + ".old"));
    }

    [Fact]
    public void AnExtractedBinaryThatIsMissingIsNamed()
    {
        SelfUpdateException failure = Assert.Throws<SelfUpdateException>(
            () => _system.ReplaceBinary(Path.Combine(Work, "kyber-weave"), Destination, windows: false, macOs: false));

        Assert.Equal("archive did not contain kyber-weave", failure.Message);
        Assert.False(_files.FileExists(Destination));
    }

    [Fact]
    public void OnMacOsTheQuarantineIsClearedOnTheStagedFileBeforeItIsMoved()
    {
        _files.WriteAllText(Path.Combine(Work, "kyber-weave"), "new");

        _system.ReplaceBinary(Path.Combine(Work, "kyber-weave"), Destination, windows: false, macOs: true);

        ProcessStartInfo xattr = Assert.Single(_processes.Calls);
        Assert.Equal("xattr", xattr.FileName);
        Assert.Equal(["-d", "com.apple.quarantine", Path.Combine(Install, ".kyber-weave.new")], xattr.ArgumentList);
        Assert.Equal([TimeSpan.FromSeconds(30)], _processes.Timeouts);
        Assert.Equal("new", _files.ReadAllText(Destination));
    }

    [Fact]
    public void AFailingQuarantineClearDoesNotStopTheReplacement()
    {
        ScriptedProcessExecutor failing = new(_ => throw new TimeoutException("xattr hung"));
        SelfUpdateSystem system = new(
            _files, new FakePathResolver(_files), failing, _console, _modes);
        _files.WriteAllText(Path.Combine(Work, "kyber-weave"), "new");

        system.ReplaceBinary(Path.Combine(Work, "kyber-weave"), Destination, windows: false, macOs: true);

        Assert.Equal("new", _files.ReadAllText(Destination));
    }

    [Fact]
    public void CopyingCarriesContentAndPermissionBitsAndLeavesNoStagingFile()
    {
        _files.WriteAllText(Path.Combine(Install, "source"), "binary");
        UnixFileMode executable = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        _modes.Set(Path.Combine(Install, "source"), executable);

        _system.CopyFile(Path.Combine(Install, "source"), Path.Combine(Work, "backup"), overwrite: true);

        Assert.Equal("binary", _files.ReadAllText(Path.Combine(Work, "backup")));
        Assert.Equal(executable, _modes.Modes[Path.Combine(Work, ".backup.new")]);
        Assert.Equal([Path.Combine(Work, "backup")], _files.EnumerateFiles(Work, "*", recursive: true));
    }

    [Fact]
    public void CopyingWithoutOverwriteRefusesAnExistingDestinationAndKeepsIt()
    {
        _files.WriteAllText(Path.Combine(Install, "source"), "source");
        _files.WriteAllText(Path.Combine(Work, "destination"), "destination");

        _ = Assert.Throws<IOException>(() => _system.CopyFile(Path.Combine(Install, "source"), Path.Combine(Work, "destination"), overwrite: false));
        _system.CopyFile(Path.Combine(Install, "source"), Path.Combine(Work, "destination"), overwrite: true);

        Assert.Equal("source", _files.ReadAllText(Path.Combine(Work, "destination")));
    }

    [Fact]
    public void CopyingAMissingSourceIsNamed()
    {
        _ = Assert.Throws<FileNotFoundException>(() => _system.CopyFile(Path.Combine(Install, "absent"), Path.Combine(Work, "destination"), overwrite: true));
    }

    [Fact]
    public void AWritableDirectoryProbesTrueAndLeavesNoProbeBehind()
    {
        Assert.True(_system.CanWriteDirectory(Install));

        Assert.Empty(_files.EnumerateFiles(Install, "*", recursive: false));
    }

    [Fact]
    public void AProbeAKilledUpdateLeftDoesNotMakeTheDirectoryLookUnwritable()
    {
        _files.WriteAllText(Path.Combine(Install, ".kyber-weave-update-probe"), string.Empty);

        Assert.True(_system.CanWriteDirectory(Install));

        Assert.Empty(_files.EnumerateFiles(Install, "*", recursive: false));
    }

    [Fact]
    public void AProbeThatCannotBeCreatedLeavesNothingBehind()
    {
        _files.FailWhen(
            operation => operation.Name == nameof(_files.OpenWriteNew),
            new IOException("disk full"));

        Assert.False(_system.CanWriteDirectory(Install));

        Assert.Empty(_files.EnumerateFiles(Install, "*", recursive: false));
    }

    [Fact]
    public void ADirectoryThatIsNotThereProbesFalse()
    {
        Assert.False(_system.CanWriteDirectory(Path.Combine(Install, "missing")));
    }

    [Fact]
    public void AnUnwritableDirectoryProbesFalseWhenTheProbeIsRefused()
    {
        _files.FailWhen(
            operation => operation.Name == nameof(_files.OpenWriteNew),
            new UnauthorizedAccessException("read-only"));

        Assert.False(_system.CanWriteDirectory(Install));
    }

    [Fact]
    public void ACapturedVerbRunsThroughTheBoundedExecutorWithTwoMinutes()
    {
        ProcessResult result = _system.RunCapture("kyberdash", ["engine", "ensure"]);

        Assert.Equal("out", result.StandardOutput);
        Assert.Equal([TimeSpan.FromMinutes(2)], _processes.Timeouts);
        ProcessStartInfo call = Assert.Single(_processes.Calls);
        Assert.Equal(["engine", "ensure"], call.ArgumentList);
        Assert.True(call.RedirectStandardOutput);
    }

    [Fact]
    public void TheTrayStepSharesTheConsoleWithFiveMinutes()
    {
        _console.ExitCode = 7;

        Assert.Equal(7, _system.RunProcess("kyberdash", ["tray", "update"]));

        Assert.Equal(TimeSpan.FromMinutes(5), Assert.Single(_console.Runs).Timeout);
        Assert.Empty(_processes.Calls);
    }

    [Fact]
    public void ATrayStepThatCannotStartIsNamedAndOneThatHangsKeepsTheFiveMinuteMessage()
    {
        _console.Failure = new InvalidOperationException("no such program");
        SelfUpdateException cannotStart = Assert.Throws<SelfUpdateException>(() => _system.RunProcess("kyberdash", []));
        Assert.Equal("the KyberDash tray step could not start kyberdash.", cannotStart.Message);

        _console.Failure = new TimeoutException("stopped");
        TimeoutException hung = Assert.Throws<TimeoutException>(() => _system.RunProcess("kyberdash", []));
        Assert.Equal("The process did not exit within 300 seconds.", hung.Message);
    }

    [Fact]
    public void TheFinishUpdateChildSharesTheConsoleWithFifteenMinutes()
    {
        _console.ExitCode = 3;

        Assert.Equal(3, _system.StartFinishingProcess("kyber-weave", ["update", "--finish-update"], discardOutput: false));

        (string FileName, IReadOnlyList<string> Arguments, TimeSpan Timeout, bool DiscardOutput) run = Assert.Single(_console.Runs);
        Assert.Equal(TimeSpan.FromMinutes(15), run.Timeout);
        Assert.False(run.DiscardOutput);
        Assert.Empty(_processes.Calls);
    }

    [Fact]
    public void TheRehearsalRunsTheHandOffStartPathBoundedByThirtySecondsWithItsOutputDiscarded()
    {
        _console.ExitCode = 5;

        int exit = _system.StartFinishingProcess("kyber-weave", ["--version"], discardOutput: true);

        Assert.Equal(5, exit);
        (string FileName, IReadOnlyList<string> Arguments, TimeSpan Timeout, bool DiscardOutput) run = Assert.Single(_console.Runs);
        Assert.Equal(TimeSpan.FromSeconds(30), run.Timeout);
        Assert.True(run.DiscardOutput);
        Assert.Empty(_processes.Calls);
    }

    private string Stage(string name, byte[] bytes)
    {
        string path = Path.Combine(Work, name);
        _files.WriteAllBytes(path, bytes);
        return path;
    }
}
