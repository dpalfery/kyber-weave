using System.Diagnostics;
using KyberWeave.Core.Processes;

namespace KyberWeave.Core.CodeGraph;

/// <summary>
/// Decides whether a CodeGraph index is older than the checkout it was built for.
/// </summary>
/// <remarks>
/// <para>
/// The index records the commit it was built at (<c>indexed_at_commit</c>) and, per file, the
/// modification time it saw. A file changed since the index is one that git reports as differing
/// from that commit, or that is untracked, and whose modification time on disk is newer than the
/// recorded one (or that the index has never seen). Comparing times rather than trusting the git
/// difference alone keeps a file that was already dirty when the index was built from counting
/// against it.
/// </para>
/// <para>
/// Only files of a kind that yielded symbols count. Documentation, configuration and data files
/// change constantly and say nothing about whether a symbol still resolves.
/// </para>
/// </remarks>
internal static class CodeGraphIndexStaleness
{
    private const int ExampleCap = 3;

    /// <summary>Allowance for filesystems that store modification times more coarsely than the index records them.</summary>
    private const long ClockSlackMilliseconds = 2000;

    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(20);

    /// <summary>One line describing the lag, or null when the index is current or git cannot say.</summary>
    /// <param name="repositoryRoot">The checkout the index belongs to.</param>
    /// <param name="indexedCommit">The commit the index recorded, or null when it recorded none.</param>
    /// <param name="files">Each indexed path with its recorded modification time (ms) and symbol count.</param>
    public static string? Describe(
        string repositoryRoot,
        string? indexedCommit,
        IReadOnlyDictionary<string, (long ModifiedMilliseconds, int NodeCount)> files)
    {
        HashSet<string> codeExtensions = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string path, (long _, int nodes)) in files)
        {
            string extension = Path.GetExtension(path);
            if (nodes > 0 && extension.Length > 0) codeExtensions.Add(extension);
        }

        if (codeExtensions.Count == 0) return null;

        // Where the index has code at all. A new file outside every such folder is one the index
        // skips on purpose (vendored, generated or ignored trees), and the index exposes no ignore
        // list of its own to say so.
        HashSet<string> indexedDirectories = new(StringComparer.Ordinal);
        foreach ((string path, (long _, int nodes)) in files)
        {
            if (nodes > 0) indexedDirectories.Add(DirectoryOf(path));
        }

        string? head = Git(repositoryRoot, "rev-parse", "HEAD");
        if (head is null) return null;
        head = head.Trim();

        string baseline = string.IsNullOrWhiteSpace(indexedCommit) ? head : indexedCommit.Trim();
        string? differing = Git(repositoryRoot, "diff", "--name-only", "--no-renames", baseline);
        if (differing is null)
        {
            return $"the index was built at commit {Short(baseline)}, which this checkout does not contain";
        }

        string untracked = Git(repositoryRoot, "ls-files", "--others", "--exclude-standard") ?? string.Empty;

        List<string> changed = [];
        foreach (string line in (differing + "\n" + untracked).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string path = line.Trim();
            if (path.Length == 0 || !codeExtensions.Contains(Path.GetExtension(path))) continue;
            if (IsChangedSinceIndex(repositoryRoot, path, files, indexedDirectories)) changed.Add(path);
        }

        if (changed.Count == 0) return null;

        string examples = string.Join(", ", changed.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(ExampleCap));
        string built = string.Equals(baseline, head, StringComparison.Ordinal)
            ? $"the index was built at the current commit {Short(head)}"
            : $"the index was built at {Short(baseline)} and HEAD is {Short(head)}";
        return $"{built}, and {changed.Distinct(StringComparer.Ordinal).Count()} source file(s) changed since (for example {examples})";
    }

    private static bool IsChangedSinceIndex(
        string root,
        string relativePath,
        IReadOnlyDictionary<string, (long ModifiedMilliseconds, int NodeCount)> files,
        IReadOnlySet<string> indexedDirectories)
    {
        string full = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        bool indexed = files.TryGetValue(relativePath, out (long ModifiedMilliseconds, int NodeCount) recorded);

        if (!File.Exists(full)) return indexed;
        if (!indexed) return IsUnderIndexedCode(relativePath, indexedDirectories);

        long onDisk = new DateTimeOffset(File.GetLastWriteTimeUtc(full)).ToUnixTimeMilliseconds();
        return onDisk > recorded.ModifiedMilliseconds + ClockSlackMilliseconds;
    }

    private static string DirectoryOf(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : path[..slash];
    }

    /// <summary>True when the file sits in a folder the index holds code for, or beneath one.</summary>
    private static bool IsUnderIndexedCode(string path, IReadOnlySet<string> indexedDirectories)
    {
        string directory = DirectoryOf(path);
        while (true)
        {
            if (indexedDirectories.Contains(directory)) return true;
            if (directory.Length == 0) return false;
            directory = DirectoryOf(directory);
            // The repository root counts only for files directly in it: every file is beneath it.
            if (directory.Length == 0) return false;
        }
    }

    private static string Short(string commit) => commit.Length > 8 ? commit[..8] : commit;

    private static string? Git(string root, params string[] arguments)
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
        foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);

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
}
