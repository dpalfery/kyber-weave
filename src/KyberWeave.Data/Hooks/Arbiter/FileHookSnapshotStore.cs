using System.Diagnostics;
using System.Text.Json;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.FileSystem;

namespace KyberWeave.Data.Hooks.Arbiter;

/// <summary>The snapshot documents as files: written aside and moved into place, so a crash mid-write leaves the previous whole document.</summary>
public sealed class FileHookSnapshotStore : IHookSnapshotStore
{
    private const string SnapshotLockFileName = ".snapshots.lock";

    /// <inheritdoc/>
    public bool Exists(string directory, string fileName) => File.Exists(Path.Combine(directory, fileName));

    /// <inheritdoc/>
    public string ReadText(string directory, string fileName) => ReadStoreText(Path.Combine(directory, fileName));

    /// <inheritdoc/>
    public void Update(string directory, string fileName, Func<string, string?> mutate)
    {
        try
        {
            Directory.CreateDirectory(directory);
            string lockPath = Path.Combine(directory, SnapshotLockFileName);
            Stopwatch elapsed = Stopwatch.StartNew();
            TimeSpan backoff = TimeSpan.FromMilliseconds(5);
            while (true)
            {
                FileStream? lockFile = null;
                try
                {
                    lockFile = File.Open(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                }
                catch (IOException) when (elapsed.Elapsed < TimeSpan.FromMilliseconds(500))
                {
                    Thread.Sleep(backoff);
                    backoff = TimeSpan.FromMilliseconds(Math.Min(50, backoff.TotalMilliseconds * 2));
                    continue;
                }

                using (lockFile)
                {
                    string filePath = Path.Combine(directory, fileName);
                    SweepTempFiles(directory);
                    string? json = mutate(ReadStoreText(filePath));
                    if (json is not null)
                    {
                        // Write aside and move into place: a crash mid-write leaves the previous
                        // whole document behind, never a truncated one that reads back as [].
                        string tempPath = Path.Combine(directory, ".snapshots." + Guid.NewGuid().ToString("N") + ".tmp");
                        try
                        {
                            File.WriteAllText(tempPath, json);
                            File.Move(tempPath, filePath, overwrite: true);
                        }
                        finally
                        {
                            try
                            {
                                File.Delete(tempPath);
                            }
                            catch (Exception ex) when (FileFailure.Is(ex))
                            {
                            }
                        }
                    }

                    return;
                }
            }
        }
        catch (Exception ex) when (FileFailure.Is(ex) || ex is JsonException)
        {
            // Snapshot persistence is best-effort; on failure the fact stays absent so the
            // rule stops and asks instead of judging a diff the dispatch never appeared in.
        }
    }

    private static string ReadStoreText(string filePath)
    {
        try
        {
            return File.Exists(filePath) ? File.ReadAllText(filePath) : string.Empty;
        }
        catch (Exception ex) when (FileFailure.Is(ex))
        {
            return string.Empty;
        }
    }

    // Crash debris from an interrupted write-aside: swept on the next locked record,
    // before the fresh document moves into place.
    private static void SweepTempFiles(string directory)
    {
        foreach (string temp in Directory.GetFiles(directory, ".snapshots.*.tmp"))
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception ex) when (FileFailure.Is(ex))
            {
            }
        }
    }
}
