using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Tests.FileSystem;
using KyberWeave.Application.Squad.Release;

namespace KyberWeave.Tests.Fakes;

/// <summary>
/// Builds the real <see cref="GitHubSquadReleaseSource"/> over an in-memory file system, so a test of the download,
/// verification and extraction rules needs no disk and no socket.
/// </summary>
internal static class InMemorySquadReleaseSource
{
    /// <summary>The source over <paramref name="handler"/> and the in-memory tree.</summary>
    /// <param name="files">The file system the archive is extracted into.</param>
    /// <param name="handler">The transport; it must not follow redirects.</param>
    /// <param name="apiRoot">The release API root.</param>
    /// <param name="currentDirectory">The working directory a relative destination resolves against.</param>
    /// <param name="onStagingCreated">Called once the staging directory exists.</param>
    /// <param name="maximumAssetBytes">The largest asset one download buffers.</param>
    internal static GitHubSquadReleaseSource Create(
        InMemoryFileSystem files,
        HttpMessageHandler handler,
        Uri apiRoot,
        string? currentDirectory = null,
        Action? onStagingCreated = null,
        long maximumAssetBytes = GitHubSquadReleaseSource.DefaultMaximumAssetBytes) =>
        new(
            handler,
            apiRoot,
            files,
            new FakePathResolver(files),
            new FakeProcessEnvironment(
                currentDirectory: currentDirectory ?? files.Root,
                isFileSystemCaseInsensitive: !files.CaseSensitive),
            new FakeIdGenerator(),
            onStagingCreated,
            maximumAssetBytes);
}
