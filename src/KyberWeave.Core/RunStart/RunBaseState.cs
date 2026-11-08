using System.Text.Json;

namespace KyberWeave.Core.RunStart;

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
    /// created from, replacing any earlier record.
    /// </summary>
    public static void Write(string homeDirectory, string branch, string baseRef)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseRef);

        string path = StatePath(homeDirectory, branch);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new StateDocument(baseRef), JsonOptions));
    }

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
    /// takes become dashes, so the key never escapes the runs directory.
    /// </summary>
    private static string KeyOf(string branch)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        return string.Concat(branch.Select(character =>
            character == '/' || character == '\\' || Array.IndexOf(invalid, character) >= 0 ? '-' : character));
    }

    private sealed record StateDocument(string BaseRef);
}
