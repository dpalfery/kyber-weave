using System.ComponentModel;
using System.Diagnostics;
using KyberWeave.Contracts.Repositories;
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
public sealed class CodeGraphIndexStaleness
{
    private const int ExampleCap = 3;

    /// <summary>Allowance for filesystems that store modification times more coarsely than the index records them.</summary>
    private const long ClockSlackMilliseconds = 2000;

    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(20);

    private readonly IFileSystem _disk;
    private readonly IProcessRunner _git;

    /// <summary>One line describing the lag, or null when the index is current or git cannot say.</summary>
    /// <param name="disk">The file system the changed files' modification times are read from.</param>
    /// <param name="git">Runs <c>git</c> in the checkout without blocking a thread.</param>
    public CodeGraphIndexStaleness(IFileSystem disk, IProcessRunner git)
    {
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(git);
        _disk = disk;
        _git = git;
    }

    /// <summary>One line describing the lag, or null when the index is current or git cannot say.</summary>
    /// <param name="repositoryRoot">The checkout the index belongs to.</param>
    /// <param name="indexedCommit">The commit the index recorded, or null when it recorded none.</param>
    /// <param name="files">Each indexed path with its recorded modification time (ms) and symbol count.</param>
    public string? Describe(
        string repositoryRoot,
        string? indexedCommit,
        IReadOnlyDictionary<string, (long ModifiedMilliseconds, int NodeCount)> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (!TryPrepareIndexedCode(files, out HashSet<string> codeExtensions, out HashSet<string> indexedDirectories)) return null;
        return DescribeCore(
            repositoryRoot,
            indexedCommit,
            files,
            codeExtensions,
            indexedDirectories,
            ReadGitFacts(repositoryRoot, indexedCommit));
    }

    /// <summary>One line describing the lag without blocking a thread, or null when the index is current or git cannot say.</summary>
    /// <param name="repositoryRoot">The checkout the index belongs to.</param>
    /// <param name="indexedCommit">The commit the index recorded, or null when it recorded none.</param>
    /// <param name="files">Each indexed path with its recorded modification time (ms) and symbol count.</param>
    /// <param name="cancellationToken">Cancels the git reads.</param>
    public async Task<string?> DescribeAsync(
        string repositoryRoot,
        string? indexedCommit,
        IReadOnlyDictionary<string, (long ModifiedMilliseconds, int NodeCount)> files,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (!TryPrepareIndexedCode(files, out HashSet<string> codeExtensions, out HashSet<string> indexedDirectories)) return null;
        GitFacts facts = await ReadGitFactsAsync(repositoryRoot, indexedCommit, cancellationToken).ConfigureAwait(false);
        return DescribeCore(repositoryRoot, indexedCommit, files, codeExtensions, indexedDirectories, facts);
    }

