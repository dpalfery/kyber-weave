using KyberWeave.Core.Squad.Deployment;

namespace KyberWeave.Tests.Fakes;

/// <summary>
/// The one fake for <see cref="ISquadUserPaths"/>: it reports the home and application data directories the
/// test configured, and nothing about the real profile.
/// </summary>
/// <remarks>
/// Use a factory for a layout a test would otherwise rebuild: <see cref="FromHome"/> for state anchored on a
/// home directory, and <see cref="UnderTemp"/> where a test must be refused a path outside the temp directory.
/// </remarks>
public sealed class FakeSquadUserPaths : ISquadUserPaths
{
    /// <summary>User paths whose home sits beneath <paramref name="applicationDataDirectory"/>, as the product's default does.</summary>
    public FakeSquadUserPaths(string applicationDataDirectory)
        : this(applicationDataDirectory, Path.Combine(applicationDataDirectory, "home"))
    {
    }

    /// <summary>User paths with the application data and home directories the test names.</summary>
    public FakeSquadUserPaths(string applicationDataDirectory, string homeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDataDirectory);
        ArgumentNullException.ThrowIfNull(homeDirectory);
        ApplicationDataDirectory = applicationDataDirectory;
        HomeDirectory = homeDirectory;
    }

    /// <inheritdoc />
    public string ApplicationDataDirectory { get; }

    /// <inheritdoc />
    public string HomeDirectory { get; }

    /// <summary>
    /// User paths whose application data directory is the <c>application-data</c> sibling of
    /// <paramref name="homeDirectory"/>, for state read from the home directory alone.
    /// </summary>
    internal static FakeSquadUserPaths FromHome(string homeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);
        string trimmed = Path.TrimEndingDirectorySeparator(homeDirectory);
        string? parent = Path.GetDirectoryName(trimmed);
        if (string.IsNullOrEmpty(parent))
        {
            throw new ArgumentException(
                $"A home directory needs a parent directory to hold the application data beside it; got '{homeDirectory}'.",
                nameof(homeDirectory));
        }

        return new FakeSquadUserPaths(Path.Combine(parent, "application-data"), homeDirectory);
    }

    /// <summary>
    /// User paths that refuse a location outside the process temp directory, so a test that forgets its fixture
    /// fails instead of reaching the real user profile.
    /// </summary>
    internal static FakeSquadUserPaths UnderTemp(string applicationDataDirectory, string homeDirectory)
    {
        RequireUnderTemp(applicationDataDirectory);
        RequireUnderTemp(homeDirectory);
        return new FakeSquadUserPaths(applicationDataDirectory, homeDirectory);
    }

    private static void RequireUnderTemp(string path)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        if (!full.StartsWith(temp + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Squad CLI tests must keep every fake home and state root beneath the temp directory; got '{full}'.");
        }
    }
}
