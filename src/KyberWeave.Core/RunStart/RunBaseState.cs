using System.Text.Json;
using KyberWeave.Core.Arbiter.Facts;

namespace KyberWeave.Core.RunStart;

/// <summary>Where a run started: the facts <c>run start</c> records beside the base, read back by the dispatch guard.</summary>
/// <param name="Repository">The repository's common git directory, absolute.</param>
/// <param name="Worktree">The directory the run works in: its worktree, or the checkout it branched in place.</param>
/// <param name="StartedAt">When the run started.</param>
public sealed record RunStartPlace(string Repository, string Worktree, DateTimeOffset StartedAt);

/// <summary>
/// The base a run branched from, kept where a later dispatch hook can read it: a
/// branch-keyed file under the operator's home. A hook process sees a prompt and a
/// working directory but no PR metadata, so without this record its only base is the
/// <c>origin/HEAD</c> guess — wrong on a stacked branch, silent when unresolvable.
/// </summary>
/// <remarks>
/// One file per branch, refreshed by every <c>run start</c>: a rebranched branch never
/// reads a stale base, and worktrees on one branch share the record because the key is
/// the branch name, not the path.
/// </remarks>
public static class RunBaseState
{
    private const string StateDirectoryName = ".kyber-weave";
    private const string RunsDirectoryName = "runs";
    private const string StateExtension = ".json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>The state file holding <paramref name="branch" />'s recorded base.</summary>
    public static string StatePath(string homeDirectory, string branch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);

        return Path.Combine(homeDirectory, StateDirectoryName, RunsDirectoryName, KeyOf(branch) + StateExtension);
    }

    /// <summary>
    /// Records <paramref name="baseRef" /> as the base <paramref name="branch" /> was
    /// created from, replacing any earlier record. <paramref name="place" />, when given,
    /// records where the run works, which the dispatch guard reads (<see cref="HasLiveRun" />).
    /// </summary>
    public static void Write(string homeDirectory, string branch, string baseRef, RunStartPlace? place = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseRef);

        string path = StatePath(homeDirectory, branch);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        StateDocument document = new(baseRef, place?.Repository, branch, place?.Worktree, place?.StartedAt);
        File.WriteAllText(path, JsonSerializer.Serialize(document, JsonOptions));
    }

    /// <summary>
    /// Whether a run is live for <paramref name="repository" />: a record names that repository, and its
    /// worktree still exists with the run's branch checked out. Both halves are machine facts: the record
    /// <c>run start</c> wrote, and git's answer for the worktree.
    /// </summary>
    /// <remarks>
    /// A record written before <c>run start</c> recorded its place names no repository and is skipped: it
    /// predates this version, so it cannot be the run in progress. A run left live by an earlier session
    /// also counts, because nothing on disk ties a run to a harness session.
    /// </remarks>
    /// <param name="homeDirectory">The operator's home, which holds <c>.kyber-weave/runs</c>.</param>
    /// <param name="repository">The repository's common git directory.</param>
    /// <returns>True or false when the records were read; null when the runs directory could not be read.</returns>
    public static bool? HasLiveRun(string homeDirectory, string repository)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);

        string directory = Path.Combine(homeDirectory, StateDirectoryName, RunsDirectoryName);
        string[] files;
        try
        {
            if (!Directory.Exists(directory))
            {
                return false;
            }

            files = Directory.GetFiles(directory, "*" + StateExtension);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        string wanted = NormalizeDirectory(repository);
        foreach (string file in files)
        {
            StateDocument? document;
            try
            {
                document = JsonSerializer.Deserialize<StateDocument>(File.ReadAllText(file), JsonOptions);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }
            catch (JsonException)
            {
                continue;
            }

            if (document is { Repository: { Length: > 0 } recorded, Branch: { Length: > 0 } branch, Worktree: { Length: > 0 } worktree }
                && string.Equals(NormalizeDirectory(recorded), wanted, PathComparison)
                && Directory.Exists(worktree)
                && string.Equals(GitFacts.GetCurrentBranch(worktree), branch, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    private static string NormalizeDirectory(string directory) =>
        Path.GetFullPath(directory).Replace('\\', '/').TrimEnd('/');

    /// <summary>
    /// Reads the base <paramref name="branch" /> was created from. Absent, unreadable, or
    /// corrupt state reads <c>false</c> rather than throwing: the caller falls back to its
    /// default base.
    /// </summary>
    public static bool TryReadBaseRef(string homeDirectory, string branch, out string? baseRef)
    {
        baseRef = null;
        string path;
        try
        {
            path = StatePath(homeDirectory, branch);
        }
        catch (ArgumentException)
        {
            return false;
        }

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        StateDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<StateDocument>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return false;
        }

        if (document is null || string.IsNullOrWhiteSpace(document.BaseRef))
        {
            return false;
        }

        baseRef = document.BaseRef;
        return true;
    }

    /// <summary>
    /// Flattens a branch name to one file name: separators and characters no file system
    /// takes become dashes, with a hash of the full name appended, so distinct branches
    /// never share a key and the key never escapes the runs directory.
    /// </summary>
    private static string KeyOf(string branch)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string flat = string.Concat(branch.Select(character =>
            character == '/' || character == '\\' || Array.IndexOf(invalid, character) >= 0 ? '-' : character));
        byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(branch));
        return flat + "-" + Convert.ToHexStringLower(hash)[..8];
    }

    private sealed record StateDocument(
        string BaseRef,
        string? Repository = null,
        string? Branch = null,
        string? Worktree = null,
        DateTimeOffset? StartedAt = null);
}
