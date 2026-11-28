using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using KyberWeave.Cli.Commands.Docs;
using KyberWeave.Core.Docs.Closeout;
using KyberWeave.Core.Processes;
using Xunit;

using KyberWeave.Tests.Fakes;
namespace KyberWeave.Integration.Tests;

/// <summary>
/// <c>docs closeout</c> against a real directory: a symbolic link at an index path, Unix file modes, the exclusive
/// file create the archive uses, and git staging of the moved files. The rest of the command is covered over the
/// in-memory file system in <c>DocsCloseoutCommandTests</c>.
/// </summary>
public sealed class DocsCloseoutRealFileSystemTests
{
    private const string Plan = DocsCloseoutRepository.Plan;
    private const string Archived = DocsCloseoutRepository.Archived;
    private const string PlanIndex = DocsCloseoutRepository.PlanIndex;
    private const string Inventory = DocsCloseoutRepository.Inventory;
    private const string Date = DocsCloseoutRepository.Date;

    private static DocFixture Repository() => DocsCloseoutRepository.Create(inMemory: false);

    private static async Task<(int Exit, string Output)> RunAsync(
        DocFixture fixture,
        bool archive,
        string path = Plan,
        string? date = Date)
    {
        DocsCloseoutSettings settings = new()
        {
            Path = Path.Combine(fixture.Root, path.Replace('/', Path.DirectorySeparatorChar)),
            Format = "json",
            Archive = archive,
            Date = date
        };

        CapturedConsoleExecution<int> run = await ProcessConsoleCapture.RunAsync(
            () => DocsTestCommands.Closeout().ExecuteAsync(null!, settings, CancellationToken.None));
        return (run.Result, run.Output);
    }

    private static string Read(DocFixture fixture, string relative) =>
        File.ReadAllText(Path.Combine(fixture.Root, relative.Replace('/', Path.DirectorySeparatorChar)));

    private static bool Exists(DocFixture fixture, string relative) =>
        File.Exists(Path.Combine(fixture.Root, relative.Replace('/', Path.DirectorySeparatorChar)));

    private static string Absolute(DocFixture fixture, string relative) =>
        Path.Combine(fixture.Root, relative.Replace('/', Path.DirectorySeparatorChar));

    private const string EarlierArchive = "# an earlier archive entry that is not ours\n";

    [Theory]
    [InlineData(PlanIndex)]
    [InlineData(Inventory)]
    public async Task ALinkAtAnIndexPathIsRefusedAndNothingMoves(string linkedPath)
    {
        using DocFixture fixture = Repository();
        string linked = Path.Combine(fixture.Root, linkedPath.Replace('/', Path.DirectorySeparatorChar));
        string target = Path.Combine(fixture.Root, "owner-copy.md");
        string before = await File.ReadAllTextAsync(linked, CancellationToken.None);
        File.Move(linked, target);
        try
        {
            File.CreateSymbolicLink(linked, target);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Skip("Symbolic-link creation is not available here; the link refusal was not run.");
        }

        (int exit, string output) = await RunAsync(fixture, archive: true);

        Assert.Equal(1, exit);
        Assert.Contains("KW-DOC-CLOSEOUT-003", output, StringComparison.Ordinal);
        Assert.Contains("is a symbolic link", output, StringComparison.Ordinal);
        Assert.Equal(before, await File.ReadAllTextAsync(target, CancellationToken.None));
        Assert.True(Exists(fixture, Plan));
        Assert.False(Exists(fixture, Archived));
    }

    [Fact]
    public void TheExclusiveCreateRefusesAnExistingFileAndLeavesItsBytesAlone()
    {
        using DocFixture fixture = Repository();
        string path = Absolute(fixture, Archived);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, EarlierArchive);

        bool written = DocsPorts.Exclusive.TryWriteNew(path, "replacement");

