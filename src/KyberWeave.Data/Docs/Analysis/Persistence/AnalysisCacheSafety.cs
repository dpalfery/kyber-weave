using KyberWeave.Core.FileSystem;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Enumeration;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Processes;

namespace KyberWeave.Core.Docs.Analysis.Persistence;

/// <summary>Checks whether local analysis state is protected from accidental commits.</summary>
public sealed class AnalysisCacheSafety
{
    private const string CacheIgnoreEntry = "cache/";

    /// <summary>
    ///     The bound for <c>git ls-files</c> on the cache path. A healthy index answers in well under a second.
    /// </summary>
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(60);

    private readonly IFileSystem _files;
    private readonly IPathResolver _paths;
    private readonly IProcessEnvironment _environment;
    private readonly IBoundedProcessExecutor _processes;
    private readonly IProcessRunner? _processesAsync;

    /// <summary>Checks whether local analysis state is protected from accidental commits.</summary>
    /// <param name="files">Reads the repository's state ignore file.</param>
    /// <param name="paths">Resolves the repository root to a full path.</param>
    /// <param name="environment">Supplies the directory a relative root resolves against.</param>
    /// <param name="processes">Runs <c>git ls-files</c> under a time limit.</param>
    public AnalysisCacheSafety(
        IFileSystem files,
        IPathResolver paths,
        IProcessEnvironment environment,
        IBoundedProcessExecutor processes)
        : this(files, paths, environment, processes, processes as IProcessRunner)
    {
    }

    /// <summary>Checks whether local analysis state is protected from accidental commits, reading git without blocking.</summary>
    /// <param name="files">Reads the repository's state ignore file.</param>
    /// <param name="paths">Resolves the repository root to a full path.</param>
    /// <param name="environment">Supplies the directory a relative root resolves against.</param>
    /// <param name="processes">Runs <c>git ls-files</c> under a time limit without blocking a thread.</param>
    public AnalysisCacheSafety(
        IFileSystem files,
        IPathResolver paths,
        IProcessEnvironment environment,
        IProcessRunner processes)
        : this(files, paths, environment, processes, processes)
    {
    }

    private AnalysisCacheSafety(
        IFileSystem files,
        IPathResolver paths,
        IProcessEnvironment environment,
        IBoundedProcessExecutor processes,
        IProcessRunner? processesAsync)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(processes);
        _files = files;
        _paths = paths;
        _environment = environment;
        _processes = processes;
        _processesAsync = processesAsync;
    }

    /// <summary>
    /// Returns true only when the repository-owned state ignore file contains the narrow
    /// cache-directory entry used by Kyber-Weave.
    /// </summary>
    public bool IsSafe(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);

        string ignorePath = IgnorePathFor(repositoryRoot);
        try
        {
            return _files.FileExists(ignorePath)
                && HasEffectiveCacheIgnore(ignorePath)
                && !HasTrackedCacheEntry(repositoryRoot);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Returns true only when the repository-owned state ignore file contains the narrow
    /// cache-directory entry used by Kyber-Weave, reading git without blocking a thread.
    /// </summary>
    /// <param name="repositoryRoot">The repository root.</param>
    /// <param name="cancellationToken">Cancels the git read.</param>
    public async Task<bool> IsSafeAsync(string repositoryRoot, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);

        string ignorePath = IgnorePathFor(repositoryRoot);
        try
        {
            return _files.FileExists(ignorePath)
                && HasEffectiveCacheIgnore(ignorePath)
                && !await HasTrackedCacheEntryAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private string IgnorePathFor(string repositoryRoot) =>
        Path.Combine(
            _paths.GetFullPath(repositoryRoot, _environment.CurrentDirectory),
            UserStatePaths.DirectoryName,
            ".gitignore");

    private bool HasEffectiveCacheIgnore(string ignorePath)
    {
        bool protectedByExactEntry = false;
        foreach (string line in _files.ReadLines(ignorePath))
        {
            if (StringComparer.Ordinal.Equals(line, CacheIgnoreEntry))
            {
                protectedByExactEntry = true;
                continue;
            }

            if (protectedByExactEntry
                && line.StartsWith('!')
                && NegatesCacheProtection(line[1..]))
            {
                protectedByExactEntry = false;
            }
        }

        return protectedByExactEntry;
    }

    private static bool NegatesCacheProtection(string pattern)
    {
        if (pattern.StartsWith('/')) pattern = pattern[1..];
        if (pattern.Length == 0 || pattern.StartsWith('#')) return false;

        const string databaseRelativePath = "cache/docs-analysis.sqlite3";
        if (pattern.EndsWith('/'))
            return databaseRelativePath.StartsWith(pattern, StringComparison.Ordinal);

        // CA1847 suggests Contains(char) for single-char search, but CA1307 requires StringComparison.Ordinal which is only available on the string overload
#pragma warning disable CA1847
        if (!pattern.Contains("/", StringComparison.Ordinal))
#pragma warning restore CA1847
        {
            return FileSystemName.MatchesSimpleExpression(
                pattern,
                Path.GetFileName(databaseRelativePath),
                ignoreCase: OperatingSystem.IsWindows());
        }

        // SimpleExpression covers the glob forms used by gitignore for this fixed target.
        // Character classes and `**` are treated conservatively because collapsing `**` to
        // `*` under-matches git's recursive semantics, and accepting an uncertain negation
        // would make a cache appear safer than it is.
#pragma warning disable CA1847 // Single-char Contains with StringComparison requires string overload to satisfy CA1307
        return pattern.Contains("[", StringComparison.Ordinal) || pattern.Contains("**", StringComparison.Ordinal)
            ? pattern.Contains("cache", StringComparison.Ordinal)
            : FileSystemName.MatchesSimpleExpression(
                pattern,
                databaseRelativePath,
                ignoreCase: OperatingSystem.IsWindows());
#pragma warning restore CA1847
    }

    private bool HasTrackedCacheEntry(string repositoryRoot)
    {
        ProcessStartInfo startInfo = GitStartInfo(repositoryRoot);

        try
        {
            ProcessResult result = _processes.Run(startInfo, string.Empty, GitTimeout);
            return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StandardOutput);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or TimeoutException)
        {
            // Without git, or without an answer from it, persistence cannot prove that the cache is outside the index.
            return true;
        }
    }

    private async Task<bool> HasTrackedCacheEntryAsync(string repositoryRoot, CancellationToken cancellationToken)
    {
        // Built over a bounded-only executor there is no async runner: answer as the sync twin does when git
        // cannot answer (the cache cannot be proved untracked, so it is treated as unsafe), never throw.
        if (_processesAsync is not { } processesAsync) return true;
        ProcessStartInfo startInfo = GitStartInfo(repositoryRoot);

        try
        {
            ProcessResult result = await processesAsync.RunAsync(
                startInfo,
                string.Empty,
                GitTimeout,
                cancellationToken).ConfigureAwait(false);
            return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StandardOutput);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or TimeoutException)
        {
            // Without git, or without an answer from it, persistence cannot prove that the cache is outside the index.
            return true;
        }
    }

    private static ProcessStartInfo GitStartInfo(string repositoryRoot)
    {
        ProcessStartInfo startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = repositoryRoot,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("ls-files");
        startInfo.ArgumentList.Add("--cached");
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add(UserStatePaths.AnalysisCacheRelativeDirectory);
        return startInfo;
    }
}
