using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Data.Host;
using KyberWeave.Data.Hooks;
using KyberWeave.Data.Paths;
using KyberWeave.Testing.Squad;

namespace KyberWeave.Tests.Fakes;

/// <summary>
/// Builds the real <see cref="GitHubSquadReleaseSource"/> over the real file system for the tests whose subject is the
/// files an extraction leaves on disk. The class itself takes its seams, so every test that does not need real files
/// injects an in-memory file system instead.
/// </summary>
internal static class RealSquadReleaseSource
{
    /// <summary>The source over <paramref name="handler"/> and the physical file system, paths and identifiers.</summary>
    /// <param name="handler">The transport; it must not follow redirects.</param>
    /// <param name="apiRoot">The release API root.</param>
    /// <param name="readEnvironment">Reads an environment variable; the process environment when null.</param>
    /// <param name="onStagingCreated">Called once the staging directory exists.</param>
    /// <param name="maximumAssetBytes">The largest asset one download buffers.</param>
    internal static GitHubSquadReleaseSource Create(
        HttpMessageHandler handler,
        Uri apiRoot,
        Func<string, string?>? readEnvironment = null,
        Action? onStagingCreated = null,
        long maximumAssetBytes = GitHubSquadReleaseSource.DefaultMaximumAssetBytes)
    {
        ProcessEnvironment process = new();
        PhysicalFileSystem files = new();
        return new GitHubSquadReleaseSource(
            handler,
            apiRoot,
            files,
            new PhysicalPathResolver(files),
            new FakeProcessEnvironment(
                currentDirectory: process.CurrentDirectory,
                operatingSystem: process.OperatingSystem,
                isFileSystemCaseInsensitive: process.IsFileSystemCaseInsensitive,
                readVariable: readEnvironment ?? process.GetVariable),
            new SystemIdGenerator(),
            onStagingCreated,
            maximumAssetBytes);
    }

    /// <summary>The source over <paramref name="handler"/> and the in-memory file system of <paramref name="state"/>.</summary>
    /// <param name="handler">The transport; it must not follow redirects.</param>
    /// <param name="apiRoot">The release API root.</param>
    /// <param name="state">The in-memory state whose file system, paths and environment the source works in.</param>
    internal static GitHubSquadReleaseSource CreateInMemory(HttpMessageHandler handler, Uri apiRoot, InMemorySquadState state) =>
        new(
            handler,
            apiRoot,
            state.Files,
            state.Paths,
            state.Environment,
            new FakeIdGenerator(),
            null,
            GitHubSquadReleaseSource.DefaultMaximumAssetBytes);
}
