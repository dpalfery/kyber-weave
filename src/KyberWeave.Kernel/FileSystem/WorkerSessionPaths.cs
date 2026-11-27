using System.Security.Cryptography;
using System.Text;

namespace KyberWeave.Core.FileSystem;

/// <summary>
/// The Factory session ledger's keys and file names under <c>~/.kyber-weave/arbiter/sessions</c>, and the one-file
/// test the guard's entry uses before it builds anything.
/// </summary>
/// <remarks>
/// The path rule is written here once. The store that writes the ledger composes its files from these members, so a
/// reader can never look for a file the writer spelled differently, and the entry that decides whether a Factory call
/// is worth a container asks <see cref="ChildEntryExists" /> rather than restating the path.
/// </remarks>
public static class WorkerSessionPaths
{
    private const string ChildSuffix = ".session";
    private const string PendingSuffix = ".list";
    private const string PendingPrefix = "pending-";

    /// <summary>The key a harness's worker file is named under: the first 24 hex characters of the SHA-256 of the harness and the id.</summary>
    /// <param name="harness">The harness token.</param>
    /// <param name="agentId">The subagent's or session's id.</param>
    /// <returns>A lower-case hex key that is safe as a file name.</returns>
    public static string KeyOf(string harness, string agentId) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(harness + "\n" + agentId)))[..24];

    /// <summary>The directory the ledger's files sit in.</summary>
    /// <param name="home">The operator's home.</param>
    /// <returns>The absolute directory path.</returns>
    public static string DirectoryOf(string home) => UserStatePaths.WorkerSessionsDirectory(home);

    /// <summary>The file one child session is recorded in.</summary>
    /// <param name="home">The operator's home.</param>
    /// <param name="key">The child's key.</param>
    /// <returns>The absolute file path.</returns>
    public static string ChildPathOf(string home, string key) => Path.Combine(DirectoryOf(home), key + ChildSuffix);

    /// <summary>The key one parent's in-flight list is recorded under.</summary>
    /// <param name="harness">The harness token.</param>
    /// <param name="parentSessionId">The dispatching session's id.</param>
    /// <returns>The list's key.</returns>
    public static string PendingKeyOf(string harness, string parentSessionId) => PendingPrefix + KeyOf(harness, parentSessionId);

    /// <summary>The file one parent's in-flight list is recorded in.</summary>
    /// <param name="home">The operator's home.</param>
    /// <param name="key">The parent's pending key.</param>
    /// <returns>The absolute file path.</returns>
    public static string PendingPathOf(string home, string key) => Path.Combine(DirectoryOf(home), key + PendingSuffix);

    /// <summary>
    /// Whether this session has a child entry in the ledger, answered by a plain file-existence test so a root
    /// session's tool call costs no container and no ledger read.
    /// </summary>
    /// <param name="home">The operator's home.</param>
    /// <param name="harness">The harness token.</param>
    /// <param name="sessionId">The session id the event names.</param>
    /// <returns>True when the session was recorded as a child.</returns>
    public static bool ChildEntryExists(string home, string harness, string sessionId) =>
        File.Exists(ChildPathOf(home, KeyOf(harness, sessionId)));
}
