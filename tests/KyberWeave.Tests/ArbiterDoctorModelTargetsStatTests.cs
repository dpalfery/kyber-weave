using System.Runtime.Versioning;
using KyberWeave.Cli.Commands.Arbiter;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Contract for the review fix to <c>arbiter doctor</c>'s model targets check: reading a file's write
/// time must never throw out of the doctor. A missing file reads as missing, and a file whose stat is
/// denied reads as unreadable, so the check degrades to its <c>KW-ARB-MODEL-001</c> warning.
/// </summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public sealed class ArbiterDoctorModelTargetsStatTests : IDisposable
{
    private readonly TempDirectory _root = new();

    public void Dispose()
    {
        // A directory left unsearchable by a test must be restored before it can be deleted.
        string locked = Path.Combine(_root.Path, "locked");
        if (Directory.Exists(locked))
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        _root.Dispose();
    }

    [Fact]
    public void WriteTime_OfAPresentFile_IsReadAsPresent()
    {
        string path = Path.Combine(_root.Path, "model-targets.claude.json");
        File.WriteAllText(path, "{}");

        bool present = ArbiterDoctorCommand.TryGetWriteTimeUtc(path, out DateTime written);

        Assert.True(present);
        Assert.True(written > DateTime.UnixEpoch);
    }

    [Fact]
    public void WriteTime_OfAMissingFile_IsReadAsMissingNotAsOldestDate()
    {
        string path = Path.Combine(_root.Path, "absent.json");

        bool present = ArbiterDoctorCommand.TryGetWriteTimeUtc(path, out _);

        Assert.False(present);
    }

    [Fact]
    public void WriteTime_WhenTheDirectoryCannotBeSearched_ReturnsFalseInsteadOfThrowing()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            // Windows and root ignore the directory mode, so the denied stat cannot be produced here.
            Assert.Skip("Directory search permission is not enforced for this user on this platform.");
        }

        string locked = Path.Combine(_root.Path, "locked");
        Directory.CreateDirectory(locked);
        string path = Path.Combine(locked, "model-targets.claude.json");
        File.WriteAllText(path, "{}");
        File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        bool present = ArbiterDoctorCommand.TryGetWriteTimeUtc(path, out _);

        Assert.False(present);
    }
}
