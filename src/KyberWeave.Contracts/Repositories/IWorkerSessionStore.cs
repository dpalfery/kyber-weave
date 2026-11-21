using KyberWeave.Core.Arbiter;

namespace KyberWeave.Contracts.Repositories;

/// <summary>
/// The Factory session ledger under <c>~/.kyber-weave/arbiter/sessions</c>: one file per child session mapping it to
/// the parent that dispatched it and the Squad agent type it runs as, plus one append-only in-flight list per parent.
/// </summary>
/// <remarks>
/// The budget files' pattern, one append-only file per key, is reused so parallel hook processes never lose an entry:
/// a child's file is a <c>parent</c> line and then type lines, and the last authoritative <c>type</c> line is the one
/// that stands. The ledger exists because a Factory tool-call payload names no calling sub-agent: the root's
/// <c>Task</c> dispatch, the child's <c>SessionStart</c> and the dispatch's <c>PostToolUse</c> text are joined here.
/// </remarks>
public interface IWorkerSessionStore
{
    /// <summary>Creates the ledger directory when it is missing.</summary>
    /// <param name="home">The operator's home.</param>
    void EnsureDirectory(string home);

    /// <summary>Whether a child session has a ledger entry.</summary>
    /// <param name="home">The operator's home.</param>
    /// <param name="key">The child session's key.</param>
    bool Exists(string home, string key);

    /// <summary>Reads one child session's entry, with the last type line standing.</summary>
    /// <param name="home">The operator's home.</param>
    /// <param name="key">The child session's key.</param>
    /// <returns>The entry; null when the child was never recorded.</returns>
    WorkerSessionEntry? Read(string home, string key);

    /// <summary>Reads one child's entry, polling until its type is known or the wait runs out.</summary>
    /// <param name="home">The operator's home.</param>
    /// <param name="key">The child session's key.</param>
    /// <param name="timeout">How long to keep polling for the type.</param>
    /// <returns>The entry when its type arrived in time; null otherwise.</returns>
    WorkerSessionEntry? AwaitType(string home, string key, TimeSpan timeout);

    /// <summary>Appends one parent line, so a child file is never replaced and parallel hook processes cannot lose a write.</summary>
    /// <param name="home">The operator's home.</param>
    /// <param name="key">The child session's key.</param>
    /// <param name="parentSessionId">The session id of the parent that dispatched the child.</param>
    void AppendParent(string home, string key, string parentSessionId);

    /// <summary>
    /// Appends a guessed type line, which a dispatch's return may later correct. A blank type is not written, so a
    /// guess can never erase a type another process already recorded.
    /// </summary>
    /// <param name="home">The operator's home.</param>
    /// <param name="key">The child session's key.</param>
    /// <param name="agentType">The Squad agent type the child's start suggests.</param>
    void AppendGuess(string home, string key, string agentType);

    /// <summary>
    /// Appends the type line the dispatch's return names. It is authoritative, and it stands over any guess whatever
    /// order the two processes appended in. A blank type is not written.
    /// </summary>
    /// <param name="home">The operator's home.</param>
    /// <param name="key">The child session's key.</param>
    /// <param name="agentType">The Squad agent type the dispatch returned.</param>
    void AppendType(string home, string key, string agentType);

    /// <summary>Deletes one child session's entry.</summary>
    /// <param name="home">The operator's home.</param>
    /// <param name="key">The child session's key.</param>
    void Delete(string home, string key);

    /// <summary>Deletes one parent's in-flight list.</summary>
    /// <param name="home">The operator's home.</param>
    /// <param name="key">The parent session's pending key.</param>
    void DeletePending(string home, string key);

    /// <summary>Reads a parent's in-flight list.</summary>
    /// <param name="home">The operator's home.</param>
    /// <param name="key">The parent session's pending key.</param>
    /// <returns>The lines; empty when the parent has none.</returns>
    string[] ReadPending(string home, string key);

    /// <summary>Appends one line to a parent's in-flight list.</summary>
    /// <param name="home">The operator's home.</param>
    /// <param name="key">The parent session's pending key.</param>
    /// <param name="entry">The line, with its line ending.</param>
    void AppendPending(string home, string key, string entry);

    /// <summary>Deletes the ledger files not written to since the retention window.</summary>
    /// <param name="home">The operator's home.</param>
    /// <param name="now">The time the window is measured from.</param>
    /// <param name="retention">How long a ledger file is kept after its last write.</param>
    void Sweep(string home, DateTimeOffset now, TimeSpan retention);
}
