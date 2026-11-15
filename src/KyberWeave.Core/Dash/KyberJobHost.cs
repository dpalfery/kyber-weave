using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KyberWeave.Core.Dash;

/// <summary>Published local server endpoint descriptor written to <c>server.json</c>.</summary>
public sealed record ServerInfo(
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("pid")] int Pid,
    [property: JsonPropertyName("startedAt")] string StartedAt);

/// <summary>Snapshot of the in-process background job host state.</summary>
public sealed record JobHostStatus(
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("lastSuccessAt")] string? LastSuccessAt,
    [property: JsonPropertyName("lastFailure")] string? LastFailure,
    [property: JsonPropertyName("nextDueAt")] string? NextDueAt,
    [property: JsonPropertyName("paused")] bool Paused,
    [property: JsonPropertyName("hostedElsewhere")] bool HostedElsewhere,
    [property: JsonPropertyName("lastMaintenanceFailure")] string? LastMaintenanceFailure,
    [property: JsonPropertyName("lastSyncFailure")] string? LastSyncFailure);

/// <summary>Result returned by an in-process job trigger (<c>succeeded</c>, <c>busy</c>, <c>failed</c>, <c>declined</c>, <c>hosted-elsewhere</c>).</summary>
public sealed record JobOutcome(
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("reason")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Reason = null);

/// <summary>Active exclusive lease on <c>jobs.lock</c>.</summary>
public sealed class JobsLease : IDisposable
{
    private readonly string _lockPath;
    private readonly int _pid;
    private bool _released;

    internal JobsLease(string lockPath, int pid)
    {
        _lockPath = lockPath;
        _pid = pid;
    }

    /// <summary>Releases <c>jobs.lock</c> if this process still owns it.</summary>
    public void Release()
    {
        if (_released)
            return;

        _released = true;
        try
        {
            if (!File.Exists(_lockPath))
                return;

            FileAttributes attrs = File.GetAttributes(_lockPath);
            if ((attrs & FileAttributes.ReparsePoint) != 0)
                return;

            string raw = File.ReadAllText(_lockPath);
            using JsonDocument doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("pid", out JsonElement pidEl)
                && pidEl.TryGetInt32(out int ownerPid)
                && ownerPid == _pid)
            {
                File.Delete(_lockPath);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (JsonException)
        {
        }
    }

    /// <inheritdoc/>
    public void Dispose() => Release();
}

/// <summary>
/// In-process background job coordinator (<c>dash/src/jobs/host.ts</c>, <c>dash/src/jobs/lease.ts</c>)
/// owning <c>jobs.lock</c> arbitration, atomic <c>0600</c> <c>server.json</c> publication with symlink
/// refusal, projection maintenance passes, and manual refresh/import triggers.
/// </summary>
public sealed class KyberJobHost : IDisposable
{
    /// <summary>File name for the mutual-exclusion job host lease.</summary>
    public const string JobsLockFile = "jobs.lock";

    /// <summary>File name for the published loopback dashboard server descriptor.</summary>
    public const string ServerInfoFile = "server.json";

    private readonly CanonStore _store;
    private readonly string _stateDir;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Action<CanonStore>? _maintenance;
    private readonly Lock _gate = new();

    private JobsLease? _lease;
    private string _state = "idle";
    private string? _lastSuccessAt;
    private string? _lastFailure;
    private string? _lastMaintenanceFailure;
    private bool _runningJob;
    private bool _disposed;

    /// <summary>Initializes a new <see cref="KyberJobHost"/> over <paramref name="store"/> and <paramref name="stateDir"/>.</summary>
    public KyberJobHost(
        CanonStore store,
        string stateDir,
        Func<DateTimeOffset>? clock = null,
        Action<CanonStore>? maintenance = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDir);

        _store = store;
        _stateDir = Path.GetFullPath(stateDir);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _maintenance = maintenance;
    }

