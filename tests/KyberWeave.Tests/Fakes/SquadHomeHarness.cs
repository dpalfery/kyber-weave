using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using System.Runtime.Versioning;
using Xunit;
using Xunit.Sdk;

namespace KyberWeave.Tests.Fakes;

/// <summary>
///     The one fake home for Squad tests that exercise the user-home state: a temp home, a temp
///     application data directory, and temp global roots, all beneath the process temp directory.
///     Shared so a home-isolation fix lands in every Squad suite at once.
/// </summary>
internal sealed class SquadHomeHarness : IDisposable
{
    public SquadHomeHarness(string root)
    {
        Root = root;
        ApplicationData = Path.Combine(root, "app-data");
        Home = Path.Combine(root, "home");
        GlobalRootsDirectory = Path.Combine(root, "global-roots");
        Directory.CreateDirectory(ApplicationData);
        Directory.CreateDirectory(Home);
        Directory.CreateDirectory(GlobalRootsDirectory);
        UserPaths = new HomeUserPaths(ApplicationData, Home);
        Store = new SquadStateStore(UserPaths);
        ReleaseSource = new SyntheticValidSquadReleaseSource();
        GlobalRoots = new TempGlobalRoots(GlobalRootsDirectory);
    }

    public string Root { get; }

    public string Home { get; }

    public string ApplicationData { get; }

    public string GlobalRootsDirectory { get; }

    public HomeUserPaths UserPaths { get; }

    public SquadStateStore Store { get; }

    public SyntheticValidSquadReleaseSource ReleaseSource { get; }

    public TempGlobalRoots GlobalRoots { get; }

    /// <summary>Releases the fake release source; the temp root itself is removed by the owning test class.</summary>
    public void Dispose()
    {
        ReleaseSource.Dispose();
    }

    public string MakeDirectory(string name)
    {
        string path = Path.Combine(Root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    public string GlobalRoot(SquadTarget target)
    {
        return GlobalRoots.ResolveGlobalRoot(target);
    }

    public SquadLifecycleService Lifecycle(ISquadRenderer renderer)
    {
        return new SquadLifecycleService(ReleaseSource, renderer, Store, globalRoots: GlobalRoots);
    }

    /// <summary>
    ///     The environment variable a CI runner sets (GitHub Actions and most others export <c>CI</c>). When it is
    ///     set, a denial that cannot be simulated fails the test instead of skipping it, so a CI host that runs
    ///     as root cannot silently pass the read-denial scenarios.
    /// </summary>
    public const string CiEnvironmentVariable = "CI";

    /// <summary>
    ///     Refuses this process read access to an existing file until the returned scope is disposed.
    ///     On Windows an exclusive open makes every other open of the file fail, this process's included.
    ///     On Unix the mode is cleared; a process that can still read it (root) is skipped, because the
    ///     denial it asked for cannot be expressed there.
    /// </summary>
    public static IDisposable DenyReadOrSkip(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
        }
        else
        {
            return DenyUnixReadOrSkip(path);
        }
    }

    [UnsupportedOSPlatform("windows")]
    private static IDisposable DenyUnixReadOrSkip(string path)
    {
        UnixFileMode original = File.GetUnixFileMode(path);
        File.SetUnixFileMode(path, UnixFileMode.None);
        try
        {
            File.ReadAllBytes(path);
        }
        catch (UnauthorizedAccessException)
        {
            return new RestoreMode(path, original);
        }
        catch (Exception)
        {
            File.SetUnixFileMode(path, original);
            throw;
        }

        File.SetUnixFileMode(path, original);
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(CiEnvironmentVariable)))
            throw new InvalidOperationException(
                $"The process can still read the mode-000 file '{path}' (running as root?), so the read denial cannot be simulated. " +
                $"{CiEnvironmentVariable} is set, so this must fail rather than skip.");

        throw SkipException.ForSkip("The process can still read a mode-000 file (running as root?), so a read denial cannot be simulated.");
    }

    [UnsupportedOSPlatform("windows")]
    private sealed class RestoreMode(string path, UnixFileMode mode) : IDisposable
    {
        public void Dispose()
        {
            File.SetUnixFileMode(path, mode);
        }
    }

    internal sealed class HomeUserPaths : ISquadUserPaths
    {
        public HomeUserPaths(string applicationDataDirectory, string homeDirectory)
        {
            RequireUnderTemp(applicationDataDirectory);
            RequireUnderTemp(homeDirectory);
            ApplicationDataDirectory = applicationDataDirectory;
            HomeDirectory = homeDirectory;
        }

        public string ApplicationDataDirectory { get; }

        public string HomeDirectory { get; }

        private static void RequireUnderTemp(string path)
        {
            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            if (!full.StartsWith(temp + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Squad CLI tests must keep every fake home and state root beneath the temp directory; got '{full}'.");
        }
    }

    internal sealed class TempGlobalRoots(string root) : ISquadGlobalRootResolver
    {
        public string ResolveGlobalRoot(SquadTarget target)
        {
            return Path.Combine(root, SquadTargetCatalog.GetToken(target));
        }
    }
}
