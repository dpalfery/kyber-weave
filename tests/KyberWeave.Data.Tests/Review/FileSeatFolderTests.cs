using KyberWeave.Contracts.Repositories;
using KyberWeave.Data.Review;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Tests.Review;

/// <summary>The seat folder lists markdown entries one level deep, over an in-memory file system.</summary>
public sealed class FileSeatFolderTests
{
    private static readonly string Folder = Path.GetFullPath(Path.Combine(new InMemoryFileSystem().Root, "seats"));

    private readonly InMemoryFileSystem _files = new();

    [Fact]
    public void AFolderThatExistsIsReportedAndAMissingOneIsNot()
    {
        FileSeatFolder seats = Seats();
        _files.CreateDirectory(Folder);

        Assert.True(seats.Exists(Folder));
        Assert.False(seats.Exists(Path.Combine(Folder, "missing")));
    }

    [Fact]
    public void MarkdownFilesAreListedAndOtherFilesAreNot()
    {
        _files.CreateDirectory(Folder);
        _files.WriteAllText(Path.Combine(Folder, "seat-a.md"), "a");
        _files.WriteAllText(Path.Combine(Folder, "notes.txt"), "b");

        SeatFolderEntry entry = Assert.Single(Seats().ListMarkdown(Folder));

        Assert.Equal(new SeatFolderEntry("seat-a.md", Path.Combine(Folder, "seat-a.md"), IsFile: true), entry);
    }

    [Fact]
    public void ADirectoryNamedLikeMarkdownIsListedAsNotAFile()
    {
        _files.CreateDirectory(Path.Combine(Folder, "seat-b.md"));

        SeatFolderEntry entry = Assert.Single(Seats().ListMarkdown(Folder));

        Assert.False(entry.IsFile);
    }

    [Fact]
    public void FilesInASubfolderAreNotListed()
    {
        _files.CreateDirectory(Path.Combine(Folder, "deeper"));
        _files.WriteAllText(Path.Combine(Folder, "deeper", "seat-c.md"), "c");

        Assert.Empty(Seats().ListMarkdown(Folder));
    }

    [Fact]
    public void ANameInAnotherCaseIsListedOnlyWhereTheFileSystemIgnoresCase()
    {
        _files.CreateDirectory(Folder);
        _files.WriteAllText(Path.Combine(Folder, "SEAT.MD"), "d");

        Assert.Single(Seats(OperatingSystemKind.MacOS).ListMarkdown(Folder));
        Assert.Empty(Seats(OperatingSystemKind.Linux).ListMarkdown(Folder));
    }

    [Fact]
    public void AFolderThatCannotBeListedIsRefusedNotReadAsEmpty()
    {
        _files.CreateDirectory(Folder);
        _files.FailWhen(operation => operation.Name == nameof(InMemoryFileSystem.EnumerateEntries), new UnauthorizedAccessException());

        Assert.Throws<UnauthorizedAccessException>(() => Seats().ListMarkdown(Folder));
    }

    private FileSeatFolder Seats(OperatingSystemKind system = OperatingSystemKind.Linux) =>
        new(_files, new FakeProcessEnvironment(operatingSystem: system));
}