        Assert.False(written);
        Assert.Equal(EarlierArchive, File.ReadAllText(path));
    }

    [Fact]
    public void TheExclusiveCreateWritesANewFileWithoutAByteOrderMark()
    {
        using DocFixture fixture = Repository();
        string path = Absolute(fixture, "6-Docs/archive/plans/new.md");

        bool written = DocsPorts.Exclusive.TryWriteNew(path, "# new\n");

        Assert.True(written);
        Assert.Equal(Encoding.UTF8.GetBytes("# new\n"), File.ReadAllBytes(path));
    }

    /// <summary>
    /// A failure while writing is not "the archive already exists": it propagates, and the partial file this call
    /// created is removed so the next closeout is not refused on it. A lone surrogate cannot be encoded, which fails
    /// the write after the file was created, as a full disk would.
    /// </summary>
    [Fact]
    public void AWriteThatFailsAfterTheCreatePropagatesAndRemovesThePartialFile()
    {
        using DocFixture fixture = Repository();
        string path = Absolute(fixture, "6-Docs/archive/plans/partial.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        Assert.ThrowsAny<EncoderFallbackException>(() => DocsPorts.Exclusive.TryWriteNew(path, "text then a lone surrogate \uD800 here"));

        Assert.False(File.Exists(path));
    }

    /// <summary>
    /// When removing the partial file fails too, the write error is still what propagates: a cleanup failure must not
    /// replace the error the caller needs to see.
    /// </summary>
    [Fact]
    public void AFailingCleanupDoesNotMaskTheWriteError()
    {
        using DocFixture fixture = Repository();
        string path = Absolute(fixture, "6-Docs/archive/plans/stuck.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        List<string> deleteAttempts = [];

        Assert.ThrowsAny<EncoderFallbackException>(() => DocsPorts.Exclusive.TryWriteNew(
            path,
            "text then a lone surrogate \uD800 here",
            attempted =>
            {
                deleteAttempts.Add(attempted);
                throw new IOException("the file is in use");
            }));

        Assert.Equal([path], deleteAttempts);
    }

    /// <summary>
    /// A folder where the archive file belongs fails the exclusive create without a file being there to find: Windows
    /// answers with an access error, Unix with "exists". Either is a taken path, refused with nothing overwritten and no
    /// second look at the path to decide it.
    /// </summary>
    [Fact]
    public void ThePathHoldingAFolderIsRefusedAsTakenAndTheFolderIsLeftAlone()
    {
        using DocFixture fixture = Repository();
        string path = Absolute(fixture, "6-Docs/archive/plans/folder.md");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "inside.txt"), "kept");

        bool written = DocsPorts.Exclusive.TryWriteNew(path, "text");

        Assert.False(written);
        Assert.Equal("kept", File.ReadAllText(Path.Combine(path, "inside.txt")));
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void AFolderThatCannotBeWrittenToIsRefusedNotThrownAndNothingIsCreated()
    {
        Assert.SkipWhen(
            OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess,
            "Unix directory modes are not enforced on Windows or for a privileged user, so a denied create cannot be simulated.");
        using DocFixture fixture = Repository();
        string directory = Absolute(fixture, "6-Docs/archive/plans/locked");
        Directory.CreateDirectory(directory);
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            bool written = DocsPorts.Exclusive.TryWriteNew(Path.Combine(directory, "new.md"), "text");

            Assert.False(written);
            Assert.False(File.Exists(Path.Combine(directory, "new.md")));
        }
        finally
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task AnUnreadablePlanIsNamedInTheReportNotThrown()
    {
        Assert.SkipWhen(
            OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess,
            "Unix file modes are not enforced on Windows or for a privileged user, so an unreadable plan cannot be simulated.");

        using DocFixture fixture = Repository();
        string plan = Absolute(fixture, Plan);
        File.SetUnixFileMode(plan, UnixFileMode.None);
        try
        {
            (int exit, string output) = await RunAsync(fixture, archive: true);

            Assert.Equal(1, exit);
            Assert.Contains(PlanCloseout.NotAnActivePlan, output, StringComparison.Ordinal);
            Assert.Contains("could not be read", output, StringComparison.Ordinal);
            Assert.False(Exists(fixture, Archived));
        }
        finally
        {
            File.SetUnixFileMode(plan, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public async Task InAGitRepositoryTheMovedAndEditedFilesAreStaged()
    {
        using DocFixture fixture = Repository();
        Directory.Delete(Path.Combine(fixture.Root, ".git"));
        Git(fixture, "init", "--quiet");
        Git(fixture, "add", "-A");
        Git(fixture, "-c", "user.name=t", "-c", "user.email=t@example.invalid", "-c", "commit.gpgsign=false", "commit", "--quiet", "-m", "start");

        (int exit, string output) = await RunAsync(fixture, archive: true);

        Assert.True(exit == 0, output);
        string status = Git(fixture, "status", "--porcelain");
        Assert.DoesNotContain("??", status, StringComparison.Ordinal);
        Assert.Contains("6-Docs/archive/plans/2026-10-20-widget.md", status, StringComparison.Ordinal);
    }

    private static string Git(DocFixture fixture, params string[] arguments)
    {
        ProcessStartInfo startInfo = new("git")
        {
            WorkingDirectory = fixture.Root,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        ProcessResult result = ProcessRunner.Run(startInfo, string.Empty, TimeSpan.FromSeconds(60));
        Assert.True(result.ExitCode == 0, $"git {string.Join(' ', arguments)}: {result.StandardError}");
        return result.StandardOutput;
    }
}
