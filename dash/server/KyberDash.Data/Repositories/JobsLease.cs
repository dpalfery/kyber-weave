using System.Diagnostics;
using System.Text.Json;

namespace KyberDash.Data.Repositories;

/// <summary>
/// Exclusive lease on <c>jobs.lock</c>. Releasing it deletes the file only while this process still
/// owns the recorded pid, so a lease reclaimed by another process is never removed underneath it.
/// </summary>
public class JobsLease : IDisposable
{
    private readonly string lockPath;
    private readonly int pid;
    private bool released;

    /// <summary>Initializes a lease over an already-created lock file.</summary>
    /// <param name="lockPath">Path to <c>jobs.lock</c>.</param>
    /// <param name="pid">Process id recorded as the lock owner.</param>
    internal JobsLease(string lockPath, int pid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockPath);
        this.lockPath = Path.GetFullPath(lockPath);
        this.pid = pid;
    }

    /// <summary>Releases <c>jobs.lock</c> when this process still owns it.</summary>
    public void Release()
    {
        if (released)
            return;

        released = true;
        try
        {
            if (!File.Exists(lockPath))
                return;

            FileAttributes attributes = File.GetAttributes(lockPath);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                return;

            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(lockPath));
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("pid", out JsonElement pidEl)
                && pidEl.TryGetInt32(out int ownerPid)
                && ownerPid == pid)
            {
                File.Delete(lockPath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // A lease that cannot be cleaned up is left for the reclaim path on the next start.
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            Release();
        }
    }

    /// <summary>True when a process with <paramref name="candidatePid"/> is still running.</summary>
    /// <param name="candidatePid">Process id to test.</param>
    /// <returns><see langword="true"/> when the process exists and has not exited.</returns>
    internal static bool IsProcessAlive(int candidatePid)
    {
        if (candidatePid <= 0 || candidatePid > MaxPidValue)
            return false;

        try
        {
            using Process process = Process.GetProcessById(candidatePid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Largest pid the host kernel can allocate. A recorded pid above it belongs to no process, and
    /// <see cref="Process.GetProcessById"/> may still hand back a handle for such a value instead of failing.
    /// </summary>
    private static int MaxPidValue
    {
        get
        {
            if (OperatingSystem.IsLinux())
            {
                try
                {
                    string maxPid = File.ReadAllText("/proc/sys/kernel/pid_max").Trim();
                    if (int.TryParse(maxPid, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int parsed)
                        && parsed > 0)
                    {
                        return parsed;
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    return int.MaxValue;
                }
            }

            // macOS caps pid_t at 99998; Windows allocates opaque ids that are never near int.MaxValue.
            return OperatingSystem.IsMacOS() ? 99_998 : int.MaxValue;
        }
    }

    /// <summary>Reads the pid recorded in <c>jobs.lock</c>.</summary>
    /// <param name="path">Lock file path.</param>
    /// <param name="pid">The recorded positive pid, when readable.</param>
    /// <returns><see langword="true"/> when a positive pid was read.</returns>
    internal static bool TryReadLockPid(string path, out int pid)
    {
        pid = 0;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("pid", out JsonElement pidEl)
                && pidEl.TryGetInt32(out pid)
                && pid > 0;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
