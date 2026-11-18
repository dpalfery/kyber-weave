using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Release;

namespace KyberWeave.Tests.Fakes;

/// <summary>
/// Builds the provider key for a prepared install from the operation's full input.
/// </summary>
/// <remarks>
/// Two tests that ask for the same harnesses, scope and outcome-changing options get one prepared install;
/// a parameterised test over N option values causes at most N, however many tests use them.
/// </remarks>
public static class SquadInstallRecipe
{
    /// <summary>Formats one recipe key.</summary>
    /// <param name="kind">The operation: install, update or uninstall.</param>
    /// <param name="scope">Whether the deployment is at the project root or the home.</param>
    /// <param name="targets">The harnesses the deployment covers.</param>
    /// <param name="options">Every option that changes the outcome, with null for an absent one.</param>
    /// <returns>A key that differs whenever any input differs.</returns>
    public static string Key(
        string kind,
        SquadDeploymentScope scope,
        IEnumerable<SquadTarget> targets,
        params (string Name, string? Value)[] options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(options);

        return string.Join(
            "/",
            kind,
            scope.ToString(),
            string.Join(",", targets.Select(SquadTargetCatalog.GetToken).Order(StringComparer.Ordinal)),
            string.Join(
                ",",
                options
                    .OrderBy(option => option.Name, StringComparer.Ordinal)
                    .Select(option => $"{option.Name}={option.Value ?? "<unset>"}")));
    }
}

/// <summary>
/// Everything one real install, update or uninstall returned, recorded when the provider built it.
/// </summary>
/// <remarks>
/// A test that asserts what the operation produced reads this instead of running the operation again:
/// the exit code, the console output with its two streams kept apart, the lifecycle result's warnings,
/// findings and receipt, and the release requests the command made. The record is read-only; a test
/// never writes it or the install it belongs to.
/// </remarks>
public sealed record SquadInstallRecord
{
    /// <summary>The record for an install whose operation returned nothing worth reading.</summary>
    public static SquadInstallRecord Empty { get; } = new();

    /// <summary>The operation's exit code.</summary>
    public int ExitCode { get; init; }

    /// <summary>What the operation wrote to standard output.</summary>
    public string StandardOutput { get; init; } = string.Empty;

    /// <summary>What the operation wrote to standard error.</summary>
    public string StandardError { get; init; } = string.Empty;

    /// <summary>The warnings the lifecycle result carried after its filter.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>The findings the lifecycle result reported: its errors and degradations.</summary>
    public IReadOnlyList<string> Findings { get; init; } = [];

    /// <summary>The receipt the operation wrote, or null when it wrote none.</summary>
    public SquadReceipt? Receipt { get; init; }

    /// <summary>The receipt exactly as it was written, or null when the operation wrote none.</summary>
    public string? ReceiptText { get; init; }

    /// <summary>The release requests the operation made, in order.</summary>
    public IReadOnlyList<SquadReleaseRequest> ReleaseRequests { get; init; } = [];

