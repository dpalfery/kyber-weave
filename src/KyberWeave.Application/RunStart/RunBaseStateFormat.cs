using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Core.RunStart;

/// <summary>The record <c>run start</c> keeps for each branch: where it lives, how it is keyed and what it contains.</summary>
/// <remarks>
/// One file per branch under <c>&lt;home&gt;/.kyber-weave/runs</c>, refreshed by every <c>run start</c>: a rebranched
/// branch never reads a stale base, and worktrees on one branch share the record because the key is the branch name,
/// not the path.
/// </remarks>
public static class RunBaseStateFormat
{
    /// <summary>The extension every record file carries.</summary>
    public const string StateExtension = ".json";

    private const string StateDirectoryName = ".kyber-weave";
    private const string RunsDirectoryName = "runs";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>The directory that holds every record under an operator's home.</summary>
    /// <param name="homeDirectory">The operator's home.</param>
    public static string RunsDirectory(string homeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);

        return Path.Combine(homeDirectory, StateDirectoryName, RunsDirectoryName);
    }

    /// <summary>The state file holding <paramref name="branch" />'s recorded base.</summary>
    public static string StatePath(string homeDirectory, string branch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);

        return Path.Combine(homeDirectory, StateDirectoryName, RunsDirectoryName, KeyOf(branch) + StateExtension);
    }

    /// <summary>How two paths are compared: exactly on Linux, ignoring case elsewhere.</summary>
    /// <param name="environment">The platform the comparison is made on.</param>
    public static StringComparison PathComparison(IProcessEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        return environment.OperatingSystem == OperatingSystemKind.Linux
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
    }

    /// <summary>A directory path in the form two records of one repository compare equal in.</summary>
    /// <param name="directory">The directory; a relative one is taken against the environment's current directory.</param>
    /// <param name="paths">Resolves the full path.</param>
    /// <param name="environment">Supplies the current directory a relative path is resolved against.</param>
    public static string NormalizeDirectory(string directory, IPathResolver paths, IProcessEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(environment);

        return paths.GetFullPath(directory, environment.CurrentDirectory).Replace('\\', '/').TrimEnd('/');
    }

    /// <summary>Renders the record for a branch.</summary>
    /// <param name="baseRef">The base the branch was created from.</param>
    /// <param name="branch">The branch.</param>
    /// <param name="place">Where the run works, when known.</param>
    public static string Serialize(string baseRef, string branch, RunStartPlace? place) =>
        JsonSerializer.Serialize(
            new StateDocument(baseRef, place?.Repository, branch, place?.Worktree, place?.StartedAt, place?.Autonomy),
            JsonOptions);

    /// <summary>Reads the base out of a record.</summary>
    /// <param name="json">The record's text.</param>
    /// <param name="baseRef">The recorded base.</param>
    /// <returns>False when the record is corrupt or names no base.</returns>
    public static bool TryReadBaseRef(string json, out string? baseRef)
    {
        baseRef = null;
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

    /// <summary>Reads the place a record names.</summary>
    /// <param name="json">The record's text.</param>
    /// <param name="record">The repository, branch and worktree.</param>
    /// <returns>False when the record is corrupt or one of the three is missing: records written before <c>run start</c> recorded its place are skipped.</returns>
    public static bool TryReadRecord(string json, out RunRecord? record)
    {
        record = null;
        StateDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<StateDocument>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return false;
        }

        if (document is { Repository: { Length: > 0 } repository, Branch: { Length: > 0 } branch, Worktree: { Length: > 0 } worktree })
        {
            record = new RunRecord(repository, branch, worktree, document.StartedAt, document.Autonomy);
            return true;
        }

        return false;
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
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(branch));
        return flat + "-" + Convert.ToHexStringLower(hash)[..8];
    }

    private sealed record StateDocument(
        string BaseRef,
        string? Repository = null,
        string? Branch = null,
        string? Worktree = null,
        DateTimeOffset? StartedAt = null,
        string? Autonomy = null);
}
