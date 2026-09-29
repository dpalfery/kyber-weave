using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using KyberWeave.Core.Processes;

namespace KyberWeave.Mcp;

/// <summary>
/// Formats the provenance header every docs tool leads with: the absolute corpus root, the
/// short HEAD revision with a dirty marker, and the live document count.
/// </summary>
/// <remarks>
/// <para>
/// The header is the tell that a same-named server is bound to a different checkout. A
/// mis-rooted process otherwise answers with the same confident prose as a correct one, so
/// the root and revision have to be visible on every response rather than inferred from a
/// document count alone (issue #162).
/// </para>
/// <para>
/// Git identity is read through <see cref="ProcessRunner"/> and never invented: a missing
/// git, a directory that is not a repository, or a failed command all yield
/// <c>rev=unavailable</c> and <c>dirty=unknown</c>, which are success paths for the header.
/// The values move only when the corpus does, so they are cached per root until the host's
/// corpus stamp changes rather than shelling out on every tool call.
/// </para>
/// </remarks>
internal static class CorpusProvenance
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(5);

    private static readonly ConcurrentDictionary<string, CachedIdentity> Cache =
        new(StringComparer.Ordinal);

    /// <summary>The one-line provenance header for a response over <paramref name="documentCount"/> documents.</summary>
    /// <param name="repositoryRoot">The bound repository root; normalised with <see cref="Path.GetFullPath(string)"/>.</param>
    /// <param name="documentCount">The live document count for the corpus.</param>
    /// <param name="corpusStamp">The host's corpus stamp, used to expire the cached git identity.</param>
    public static string Line(string repositoryRoot, int documentCount, int corpusStamp)
    {
        string root = Path.GetFullPath(repositoryRoot);
        (string revision, string dirty) = Identity(root, corpusStamp);
        return "provenance: root=" + root
            + " rev=" + revision
            + " dirty=" + dirty
            + " documents=" + documentCount.ToString(CultureInfo.InvariantCulture);
    }

    private static (string Revision, string Dirty) Identity(string root, int corpusStamp)
    {
        if (Cache.TryGetValue(root, out CachedIdentity cached) && cached.CorpusStamp == corpusStamp)
        {
            return (cached.Revision, cached.Dirty);
        }

        (string revision, string dirty) = ReadGitIdentity(root);
        Cache[root] = new CachedIdentity(corpusStamp, revision, dirty);
        return (revision, dirty);
    }

    private static (string Revision, string Dirty) ReadGitIdentity(string root)
    {
        string? revision = RunGit(root, "rev-parse", "--short", "HEAD");
        string? status = RunGit(root, "status", "--porcelain", "--untracked-files=no");
        string dirty = status is null
            ? "unknown"
            : status.Trim().Length == 0 ? "no" : "yes";
        return (revision is null ? "unavailable" : revision.Trim(), dirty);
    }

    private static string? RunGit(string root, params string[] arguments)
    {
        ProcessStartInfo startInfo = new("git")
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            ProcessResult result = ProcessRunner.Run(startInfo, string.Empty, GitTimeout);
            return result.ExitCode == 0 ? result.StandardOutput : null;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
            or InvalidOperationException
            or TimeoutException)
        {
            return null;
        }
    }

    private readonly record struct CachedIdentity(int CorpusStamp, string Revision, string Dirty);
}