    /// <summary>Records a lifecycle result, with an exit code and console streams the caller supplies.</summary>
    /// <param name="result">The lifecycle result the operation returned.</param>
    /// <param name="exitCode">The exit code the command returned.</param>
    /// <param name="standardOutput">The command's standard output.</param>
    /// <param name="standardError">The command's standard error.</param>
    /// <param name="receiptText">The receipt text the operation wrote, if the caller read it.</param>
    /// <param name="releaseRequests">The release requests the command made.</param>
    public static SquadInstallRecord FromLifecycle(
        SquadLifecycleResult result,
        int exitCode = 0,
        string standardOutput = "",
        string standardError = "",
        string? receiptText = null,
        IReadOnlyList<SquadReleaseRequest>? releaseRequests = null)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new SquadInstallRecord
        {
            ExitCode = exitCode,
            StandardOutput = standardOutput,
            StandardError = standardError,
            Warnings = result.Warnings ?? [],
            Findings = [.. result.Errors ?? [], .. (result.Degradations ?? []).Select(static d => $"{d.Target}/{d.Subject}/{d.Code}")],
            Receipt = result.Receipt,
            ReceiptText = receiptText,
            ReleaseRequests = releaseRequests ?? [],
        };
    }
}

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
    /// <param name="record">What the operation returned, or null when the caller recorded nothing.</param>
    internal PreparedSquadInstall(string key, TempDirectory directory, SquadInstallRecord? record = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(directory);
        Key = key;
        _directory = directory;
        Record = record ?? SquadInstallRecord.Empty;
    }

    /// <summary>The recipe this install answers.</summary>
    public string Key { get; }

    /// <summary>What the install, update or uninstall returned when the provider built it.</summary>
    public SquadInstallRecord Record { get; }

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
    private readonly record struct CacheKey(Type Type, string Key);

    private static readonly ConcurrentDictionary<CacheKey, Lazy<PreparedSquadInstall>> Installs = new();

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
    /// <param name="key">The recipe. Two callers with the same key and type share one install.</param>
    /// <param name="build">Makes the install. It runs once, under a lock, and must not return null.</param>
    /// <returns>The one install for that recipe.</returns>
    public static T Get<T>(string key, Func<T> build)
        where T : PreparedSquadInstall
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(build);

        CacheKey cacheKey = new(typeof(T), key);
        Lazy<PreparedSquadInstall> entry = Installs.GetOrAdd(
            cacheKey,
            static (ck, factory) => new Lazy<PreparedSquadInstall>(
                () =>
                {
                    PreparedSquadInstall install = factory()
                        ?? throw new InvalidOperationException($"Prepared install factory for '{ck.Key}' returned null.");
                    BuildCounts.AddOrUpdate(ck.Key, 1, static (_, count) => count + 1);
                    return install;
                },
                LazyThreadSafetyMode.ExecutionAndPublication),
            build);

        try
        {
            return (T)entry.Value;
        }
        catch
        {
            // A Lazy in ExecutionAndPublication mode caches its exception, so leaving this entry in place
            // would rethrow the one failure to every later request for the recipe, with no retry. Removing
            // this exact entry leaves one another thread has already stored in its place untouched.
            _ = Installs.TryRemove(new KeyValuePair<CacheKey, Lazy<PreparedSquadInstall>>(cacheKey, entry));
            throw;
        }
    }

    /// <summary>Removes every prepared install.</summary>
    public static void RemoveAll()
    {
        foreach (CacheKey cacheKey in Installs.Keys)
            Remove(cacheKey.Key);
    }

    /// <summary>Removes the prepared install for <paramref name="key"/>, if there is one.</summary>
    /// <param name="key">The recipe to remove. Nothing else is touched, so one test can remove its own install.</param>
    internal static void Remove(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        foreach (CacheKey cacheKey in Installs.Keys)
        {
            if (string.Equals(cacheKey.Key, key, StringComparison.Ordinal) &&
                Installs.TryRemove(cacheKey, out Lazy<PreparedSquadInstall>? entry) &&
                entry.IsValueCreated)
            {
                entry.Value.Dispose();
            }
        }

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

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".json", ".toml", ".yaml", ".yml", ".md", ".txt", ".sh", ".ps1",
        ".cmd", ".bat", ".cfg", ".conf", ".ini", ".xml", ".env", ".gitignore",
    };

    /// <summary>
    /// Copies a directory tree with <see cref="File.Copy"/>, preserving symbolic links and Unix file modes.
    /// </summary>
    /// <param name="source">The directory to copy from. It is only read.</param>
    /// <param name="destination">The directory to copy into. It is created if it is missing.</param>
    /// <param name="include">Filters a path relative to <paramref name="source"/>; null copies everything.</param>
    public static void Tree(string source, string destination, Func<string, bool>? include = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);

        _ = Directory.CreateDirectory(destination);
        CopyDirectoryContents(source, source, destination, include);
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

        ReRootTextFiles(stateDirectory, from, to);
    }

    /// <summary>
    /// Rewrites the template's recorded absolute root to the copy's root inside deployed files too, and
    /// records each rewritten file's new digest in the copied receipt.
    /// </summary>
    /// <remarks>
    /// A global deployment can put its own physical root into a deployed file, as the Copilot harness's
    /// absolute reference links do. A plain copy would then name the template's root, so the copy must
    /// rewrite those bytes and update the receipt that names them, or doctor reads the file as a hand edit.
    /// A multi-target deployment repeats relative paths under each target's own root, so
    /// <paramref name="target"/> keeps one target's digests from being recomputed from another's bytes.
    /// </remarks>
    /// <param name="deployedRoot">The copied target root holding the deployed files.</param>
    /// <param name="receiptPath">The copied receipt, or a path that does not exist when there is none.</param>
    /// <param name="store">The state store that serialises and deserialises the receipt.</param>
    /// <param name="from">The template's canonical root.</param>
    /// <param name="to">The copy's canonical root.</param>
    /// <param name="target">The receipt target whose digests to recompute, or null to recompute every entry.</param>
    /// <returns>The rewritten receipt, or null when there was none.</returns>
    public static SquadReceipt? ReRootDeployed(
        string deployedRoot,
        string receiptPath,
        SquadStateStore store,
        string from,
        string to,
        string? target = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deployedRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(receiptPath);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(from);
        ArgumentException.ThrowIfNullOrWhiteSpace(to);

        if (string.Equals(from, to, StringComparison.Ordinal))
            return File.Exists(receiptPath) ? store.DeserializeReceipt(File.ReadAllText(receiptPath)) : null;

        ReRootTextFiles(deployedRoot, from, to);

        if (!File.Exists(receiptPath))
            return null;

        SquadReceipt receipt = store.DeserializeReceipt(File.ReadAllText(receiptPath));
        receipt = receipt with
        {
            Files =
            [
                .. receipt.Files.Select(file =>
                {
                    if (target is not null && !string.Equals(file.Target, target, StringComparison.Ordinal))
                        return file;

                    string deployed = Path.Combine(deployedRoot, file.RelativePath);
                    return File.Exists(deployed)
                        ? file with { Sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(deployed))) }
                        : file;
                })
            ]
        };
        WriteTextAtomically(receiptPath, store.SerializeReceipt(receipt));
        return receipt;
    }

    private static void CopyDirectoryContents(
        string rootSource,
        string currentSource,
        string destination,
        Func<string, bool>? include)
    {
        foreach (FileSystemInfo entry in new DirectoryInfo(currentSource).EnumerateFileSystemInfos("*", SearchOption.TopDirectoryOnly))
        {
            string relative = Path.GetRelativePath(rootSource, entry.FullName);
            string target = Path.Combine(destination, relative);

            if (entry.LinkTarget is not null)
            {
                if (include is not null && !include(relative))
                    continue;

                _ = Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                DeleteExistingTarget(target);
                if (entry is DirectoryInfo)
                    _ = Directory.CreateSymbolicLink(target, entry.LinkTarget);
                else
                    _ = File.CreateSymbolicLink(target, entry.LinkTarget);

                continue;
            }

            if (entry is DirectoryInfo)
            {
                if (include is null || include(relative))
                    _ = Directory.CreateDirectory(target);

                CopyDirectoryContents(rootSource, entry.FullName, destination, include);
                continue;
            }

            if (include is not null && !include(relative))
                continue;

            _ = Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(entry.FullName, target, overwrite: true);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(target, File.GetUnixFileMode(entry.FullName));
        }
    }

    private static void DeleteExistingTarget(string target)
    {
        if (File.Exists(target))
        {
            File.Delete(target);
            return;
        }

        if (Directory.Exists(target))
            Directory.Delete(target, recursive: false);
    }

    private static void ReRootTextFiles(string directory, string from, string to)
    {
        foreach (string file in EnumerateOwnFiles(directory))
        {
            if (!TextExtensions.Contains(Path.GetExtension(file)))
                continue;

            string text = File.ReadAllText(file);
            if (TryReplaceRoot(text, from, to, out string updated))
                WriteTextAtomically(file, updated);
        }
    }

    /// <summary>
    /// Lists the files the directory owns: links, file and directory, are not its content, so neither is
    /// returned nor descended into. A copy keeps the template's links, and following one would rewrite a
    /// file outside the copy, visit one inode twice through the linked path, or never finish on a cycle.
    /// </summary>
    private static IEnumerable<string> EnumerateOwnFiles(string directory)
    {
        foreach (FileSystemInfo entry in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", SearchOption.TopDirectoryOnly))
        {
            if (entry.LinkTarget is not null)
                continue;

            if (entry is DirectoryInfo nested)
            {
                foreach (string file in EnumerateOwnFiles(nested.FullName))
                    yield return file;

                continue;
            }

            yield return entry.FullName;
        }
    }

    private static bool TryReplaceRoot(string text, string from, string to, out string updated)
    {
        int index = text.IndexOf(from, StringComparison.Ordinal);
        if (index < 0)
        {
            updated = text;
            return false;
        }

        bool endsWithSeparator = from[^1] is '/' or '\\';
        StringBuilder? builder = null;
        int cursor = 0;

        while (index >= 0)
        {
            int after = index + from.Length;
            bool isBoundary = endsWithSeparator
                || after >= text.Length
                || !(char.IsLetterOrDigit(text[after]) || text[after] is '-' or '_' or '.');

            if (isBoundary)
            {
                builder ??= new StringBuilder(text.Length);
                _ = builder.Append(text, cursor, index - cursor);
                _ = builder.Append(to);
                cursor = after;
            }

            index = text.IndexOf(from, after, StringComparison.Ordinal);
        }

        if (builder is null)
        {
            updated = text;
            return false;
        }

        _ = builder.Append(text, cursor, text.Length - cursor);
        updated = builder.ToString();
        return true;
    }

    private static void WriteTextAtomically(string path, string content)
    {
        string directory = Path.GetDirectoryName(path)!;
        string tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(tempPath, content);
            if (!OperatingSystem.IsWindows() && File.Exists(path))
                File.SetUnixFileMode(tempPath, File.GetUnixFileMode(path));

            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    /// <summary>The physical identity of <paramref name="path"/> as the product records it, existing or not.</summary>
    /// <param name="path">The path to canonicalise.</param>
    /// <returns>The canonical path.</returns>
    public static string Canonical(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return SquadPhysicalRootIdentities.CanonicalizeAllowingMissing(path, Semantics);
    }
}
