using System.Text.Json;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.FileSystem;

namespace KyberWeave.Data.Hooks.Arbiter;

/// <summary>The snapshot documents as files: written aside and moved into place, so a crash mid-write leaves the previous whole document.</summary>
public sealed class FileHookSnapshotStore(IWritableFileSystem files, TimeProvider time, IIdGenerator ids) : IHookSnapshotStore
{
    private const string SnapshotLockFileName = ".snapshots.lock";

    private static readonly TimeSpan LockWindow = TimeSpan.FromMilliseconds(500);

    /// <inheritdoc/>
    public bool Exists(string directory, string fileName) => files.FileExists(Path.Combine(directory, fileName));

    /// <inheritdoc/>
    public string ReadText(string directory, string fileName) => ReadStoreText(Path.Combine(directory, fileName));

    /// <inheritdoc/>
    public void Update(string directory, string fileName, Func<string, string?> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        try
        {
            files.CreateDirectory(directory);
            string lockPath = Path.Combine(directory, SnapshotLockFileName);
            long started = time.GetTimestamp();
            TimeSpan backoff = TimeSpan.FromMilliseconds(5);
            while (true)
            {
                Stream? lockFile = null;
                try
                {
                    lockFile = files.OpenExclusive(lockPath);
                }
                catch (IOException) when (time.GetElapsedTime(started) < LockWindow)
                {
                    Pause(time, backoff);
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
                        string tempPath = Path.Combine(directory, ".snapshots." + ids.NewGuid().ToString("N") + ".tmp");
                        try
                        {
                            files.WriteAllText(tempPath, json);
                            files.MoveFile(tempPath, filePath, overwrite: true);
                        }
                        finally
                        {
                            try
                            {
                                files.DeleteFile(tempPath);
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

    /// <summary>
    ///     Blocks the calling thread for <paramref name="delay" /> on the store's clock. A synchronous caller waits on
    ///     a timer rather than on a Task; the controlled test clock fires the timer as soon as it is created.
    /// </summary>
    private static void Pause(TimeProvider clock, TimeSpan delay)
    {
        using ManualResetEventSlim reached = new(false);
        using ITimer timer = clock.CreateTimer(_ => reached.Set(), null, delay, Timeout.InfiniteTimeSpan);
        reached.Wait(Timeout.InfiniteTimeSpan);
    }

    /// <inheritdoc/>
    public async Task UpdateAsync(string directory, string fileName, Func<string, string?> mutate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        try
        {
            files.CreateDirectory(directory);
            string lockPath = Path.Combine(directory, SnapshotLockFileName);
            long started = time.GetTimestamp();
            TimeSpan backoff = TimeSpan.FromMilliseconds(5);
            while (true)
            {
                Stream? lockFile = null;
                try
                {
                    lockFile = files.OpenExclusive(lockPath);
                }
                catch (IOException) when (time.GetElapsedTime(started) < LockWindow)
                {
                    await Task.Delay(backoff, time, cancellationToken).ConfigureAwait(false);
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
                        string tempPath = Path.Combine(directory, ".snapshots." + ids.NewGuid().ToString("N") + ".tmp");
                        try
                        {
                            files.WriteAllText(tempPath, json);
                            files.MoveFile(tempPath, filePath, overwrite: true);
                        }
                        finally
                        {
                            try
                            {
                                files.DeleteFile(tempPath);
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

    private string ReadStoreText(string filePath)
    {
        try
        {
            return files.FileExists(filePath) ? files.ReadAllText(filePath) : string.Empty;
        }
        catch (Exception ex) when (FileFailure.Is(ex))
        {
            return string.Empty;
        }
    }

    // Crash debris from an interrupted write-aside: swept on the next locked record,
    // before the fresh document moves into place.
    private void SweepTempFiles(string directory)
    {
        foreach (string temp in files.EnumerateFiles(directory, ".snapshots.*.tmp", recursive: false).ToList())
        {
            try
            {
                files.DeleteFile(temp);
            }
            catch (Exception ex) when (FileFailure.Is(ex))
            {
            }
        }
    }
}
