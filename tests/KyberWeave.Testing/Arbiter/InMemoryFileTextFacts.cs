using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;

namespace KyberWeave.Tests.Arbiter;

/// <summary>Builds <see cref="FileTextFacts"/> over an in-memory file system, so a test needs no disk.</summary>
internal static class InMemoryFileTextFacts
{
    /// <summary>The excerpt reader over the repository file store, path resolver and environment of <paramref name="files"/>.</summary>
    /// <param name="files">The in-memory file system the repository lives in.</param>
    internal static FileTextFacts Create(InMemoryFileSystem files) =>
        new(
            new InMemoryRepositoryFileStore(files),
            new FakePathResolver(files),
            new FakeProcessEnvironment(
                currentDirectory: ArbiterTestRoots.Under("work"),
                isFileSystemCaseInsensitive: !files.CaseSensitive));
}
