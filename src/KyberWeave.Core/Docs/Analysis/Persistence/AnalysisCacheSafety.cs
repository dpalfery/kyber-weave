using System.ComponentModel;
using System.Diagnostics;
using System.IO.Enumeration;
using KyberWeave.Core.Processes;

namespace KyberWeave.Core.Docs.Analysis.Persistence;

/// <summary>Checks whether local analysis state is protected from accidental commits.</summary>
public static class AnalysisCacheSafety
{
    private const string CacheIgnoreEntry = "cache/";

    /// <summary>
    /// Returns true only when the repository-owned state ignore file contains the narrow
    /// cache-directory entry used by Kyber-Weave.
    /// </summary>
    public static bool IsSafe(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);

        string ignorePath = Path.Combine(
            Path.GetFullPath(repositoryRoot),
            ".kyber-weave",
            ".gitignore");
        try
        {
            return File.Exists(ignorePath)
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

    private static bool HasEffectiveCacheIgnore(string ignorePath)
    {
        bool protectedByExactEntry = false;
        foreach (string line in File.ReadLines(ignorePath))
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

    private static bool HasTrackedCacheEntry(string repositoryRoot)
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
        startInfo.ArgumentList.Add(".kyber-weave/cache");

        try
        {
            ProcessResult result = ProcessRunner.Run(startInfo, string.Empty);
            return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StandardOutput);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            // Without git, persistence cannot prove that the cache is outside the index.
            return true;
        }
    }
}