    /// <summary>Publishes <c>server.json</c> atomically with owner-only (<c>0600</c>) permissions, refusing symlinks.</summary>
    public static void PublishServerInfo(string stateDir, int port, int pid, DateTimeOffset? startedAt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDir);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(port);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pid);

        string fullDir = Path.GetFullPath(stateDir);
        EnsureSafeDirectory(fullDir);

        string targetPath = Path.Combine(fullDir, ServerInfoFile);
        RefuseSymlink(targetPath);

        string tempPath = Path.Combine(
            fullDir,
            string.Create(CultureInfo.InvariantCulture, $".server.{pid}.{Guid.NewGuid():N}.tmp"));

        ServerInfo info = new(
            Port: port,
            Pid: pid,
            StartedAt: (startedAt ?? DateTimeOffset.UtcNow).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
        byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(info);

        using (FileStream fs = new(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            fs.Write(jsonBytes, 0, jsonBytes.Length);

        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(tempPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        File.Move(tempPath, targetPath, overwrite: true);
    }

    /// <summary>Reads <c>server.json</c> if present and safe, refusing symlinks.</summary>
    public static ServerInfo? ReadServerInfo(string stateDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDir);
        string fullDir = Path.GetFullPath(stateDir);
        string targetPath = Path.Combine(fullDir, ServerInfoFile);
        if (!File.Exists(targetPath))
            return null;

        RefuseSymlink(fullDir);
        RefuseSymlink(targetPath);

        string raw = File.ReadAllText(targetPath);
        return JsonSerializer.Deserialize<ServerInfo>(raw);
    }

    /// <summary>Removes <c>server.json</c> only if it exists, is not a symlink, and records <paramref name="pid"/>.</summary>
    public static bool RemoveServerInfo(string stateDir, int pid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDir);
        string fullDir = Path.GetFullPath(stateDir);
        string targetPath = Path.Combine(fullDir, ServerInfoFile);
        if (!File.Exists(targetPath))
            return false;

        RefuseSymlink(fullDir);
        RefuseSymlink(targetPath);

        try
        {
            string raw = File.ReadAllText(targetPath);
            ServerInfo? info = JsonSerializer.Deserialize<ServerInfo>(raw);
            if (info is null || info.Pid != pid)
                return false;

            File.Delete(targetPath);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Attempts to acquire the exclusive <c>jobs.lock</c> lease for <paramref name="stateDir"/> without
    /// blocking, reclaiming stale locks whose owning PID has exited.
    /// </summary>
    public static JobsLease? TryAcquireJobsLease(string stateDir, int? pid = null, DateTimeOffset? now = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDir);
        string fullDir = Path.GetFullPath(stateDir);
        EnsureSafeDirectory(fullDir);

        string lockPath = Path.Combine(fullDir, JobsLockFile);
        RefuseSymlink(lockPath);

        int ownerPid = pid ?? Environment.ProcessId;
        string acquiredAt = (now ?? DateTimeOffset.UtcNow).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new { pid = ownerPid, acquiredAt });

        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using FileStream fs = new(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                fs.Write(payload, 0, payload.Length);
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(lockPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

                return new JobsLease(lockPath, ownerPid);
            }
            catch (IOException)
            {
                RefuseSymlink(lockPath);
                if (!TryReadLockPid(lockPath, out int existingPid))
                    return null;

                if (existingPid == ownerPid)
                    return new JobsLease(lockPath, ownerPid);

                if (IsProcessAlive(existingPid))
                    return null;

                try
                {
                    File.Delete(lockPath);
                }
                catch (IOException)
                {
                    return null;
                }
            }
        }

        return null;
    }

    /// <summary>Attempts to claim hosting of background jobs via <c>jobs.lock</c>.</summary>
    public bool TryStartHosting()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_lease is not null)
                return true;

            _lease = TryAcquireJobsLease(_stateDir, now: _clock());
            return _lease is not null;
        }
    }

    /// <summary>Returns the current job host status snapshot.</summary>
    public JobHostStatus GetStatus()
    {
        SettingsView settings = CanonSettings.ReadSettings(_store);
        lock (_gate)
        {
            bool hostedElsewhere = _lease is null && !TryEnsureLeaseLocked();
            string reportedState = hostedElsewhere ? "running-elsewhere" : _state;
            string? nextDueAt = null;
            if (!settings.JobsPaused && !hostedElsewhere && _lastSuccessAt is not null
                && DateTimeOffset.TryParse(_lastSuccessAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset last))
            {
                nextDueAt = last.AddMinutes(settings.RefreshCadenceMinutes)
                    .UtcDateTime
                    .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
            }

            return new JobHostStatus(
                State: reportedState,
                LastSuccessAt: _lastSuccessAt,
                LastFailure: _lastFailure,
                NextDueAt: nextDueAt,
                Paused: settings.JobsPaused,
                HostedElsewhere: hostedElsewhere,
                LastMaintenanceFailure: _lastMaintenanceFailure,
                LastSyncFailure: null);
        }
    }

    /// <summary>Executes one maintenance projection pass if this host holds the jobs lease.</summary>
    public JobOutcome RunMaintenancePass()
    {
        lock (_gate)
        {
            if (_disposed)
                return new JobOutcome("cancelled", "Job host disposed");

            if (!TryEnsureLeaseLocked())
                return new JobOutcome("hosted-elsewhere");

            if (_runningJob)
                return new JobOutcome("busy");

            _runningJob = true;
            _state = "running";
        }

        try
        {
            if (_maintenance is not null)
            {
                _maintenance(_store);
            }
            else
            {
                CanonRefresh.PurgeExpiredContent(_store, _clock());
                CanonSessionProjector.ProjectIncremental(_store);
                CanonRollups.ProjectRunsAndHarnesses(_store);
            }

            lock (_gate)
            {
                _lastMaintenanceFailure = null;
                _lastSuccessAt = _clock().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
                _lastFailure = null;
                _state = "idle";
            }

            return new JobOutcome("succeeded");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or JsonException)
        {
            string summary = SummarizeFailure(ex.Message);
            lock (_gate)
            {
                _lastMaintenanceFailure = summary;
                _lastFailure = summary;
                _state = "failed";
            }

            return new JobOutcome("failed", summary);
        }
        finally
        {
            lock (_gate)
                _runningJob = false;
        }
    }

    /// <summary>Triggers an immediate refresh/projection run inside this process.</summary>
    public JobOutcome TriggerRefresh(Action<CanonStore>? onRefresh = null)
    {
        SettingsView settings = CanonSettings.ReadSettings(_store);
        if (settings.JobsPaused)
            return new JobOutcome("declined", "Background jobs are paused");

        return ExecuteManualJob(onRefresh);
    }

    /// <summary>Triggers an immediate folder-history import/projection pass inside this process.</summary>
    public JobOutcome TriggerImportHistory(Action<CanonStore>? onImport = null)
    {
        return ExecuteManualJob(onImport);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            _lease?.Dispose();
            _lease = null;
        }
    }

    private JobOutcome ExecuteManualJob(Action<CanonStore>? work)
    {
        lock (_gate)
        {
            if (_disposed)
                return new JobOutcome("cancelled", "Job host disposed");

            if (!TryEnsureLeaseLocked())
                return new JobOutcome("hosted-elsewhere");

            if (_runningJob)
                return new JobOutcome("busy");

            _runningJob = true;
            _state = "running";
        }

        try
        {
            work?.Invoke(_store);
            CanonRefresh.PurgeExpiredContent(_store, _clock());
            CanonSessionProjector.ProjectIncremental(_store);
            CanonRollups.ProjectRunsAndHarnesses(_store);

            lock (_gate)
            {
                _lastSuccessAt = _clock().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
                _lastFailure = null;
                _state = "idle";
            }

            return new JobOutcome("succeeded");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or JsonException or ArgumentException)
        {
            string summary = SummarizeFailure(ex.Message);
            lock (_gate)
            {
                _lastFailure = summary;
                _state = "failed";
            }

            return new JobOutcome("failed", summary);
        }
        finally
        {
            lock (_gate)
                _runningJob = false;
        }
    }

    private bool TryEnsureLeaseLocked()
    {
        if (_lease is not null)
            return true;

        _lease = TryAcquireJobsLease(_stateDir, now: _clock());
        return _lease is not null;
    }

    private static void EnsureSafeDirectory(string fullDir)
    {
        if (Directory.Exists(fullDir))
        {
            RefuseSymlink(fullDir);
            return;
        }

        DirectoryInfo created = Directory.CreateDirectory(fullDir);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                created.FullName,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static void RefuseSymlink(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return;

        FileAttributes attrs = File.GetAttributes(path);
        if ((attrs & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Refusing to traverse symbolic link at '{path}'");
    }

    private static bool TryReadLockPid(string lockPath, out int pid)
    {
        pid = 0;
        try
        {
            string raw = File.ReadAllText(lockPath);
            using JsonDocument doc = JsonDocument.Parse(raw);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("pid", out JsonElement pidEl)
                && pidEl.TryGetInt32(out pid)
                && pid > 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        if (pid <= 0)
            return false;

        try
        {
            using Process proc = Process.GetProcessById(pid);
            return !proc.HasExited;
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

    private static string SummarizeFailure(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "Job failed";

        string singleLine = message.ReplaceLineEndings(" ").Trim();
        return singleLine.Length > 200 ? singleLine[..200] : singleLine;
    }
}
