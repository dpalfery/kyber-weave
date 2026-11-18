using System.Security.Cryptography;
using System.Text;

namespace KyberWeave.Domain.Doctor;

/// <summary>The pure rules of CodeGraph index preparation: where its lock lives, how a path is quoted, and how a failure is read.</summary>
public static class CodeGraphIndexRules
{
    /// <summary>The directory name CodeGraph keeps its index in, at the repository root.</summary>
    public const string IndexDirectoryName = ".codegraph";

    /// <summary>
    ///     The tracked files <c>codegraph init</c> writes, repository-relative with forward slashes, in ordinal order.
    /// </summary>
    /// <remarks>
    ///     Measured on a fresh clone: <c>codegraph init .</c> changed no tracked file and created no untracked file other
    ///     than the git-ignored <c>.codegraph/</c>, so the list is empty. It is kept as a named list so that a future
    ///     CodeGraph release which writes a tracked file is restored by name rather than by guess.
    /// </remarks>
    public static IReadOnlyList<string> InitConfigFiles { get; } = [];

    /// <summary>Returns the lock path for a root: a file in <paramref name="lockDirectory"/> named by a hash of the root.</summary>
    /// <param name="root">The repository root.</param>
    /// <param name="lockDirectory">The directory that holds lock files, normally the OS temp directory.</param>
    public static string LockPathFor(string root, string lockDirectory)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(Normalise(root)));
        string key = Convert.ToHexString(digest, 0, 8).ToUpperInvariant();
        return Path.Combine(lockDirectory, $"kyber-weave-codegraph-{key}.lock");
    }

    /// <summary>Quotes a path for the shell of the given platform, only when it needs it.</summary>
    /// <param name="path">The path.</param>
    /// <param name="isWindows">Whether the shell is a Windows one.</param>
    public static string QuoteShellPath(string path, bool isWindows)
    {
        if (!NeedsQuoting(path))
            return path;

        return isWindows
            ? "\"" + path.Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
            : "'" + path.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    }

    /// <summary>Whether a doctor check reports that the CodeGraph index was busy provisioning.</summary>
    /// <param name="check">The check.</param>
    public static bool IsBusy(DoctorCheck check) =>
        check.Reason?.StartsWith("codegraph.index busy", StringComparison.Ordinal) == true;

    /// <summary>
    ///     How long the docs server waits before each further look at CodeGraph's lock file, when a refresh finds
    ///     the file present. The owner set the schedule: 1, 5, then 30 seconds, and a failure after the last look.
    /// </summary>
    public static IReadOnlyList<TimeSpan> LockFileWaits { get; } =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30)];

    /// <summary>Returns CodeGraph's own lock file for a repository root: <c>.codegraph/codegraph.lock</c>.</summary>
    /// <param name="root">The repository root.</param>
    public static string IndexLockPathFor(string root) =>
        Path.Combine(root, IndexDirectoryName, CodeGraphIndexFiles.IndexLockFileName);

    /// <summary>
    ///     Whether a failed check reports that <c>codegraph sync</c> found CodeGraph's own index lock held by another
    ///     process: its server, its file watcher, or another sync. The index on disk is then usable, only busy.
    /// </summary>
    /// <remarks>
    ///     CodeGraph 1.6.2 exits 1 for every sync failure and prints no code or marker for this one, so the only
    ///     signal is its message. The phrase matched is the one its sync prints when it cannot take
    ///     <c>.codegraph/codegraph.lock</c>, whichever process holds it. It sits at the start of the first error
    ///     line, so the 160-character cut the check's reason is given cannot remove it. Its two "rebuild in
    ///     progress" messages are deliberately not matched: the database file is being replaced then, so the index
    ///     on disk cannot be answered from.
    /// </remarks>
    /// <param name="check">The <c>codegraph.index</c> check a provisioning run returned.</param>
    public static bool IsIndexLockHeld(DoctorCheck check) =>
        !check.Passed
        && check.Reason is { } reason
        && reason.StartsWith("codegraph sync exited ", StringComparison.Ordinal)
        && reason.Contains("Sync could not acquire the file lock", StringComparison.Ordinal);

    private static bool NeedsQuoting(string path) =>
        path.Length == 0 || path.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '/' && c != '\\' && c != '.' && c != '-' && c != '_');

    private static string Normalise(string root) =>
        Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