    private static bool TryPrepareIndexedCode(
        IReadOnlyDictionary<string, (long ModifiedMilliseconds, int NodeCount)> files,
        out HashSet<string> codeExtensions,
        out HashSet<string> indexedDirectories)
    {
        codeExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string path, (long _, int nodes)) in files)
        {
            string extension = Path.GetExtension(path);
            if (nodes > 0 && extension.Length > 0) codeExtensions.Add(extension);
        }

        indexedDirectories = new HashSet<string>(StringComparer.Ordinal);
        if (codeExtensions.Count == 0) return false;

        // Where the index has code at all. A new file outside every such folder is one the index
        // skips on purpose (vendored, generated or ignored trees), and the index exposes no ignore
        // list of its own to say so.
        foreach ((string path, (long _, int nodes)) in files)
        {
            if (nodes > 0) indexedDirectories.Add(DirectoryOf(path));
        }

        return true;
    }

    private string? DescribeCore(
        string repositoryRoot,
        string? indexedCommit,
        IReadOnlyDictionary<string, (long ModifiedMilliseconds, int NodeCount)> files,
        IReadOnlySet<string> codeExtensions,
        IReadOnlySet<string> indexedDirectories,
        GitFacts facts)
    {
        if (facts.Head is null) return null;
        string head = facts.Head;

        string baseline = string.IsNullOrWhiteSpace(indexedCommit) ? head : indexedCommit.Trim();
        if (facts.Differing is null)
        {
            return $"the index was built at commit {Short(baseline)}, which this checkout does not contain";
        }

        List<string> changed = [];
        foreach (string line in (facts.Differing + "\n" + facts.Untracked).Split('\n', StringSplitOptions.RemoveEmptyEntries))
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

    private GitFacts ReadGitFacts(string repositoryRoot, string? indexedCommit)
    {
        string? head = Git(repositoryRoot, "rev-parse", "HEAD");
        return CompleteGitFacts(repositoryRoot, indexedCommit, head);
    }

    private async Task<GitFacts> ReadGitFactsAsync(
        string repositoryRoot,
        string? indexedCommit,
        CancellationToken cancellationToken)
    {
        string? head = await GitAsync(_git, repositoryRoot, ["rev-parse", "HEAD"], cancellationToken).ConfigureAwait(false);
        if (head is null) return new GitFacts(null, null, string.Empty);

        head = head.Trim();
        string baseline = string.IsNullOrWhiteSpace(indexedCommit) ? head : indexedCommit.Trim();
        string? differing = await GitAsync(
            _git,
            repositoryRoot,
            ["diff", "--name-only", "--no-renames", baseline],
            cancellationToken).ConfigureAwait(false);
        if (differing is null) return new GitFacts(head, null, string.Empty);

        string untracked = await GitAsync(
            _git,
            repositoryRoot,
            ["ls-files", "--others", "--exclude-standard"],
            cancellationToken).ConfigureAwait(false) ?? string.Empty;
        return new GitFacts(head, differing, untracked);
    }

    private GitFacts CompleteGitFacts(string repositoryRoot, string? indexedCommit, string? head)
    {
        if (head is null) return new GitFacts(null, null, string.Empty);

        head = head.Trim();
        string baseline = string.IsNullOrWhiteSpace(indexedCommit) ? head : indexedCommit.Trim();
        string? differing = Git(repositoryRoot, "diff", "--name-only", "--no-renames", baseline);
        if (differing is null) return new GitFacts(head, null, string.Empty);

        string untracked = Git(repositoryRoot, "ls-files", "--others", "--exclude-standard") ?? string.Empty;
        return new GitFacts(head, differing, untracked);
    }

    private bool IsChangedSinceIndex(
        string root,
        string relativePath,
        IReadOnlyDictionary<string, (long ModifiedMilliseconds, int NodeCount)> files,
        IReadOnlySet<string> indexedDirectories)
    {
        string full = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        bool indexed = files.TryGetValue(relativePath, out (long ModifiedMilliseconds, int NodeCount) recorded);

        if (!_disk.FileExists(full)) return indexed;
        if (!indexed) return IsUnderIndexedCode(relativePath, indexedDirectories);

        long onDisk = new DateTimeOffset(_disk.GetLastWriteTimeUtc(full)).ToUnixTimeMilliseconds();
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

    private string? Git(string root, params string[] arguments)
    {
        try
        {
            ProcessStartInfo startInfo = new("git")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = root
            };
            foreach (string argument in arguments)
                startInfo.ArgumentList.Add(argument);

            ProcessResult result = ProcessRunner.Run(startInfo, string.Empty, GitTimeout);
            return result.ExitCode == 0 ? result.StandardOutput : null;
        }
        catch (Exception exception) when (exception is Win32Exception
            or InvalidOperationException
            or TimeoutException)
        {
            return null;
        }
    }

    private static async Task<string?> GitAsync(
        IProcessRunner git,
        string root,
        string[] arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            ProcessResult result = await git.RunAsync(
                "git",
                arguments,
                root,
                GitTimeout,
                maxOutputBytes: null,
                cancellationToken).ConfigureAwait(false);
            return result.ExitCode == 0 ? result.StandardOutput : null;
        }
        catch (Exception exception) when (exception is Win32Exception
            or InvalidOperationException
            or TimeoutException)
        {
            return null;
        }
    }

    private readonly record struct GitFacts(string? Head, string? Differing, string Untracked);
}
