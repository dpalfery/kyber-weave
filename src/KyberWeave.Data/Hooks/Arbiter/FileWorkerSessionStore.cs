using System.Diagnostics;
using System.Text;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.FileSystem;

namespace KyberWeave.Data.Hooks.Arbiter;

/// <summary>The Factory session ledger as append-only files that parallel hook processes share, beside the budget files.</summary>
/// <remarks>
/// A child's file is never replaced: a <c>parent</c> line names the dispatching session, a <c>guess</c> line holds the
/// type a child's start suggests, and a <c>type</c> line holds the type the dispatch's return names. The reads take
/// the last of each, and the last <c>type</c> line outranks every guess, so a correction wins whatever order the hook
/// processes appended in.
/// </remarks>
public sealed class FileWorkerSessionStore(IWritableFileSystem files) : IWorkerSessionStore
{
    private const string ParentPrefix = "parent";
    private const string GuessPrefix = "guess";
    private const string TypePrefix = "type";

    /// <summary>How often <see cref="AwaitType" /> re-reads the entry while a dispatch is still in flight.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    /// <inheritdoc/>
    public void EnsureDirectory(string home) => files.CreateDirectory(WorkerSessionPaths.DirectoryOf(home));

    /// <inheritdoc/>
    public bool Exists(string home, string key) => files.FileExists(WorkerSessionPaths.ChildPathOf(home, key));

    /// <inheritdoc/>
    public WorkerSessionEntry? Read(string home, string key)
    {
        string path = WorkerSessionPaths.ChildPathOf(home, key);
        if (!files.FileExists(path))
        {
            return null;
        }

        string? parent = null;
        string? guess = null;
        string? type = null;
        foreach (string line in files.ReadLines(path))
        {
            if (SplitLine(line) is not { } entry)
            {
                continue;
            }

            if (string.Equals(entry.Name, ParentPrefix, StringComparison.Ordinal))
            {
                parent = entry.Value;
            }
            else if (string.Equals(entry.Name, GuessPrefix, StringComparison.Ordinal))
            {
                guess = entry.Value;
            }
            else if (string.Equals(entry.Name, TypePrefix, StringComparison.Ordinal))
            {
                // A corrected type is appended after the first: the last one stands.
                type = entry.Value;
            }
        }

        // A corrected type outranks a guess, so the two lines can arrive in either order.
        return parent is null ? null : new WorkerSessionEntry(parent, NonBlank(type) ?? NonBlank(guess));
    }

    /// <inheritdoc/>
    public WorkerSessionEntry? AwaitType(string home, string key, TimeSpan timeout)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        while (true)
        {
            WorkerSessionEntry? entry = Read(home, key);
            if (entry?.AgentType is not null || elapsed.Elapsed >= timeout)
            {
                return entry;
            }

            Thread.Sleep(PollInterval);
        }
    }

    /// <inheritdoc/>
    public void AppendParent(string home, string key, string parentSessionId)
    {
        ArgumentNullException.ThrowIfNull(parentSessionId);
        Append(home, key, ParentPrefix + " " + OneLine(parentSessionId));
    }

    /// <inheritdoc/>
    public void AppendGuess(string home, string key, string agentType)
    {
        if (NonBlank(agentType) is not null)
        {
            Append(home, key, GuessPrefix + " " + OneLine(agentType));
        }
    }

    /// <inheritdoc/>
    public void AppendType(string home, string key, string agentType)
    {
        if (NonBlank(agentType) is not null)
        {
            Append(home, key, TypePrefix + " " + OneLine(agentType));
        }
    }

    /// <inheritdoc/>
    public void Delete(string home, string key) => files.DeleteFile(WorkerSessionPaths.ChildPathOf(home, key));

    /// <inheritdoc/>
    public void DeletePending(string home, string key) => files.DeleteFile(WorkerSessionPaths.PendingPathOf(home, key));

    /// <inheritdoc/>
    public string[] ReadPending(string home, string key)
    {
        string path = WorkerSessionPaths.PendingPathOf(home, key);
        return files.FileExists(path) ? [.. files.ReadLines(path)] : [];
    }

    /// <inheritdoc/>
    public void AppendPending(string home, string key, string entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        using Stream append = files.OpenAppendShared(WorkerSessionPaths.PendingPathOf(home, key));
        append.Write(Encoding.UTF8.GetBytes(OneLine(entry) + "\n"));
    }

    /// <inheritdoc/>
    public void Sweep(string home, DateTimeOffset now, TimeSpan retention)
    {
        string directory = WorkerSessionPaths.DirectoryOf(home);
        if (!files.DirectoryExists(directory))
        {
            // Nothing was ever recorded, so there is nothing to sweep and no directory to create.
            return;
        }

        foreach (string file in files.EnumerateFiles(directory, "*", recursive: false))
        {
            try
            {
                if (now - files.GetLastWriteTimeUtc(file) > retention)
                {
                    files.DeleteFile(file);
                }
            }
            catch (Exception ex) when (FileFailure.Is(ex))
            {
                // A file another process holds is swept next time.
            }
        }
    }

    /// <summary>Appends one line to a child's file, creating it when it is missing.</summary>
    private void Append(string home, string key, string line)
    {
        using Stream append = files.OpenAppendShared(WorkerSessionPaths.ChildPathOf(home, key));
        append.Write(Encoding.UTF8.GetBytes(line + "\n"));
    }

    /// <summary>Splits one ledger line into its name and value, or null when it holds neither.</summary>
    private static (string Name, string Value)? SplitLine(string line)
    {
        int space = line.IndexOf(' ', StringComparison.Ordinal);
        return space <= 0 ? null : (line[..space], line[(space + 1)..]);
    }

    private static string? NonBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>A session id or type is written as one line, so a value carrying a newline cannot forge an entry.</summary>
    private static string OneLine(string value) =>
        value.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ');
}
