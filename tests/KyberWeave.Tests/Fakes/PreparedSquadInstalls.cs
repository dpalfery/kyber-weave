using System.Collections.Concurrent;
using KyberWeave.Core.Squad.Deployment;

namespace KyberWeave.Tests.Fakes;

/// <summary>
/// One real Squad install, built at most once for the whole test run and copied by every test that
/// needs it.
/// </summary>
/// <remarks>
/// A real install renders the bundle, journals and applies every file, and fsyncs the receipt. A test
/// whose subject is something else should pay that once for the run, not once per test. The install is
/// made here through the real install path, on the first request for its key, and is never written
/// afterwards; a test asks for a copy in its own folder instead (see
/// <see cref="SquadInstallCopy"/>) and removes that copy.
/// </remarks>
public class PreparedSquadInstall : IDisposable
{
    private readonly TempDirectory _directory;

    /// <summary>Wraps an install that the caller already made into <paramref name="directory"/>.</summary>
    /// <param name="key">The recipe this install answers, which is also its cache key.</param>
    /// <param name="directory">The directory holding the install. The provider owns and removes it.</param>
    internal PreparedSquadInstall(string key, TempDirectory directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(directory);
        Key = key;
        _directory = directory;
    }

    /// <summary>The recipe this install answers.</summary>
    public string Key { get; }

    /// <summary>The prepared install's own directory. Callers copy from it and never write to it.</summary>
    public string TemplatePath => _directory.Path;

    /// <inheritdoc />
    public virtual void Dispose()
    {
        _directory.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// The run-wide cache of prepared installs, keyed by the recipe that produces them.
/// </summary>
/// <remarks>
/// The first request for a key builds the install; every later request, on any thread, gets the same
/// instance. The cache is process-wide because the test runner runs one process for the whole run, and
/// it removes every prepared install when the process exits. A test never disposes a prepared install:
/// it copies it and disposes the copy.
/// </remarks>
public static class PreparedSquadInstalls
{
    private static readonly ConcurrentDictionary<string, Lazy<PreparedSquadInstall>> Installs =
        new(StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<string, int> BuildCounts = new(StringComparer.Ordinal);

    static PreparedSquadInstalls()
    {
        AppDomain.CurrentDomain.ProcessExit += static (_, _) => RemoveAll();
    }

    /// <summary>The number of times the install for <paramref name="key"/> was built in this run.</summary>
    /// <param name="key">The recipe named when the install was requested.</param>
    /// <returns>Zero when nothing has asked for that recipe yet.</returns>
    public static int BuildsOf(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return BuildCounts.TryGetValue(key, out int count) ? count : 0;
    }

    /// <summary>
    /// Gets the prepared install for <paramref name="key"/>, building it with <paramref name="build"/>
    /// if this is the first request.
    /// </summary>
    /// <typeparam name="T">The install type the recipe produces.</typeparam>
    /// <param name="key">The recipe. Two callers with the same key share one install.</param>
    /// <param name="build">Makes the install. It runs once, under a lock, and must not return null.</param>
    /// <returns>The one install for that recipe.</returns>
    public static T Get<T>(string key, Func<T> build)
        where T : PreparedSquadInstall
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(build);

        Lazy<PreparedSquadInstall> entry = Installs.GetOrAdd(
            key,
            static (recipe, factory) => new Lazy<PreparedSquadInstall>(
                () =>
                {
                    PreparedSquadInstall install = factory();
                    BuildCounts.AddOrUpdate(recipe, 1, static (_, count) => count + 1);
                    return install;
                },
                LazyThreadSafetyMode.ExecutionAndPublication),
            () => build()!);

        return (T)entry.Value;
    }

    /// <summary>Removes every prepared install.</summary>
    public static void RemoveAll()
    {
        foreach (string key in Installs.Keys)
            Remove(key);
    }

    /// <summary>Removes the prepared install for <paramref name="key"/>, if there is one.</summary>
    /// <param name="key">The recipe to remove. Nothing else is touched, so one test can remove its own install.</param>
    internal static void Remove(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (Installs.TryRemove(key, out Lazy<PreparedSquadInstall>? entry) && entry.IsValueCreated)
            entry.Value.Dispose();

        _ = BuildCounts.TryRemove(key, out _);
    }
}

/// <summary>
/// Copying a prepared install into a test's own folder, with the template's recorded absolute paths
/// re-rooted to the copy.
/// </summary>
public static class SquadInstallCopy
{
    private static readonly SquadFileSystemPathSemantics Semantics = SquadFileSystemPathSemantics.Instance;

    /// <summary>Copies a directory tree with <see cref="File.Copy"/>, which clones copy-on-write where the file system offers it.</summary>
    /// <param name="source">The directory to copy from. It is only read.</param>
    /// <param name="destination">The directory to copy into. It is created if it is missing.</param>
    /// <param name="include">Filters a path relative to <paramref name="source"/>; null copies everything.</param>
    public static void Tree(string source, string destination, Func<string, bool>? include = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);

        _ = Directory.CreateDirectory(destination);
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, directory);
            if (include is null || include(relative))
                _ = Directory.CreateDirectory(Path.Combine(destination, relative));
        }

        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);
            if (include is not null && !include(relative))
                continue;

            string target = Path.Combine(destination, relative);
            _ = Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    /// <summary>
    /// Rewrites the template's recorded root to the copy's root in every text file under
    /// <paramref name="stateDirectory"/>.
    /// </summary>
    /// <remarks>
    /// Only state is rewritten. A deployed file's bytes are what its receipt digest names, so rewriting
    /// one would read as a hand edit to the next update or to doctor.
    /// </remarks>
    /// <param name="stateDirectory">The copied state directory. It may not exist.</param>
    /// <param name="from">The template's canonical root.</param>
    /// <param name="to">The copy's canonical root.</param>
    public static void ReRoot(string stateDirectory, string from, string to)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(from);
        ArgumentException.ThrowIfNullOrWhiteSpace(to);

        if (!Directory.Exists(stateDirectory) || string.Equals(from, to, StringComparison.Ordinal))
            return;

        foreach (string file in Directory.EnumerateFiles(stateDirectory, "*", SearchOption.AllDirectories))
        {
            string text = File.ReadAllText(file);
            if (!text.Contains(from, StringComparison.Ordinal))
                continue;

            File.WriteAllText(file, text.Replace(from, to, StringComparison.Ordinal));
        }
    }

    /// <summary>The physical identity of <paramref name="path"/> as the product records it, existing or not.</summary>
    /// <param name="path">The path to canonicalise.</param>
    /// <returns>The canonical path.</returns>
    public static string Canonical(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return SquadPhysicalRootIdentity.CanonicalizeAllowingMissing(path, Semantics);
    }
}
