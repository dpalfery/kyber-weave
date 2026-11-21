using System.Diagnostics;
using System.Text;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.FileSystem;

namespace KyberWeave.Data.Hooks.Arbiter;

/// <summary>The Factory session ledger as files that parallel hook processes share, beside the budget files.</summary>
public sealed class FileWorkerSessionStore(IWritableFileSystem files) : IWorkerSessionStore
{
    private const string SessionDirectoryName = "sessions";
    private const string SessionSuffix = ".session";
    private const string PendingSuffix = ".list";
    private const string ParentPrefix = "parent";
    private const string TypePrefix = "type";

    /// <summary>How often <see cref="AwaitType" /> re-reads the entry while a dispatch is still in flight.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    /// <inheritdoc/>
    public void EnsureDirectory(string home) => files.CreateDirectory(DirectoryOf(home));

    /// <inheritdoc/>
    public bool Exists(string home, string key) => files.FileExists(SessionPathOf(home, key));

    /// <inheritdoc/>
    public WorkerSessionEntry? Read(string home, string key)
    {
        string path = SessionPathOf(home, key);
        if (!files.FileExists(path))
        {
            return null;
        }

        string? parent = null;
        string? type = null;
        foreach (string line in files.ReadLines(path))
        {
            int space = line.IndexOf(' ', StringComparison.Ordinal);
            if (space <= 0)
            {
                continue;
            }

            string name = line[..space];
            string value = line[(space + 1)..];
            if (string.Equals(name, ParentPrefix, StringComparison.Ordinal))
            {
                parent = value;
            }
            else if (string.Equals(name, TypePrefix, StringComparison.Ordinal))
            {
                // A corrected type is appended after the first: the last one stands.
                type = value;
            }
        }

        return parent is null ? null : new WorkerSessionEntry(parent, type);
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
    public void Write(string home, string key, WorkerSessionEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        StringBuilder content = new StringBuilder()
            .Append(ParentPrefix).Append(' ').Append(OneLine(entry.ParentSessionId)).Append('\n');
        if (entry.AgentType is not null)
        {
            content.Append(TypePrefix).Append(' ').Append(OneLine(entry.AgentType)).Append('\n');
        }

        files.WriteAllTextAtomic(SessionPathOf(home, key), content.ToString());
    }

    /// <inheritdoc/>
    public void Delete(string home, string key) => files.DeleteFile(SessionPathOf(home, key));

    /// <inheritdoc/>
    public string[] ReadPending(string home, string key)
    {
        string path = PendingPathOf(home, key);
        return files.FileExists(path) ? [.. files.ReadLines(path)] : [];
    }

    /// <inheritdoc/>
    public void AppendPending(string home, string key, string entry)
    {
        using Stream append = files.OpenAppendShared(PendingPathOf(home, key));
        append.Write(Encoding.UTF8.GetBytes(OneLine(entry) + "\n"));
    }

    /// <inheritdoc/>
    public void Sweep(string home, DateTimeOffset now, TimeSpan retention)
    {
        foreach (string file in files.EnumerateFiles(DirectoryOf(home), "*", recursive: false))
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

    /// <summary>A session id or type is written as one line, so a value carrying a newline cannot forge an entry.</summary>
    private static string OneLine(string value) =>
        value.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ');

    private static string DirectoryOf(string home) => Path.Combine(home, ".kyber-weave", "arbiter", SessionDirectoryName);

    private static string SessionPathOf(string home, string key) => Path.Combine(DirectoryOf(home), key + SessionSuffix);

    private static string PendingPathOf(string home, string key) => Path.Combine(DirectoryOf(home), key + PendingSuffix);
}
