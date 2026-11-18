using KyberWeave.Contracts.Paths;
using KyberWeave.Data.Paths;
using KyberWeave.Tests.FileSystem;

namespace KyberWeave.Tests.Fakes;

/// <summary>The path seam in a unit test: the shared resolver answering from an <see cref="InMemoryFileSystem" />.</summary>
/// <remarks>
/// The path seam holds no operating-system state of its own, so the only two things a fake can change are the
/// file system it reads and the case rule that file system has. Both come from the in-memory instance here, which
/// is why the shared contract suite can prove this fake and the physical adapter with one set of assertions.
/// </remarks>
public sealed class FakePathResolver(InMemoryFileSystem files)
    : FileSystemPathResolver(files, files.CaseSensitive)
{
    /// <summary>Gets the in-memory file system the resolver answers from.</summary>
    public InMemoryFileSystem Files { get; } = files;

    /// <summary>Creates a resolver over a new, empty file system.</summary>
    /// <param name="caseSensitive">True for Linux name rules; false for APFS and NTFS rules.</param>
    /// <returns>The resolver.</returns>
    public static FakePathResolver OverEmptyFileSystem(bool caseSensitive = false) =>
        new(new InMemoryFileSystem(caseSensitive: caseSensitive));
}
