using System.Text;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.FileSystem;

namespace KyberWeave.Data.Hooks.Arbiter;

/// <summary>The budget files as append-only files that parallel hook processes share.</summary>
public sealed class FileWorkerBudgetStore(IWritableFileSystem files) : IWorkerBudgetStore
{
    private const string BudgetDirectoryName = "budgets";

    /// <inheritdoc/>
    public void EnsureDirectory(string home) => files.CreateDirectory(DirectoryOf(home));

    /// <inheritdoc/>
    public bool Exists(string home, string key) => files.FileExists(PathOf(home, key));

    /// <inheritdoc/>
    public string[] ReadLines(string home, string key)
    {
        string path = PathOf(home, key);
        return files.FileExists(path) ? [.. files.ReadLines(path)] : [];
    }

    /// <inheritdoc/>
    public void Delete(string home, string key) => files.DeleteFile(PathOf(home, key));

    /// <inheritdoc/>
    public void TryCreate(string home, string key, string content)
    {
        // Losing the race is the normal case: another hook process wrote the header first, and its limits stand.
        if (files.TryCreateNewFile(PathOf(home, key)))
        {
            Append(home, key, content);
        }
    }

    /// <inheritdoc/>
    public void Append(string home, string key, string content)
    {
        using Stream append = files.OpenAppendShared(PathOf(home, key));
        append.Write(Encoding.UTF8.GetBytes(content));
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

    private static string DirectoryOf(string home) => Path.Combine(home, ".kyber-weave", "arbiter", BudgetDirectoryName);

    private static string PathOf(string home, string key) => Path.Combine(DirectoryOf(home), key + ".log");
}
