using System.Text;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.FileSystem;

namespace KyberWeave.Data.Hooks.Arbiter;

/// <summary>The budget files as append-only files that parallel hook processes share.</summary>
public sealed class FileWorkerBudgetStore : IWorkerBudgetStore
{
    private const string BudgetDirectoryName = "budgets";

    /// <inheritdoc/>
    public void EnsureDirectory(string home) => Directory.CreateDirectory(DirectoryOf(home));

    /// <inheritdoc/>
    public bool Exists(string home, string key) => File.Exists(PathOf(home, key));

    /// <inheritdoc/>
    public string[] ReadLines(string home, string key)
    {
        string path = PathOf(home, key);
        return File.Exists(path) ? File.ReadAllLines(path) : [];
    }

    /// <inheritdoc/>
    public void Delete(string home, string key) => File.Delete(PathOf(home, key));

    /// <inheritdoc/>
    public void TryCreate(string home, string key, string content)
    {
        string path = PathOf(home, key);
        try
        {
            using FileStream created = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite);
            created.Write(Encoding.UTF8.GetBytes(content));
        }
        catch (IOException) when (File.Exists(path))
        {
            // Another hook process wrote the header first; its limits stand.
        }
    }

    /// <inheritdoc/>
    public void Append(string home, string key, string content)
    {
        using FileStream append = new(PathOf(home, key), FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        append.Write(Encoding.UTF8.GetBytes(content));
    }

    /// <inheritdoc/>
    public void Sweep(string home, DateTimeOffset now, TimeSpan retention)
    {
        foreach (string file in Directory.EnumerateFiles(DirectoryOf(home)))
        {
            try
            {
                if (now - File.GetLastWriteTimeUtc(file) > retention)
                {
                    File.Delete(file);
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
