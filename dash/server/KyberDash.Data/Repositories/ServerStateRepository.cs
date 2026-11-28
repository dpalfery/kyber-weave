using System.Globalization;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KyberDash.Contracts.Repositories;
using KyberDash.Contracts.Services;
using KyberDash.Domain;

namespace KyberDash.Data.Repositories;

/// <summary>
/// Process-coordination files under the state directory: the published <c>server.json</c> endpoint, the
/// exclusive <c>jobs.lock</c> lease, and the single-engine <c>engine.json</c> plus <c>engine.sock</c> pair.
/// </summary>
public class ServerStateRepository : IServerStateRepository, IEngineDescriptorPublisher
{
    /// <summary>File name of the published loopback dashboard endpoint.</summary>
    public const string ServerInfoFile = "server.json";

    /// <summary>File name of the mutual-exclusion job host lease.</summary>
    public const string JobsLockFile = "jobs.lock";

    /// <summary>File name of the engine descriptor.</summary>
    public const string EngineDescriptorFile = "engine.json";

    /// <summary>File name of the engine Unix domain socket.</summary>
    public const string EngineSocketFile = "engine.sock";

    /// <summary>Environment variable overriding the state directory.</summary>
    public const string StateDirectoryEnvironmentVariable = "KYBERDASH_STATE_DIR";


    /// <summary>
    /// Budget for one engine IPC round trip, a loopback unix-socket request to a process on this
    /// host. It answers in milliseconds when idle; the budget is generous so a heavily loaded
    /// machine cannot turn a live engine into "not running".
    /// </summary>
    private const int EngineQueryTimeoutMs = 5000;

    /// <summary>Serializer options used for <c>engine.json</c> and the engine IPC replies.</summary>
    internal static readonly JsonSerializerOptions EngineJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string defaultStateDirectory;
    private readonly TimeProvider timeProvider;

    /// <summary>Initializes a repository whose default state directory is <c>~/.kyberdash</c> or the configured override.</summary>
    public ServerStateRepository()
        : this(null)
    {
    }

    /// <summary>Initializes a repository with an explicit default state directory.</summary>
    /// <param name="stateDirectory">State directory to use when no other override applies.</param>
    /// <param name="timeProvider">Clock supplying acquisition and lease timestamps; defaults to <see cref="TimeProvider.System"/>.</param>
    public ServerStateRepository(string? stateDirectory, TimeProvider? timeProvider = null)
    {
        defaultStateDirectory = ResolveStateDirectory(stateDirectory);
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public string DefaultStateDirectory => defaultStateDirectory;

    /// <summary>
    /// Resolves the state directory: explicit argument, then <c>KYBERDASH_STATE_DIR</c>, then
    /// <c>~/.kyberdash</c>, matching the engine host's precedence.
    /// </summary>
    /// <param name="explicitDirectory">Caller-supplied override.</param>
    /// <returns>The resolved absolute state directory.</returns>
    public static string ResolveStateDirectory(string? explicitDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitDirectory))
            return Path.GetFullPath(explicitDirectory.Trim());

        string? fromEnvironment = Environment.GetEnvironmentVariable(StateDirectoryEnvironmentVariable)?.Trim();
        if (!string.IsNullOrEmpty(fromEnvironment))
            return Path.GetFullPath(fromEnvironment);

        string home = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : Environment.GetEnvironmentVariable("HOME") is { Length: > 0 } envHome
                ? envHome
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, CanonDbPath.DefaultDirectoryName);
    }

    /// <summary>Path of the engine socket, moved to the temp directory when it would exceed the Unix socket path limit.</summary>
    /// <param name="stateDir">State directory.</param>
    /// <returns>The socket path.</returns>
    public static string EngineSocketPath(string stateDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDir);
        string fullDir = Path.GetFullPath(stateDir);
        string inState = Path.Combine(fullDir, EngineSocketFile);
        if (Encoding.UTF8.GetByteCount(inState) <= UnixSocketPathByteLimit)
            return inState;

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(fullDir));
        string hex16 = Convert.ToHexStringLower(digest)[..16];
        return Path.Combine(Path.GetTempPath(), string.Create(CultureInfo.InvariantCulture, $"kyberdash-engine-{hex16}.sock"));
    }

    /// <summary>
    /// Largest Unix domain socket path this host accepts, in UTF-8 bytes. Linux allows 108 including the
    /// terminator; every other platform allows 104. The margin keeps a binding that works here from being
    /// refused on the longest path a test can reach.
    /// </summary>
    private static int UnixSocketPathByteLimit => OperatingSystem.IsLinux() ? 107 : 103;

    /// <inheritdoc />
    public ServerInfo? ReadServerInfo(string stateDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDir);
        string fullDir = Path.GetFullPath(stateDir);
        string path = Path.Combine(fullDir, ServerInfoFile);
        if (!File.Exists(path))
            return null;

        AtomicFile.RefuseSymlink(fullDir);
        AtomicFile.RefuseSymlink(path);

        try
        {
            return JsonSerializer.Deserialize<ServerInfo>(File.ReadAllText(path, Encoding.UTF8));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public void WriteServerInfo(string stateDir, ServerInfo info)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDir);
        ArgumentNullException.ThrowIfNull(info);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(info.Port);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(info.Pid);

        string fullDir = AtomicFile.EnsureSafeDirectory(stateDir);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(info);
        AtomicFile.Write(Path.Combine(fullDir, ServerInfoFile), json, info.Pid);
    }

    /// <inheritdoc />
    public void DeleteServerInfo(string stateDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDir);
        string fullDir = Path.GetFullPath(stateDir);
        string path = Path.Combine(fullDir, ServerInfoFile);
        if (!File.Exists(path))
            return;

        AtomicFile.RefuseSymlink(fullDir);
        AtomicFile.RefuseSymlink(path);
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Another process already removed it.
        }
    }

    /// <inheritdoc />
    public EngineDescriptor? ReadEngineDescriptor(string stateDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDir);
        string path = Path.Combine(Path.GetFullPath(stateDir), EngineDescriptorFile);
        return File.Exists(path) ? ReadEngineDescriptorFromFile(path) : null;
    }

    /// <summary>Reads and parses one engine descriptor file, returning <see langword="null"/> when it is absent or malformed.</summary>
    /// <param name="path">Descriptor file path.</param>
    /// <returns>The descriptor, or <see langword="null"/>.</returns>
    internal static EngineDescriptor? ReadEngineDescriptorFromFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            AtomicFile.RefuseSymlink(path);
            return JsonSerializer.Deserialize<EngineDescriptor>(File.ReadAllText(path, Encoding.UTF8), EngineJsonOptions);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public void WriteEngineDescriptor(string stateDir, EngineDescriptor descriptor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDir);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(descriptor.Pid);

        string fullDir = AtomicFile.EnsureSafeDirectory(stateDir);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(descriptor, EngineJsonOptions);
        AtomicFile.Write(Path.Combine(fullDir, EngineDescriptorFile), json, descriptor.Pid);
    }

    /// <inheritdoc />
    public void DeleteEngineDescriptor(string stateDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDir);
        string path = Path.Combine(Path.GetFullPath(stateDir), EngineDescriptorFile);
        if (!File.Exists(path))
            return;

        AtomicFile.RefuseSymlink(path);
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Another process already removed it.
        }
    }

    /// <inheritdoc />
    public async Task<(EngineProbeState State, EngineDescriptor? Holder)> ProbeEngineAsync(
        string stateDir,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDir);
        (EngineProbeState status, EngineDescriptor? descriptor) =
            await SendEngineIpcAsync(stateDir, "{\"op\":\"info\"}\n", cancellationToken).ConfigureAwait(false);
        return (status, descriptor);
    }

    /// <inheritdoc />
    public async Task<bool> RequestEngineShutdownAsync(
        string stateDir,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDir);
        (EngineProbeState status, _) =
            await SendEngineIpcAsync(stateDir, "{\"op\":\"shutdown\"}\n", cancellationToken).ConfigureAwait(false);
        return status == EngineProbeState.Running;
    }

    /// <inheritdoc />
    public async Task<(IEngineOwnerLease? Lease, EngineDescriptor? ExistingHolder)> TryAcquireEngineAsync(
        string stateDir,
        string version,
        string exePath,
        Action onShutdownRequested,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        ArgumentNullException.ThrowIfNull(onShutdownRequested);

        string socketPath = EngineSocketPath(stateDir);
        string recordFile = Path.Combine(Path.GetFullPath(stateDir), EngineDescriptorFile);

        for (int attempt = 0; attempt < 2; attempt++)
        {
            EngineDescriptor record = new(
                Pid: Environment.ProcessId,
                Exe: exePath,
                Version: version,
                StartedAt: timeProvider.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
            EngineOwner? owner = EngineOwner.TryBindInternal(socketPath, recordFile, record, onShutdownRequested, this);
            if (owner is not null)
                return (owner, null);

            (EngineProbeState status, EngineDescriptor? holder) = await ProbeEngineAsync(stateDir, cancellationToken).ConfigureAwait(false);
            if (status == EngineProbeState.Running && holder is not null)
            {
                // The live owner is authoritative. If its descriptor file was removed underneath it, restore
                // the published endpoint so a second client can still resolve the running engine.
                if (!File.Exists(recordFile))
                {
                    try
                    {
                        WriteEngineDescriptor(stateDir, holder);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        return (null, holder);
                    }
                }

                return (null, holder);
            }

            if (status == EngineProbeState.Unresponsive)
                return (null, null);

            // A stale socket file with no listener behind it: remove it and retry once.
            try
            {
                if (File.Exists(socketPath))
                    File.Delete(socketPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return (null, null);
            }
        }

        return (null, null);
    }

    /// <inheritdoc />
    public IDisposable? TryAcquireJobsLease(string stateDir) =>
        TryAcquireJobsLease(stateDir, pid: null, now: null);

    /// <summary>
    /// Attempts to take the exclusive <c>jobs.lock</c> lease without blocking, reclaiming a lock whose
    /// owner has exited.
    /// </summary>
    /// <param name="stateDir">State directory holding the lock file.</param>
    /// <param name="pid">Process id to record as owner; defaults to the current process.</param>
    /// <param name="now">Acquisition timestamp; defaults to now.</param>
    /// <returns>The lease, or <see langword="null"/> when another live process holds it.</returns>
    public JobsLease? TryAcquireJobsLease(string stateDir, int? pid, DateTimeOffset? now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDir);
        string fullDir = AtomicFile.EnsureSafeDirectory(stateDir);

        string lockPath = Path.Combine(fullDir, JobsLockFile);
        AtomicFile.RefuseSymlink(lockPath);

        int ownerPid = pid ?? Environment.ProcessId;
        string acquiredAt = (now ?? timeProvider.GetUtcNow()).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new { pid = ownerPid, acquiredAt });

        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using FileStream stream = new(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                stream.Write(payload, 0, payload.Length);
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(lockPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

                return new JobsLease(lockPath, ownerPid);
            }
            catch (IOException)
            {
                AtomicFile.RefuseSymlink(lockPath);
                if (!JobsLease.TryReadLockPid(lockPath, out int existingPid))
                {
                    // A lock that cannot be read is corrupt, not a live holder. Reclaim it once it is
                    // old enough that a concurrent writer would have finished, so one bad file
                    // cannot deny the jobs lease forever.
                    if (!TryReclaimCorruptLock(lockPath))
                        return null;

                    continue;
                }

                if (JobsLease.IsProcessAlive(existingPid))
                    return null;

                try
                {
                    File.Delete(lockPath);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    return null;
                }
            }
        }

        return null;
    }

    /// <summary>How long a corrupt <c>jobs.lock</c> is left alone in case a writer is mid-write.</summary>
    private static readonly TimeSpan CorruptLockGracePeriod = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Deletes a <c>jobs.lock</c> that holds no readable owner, or reports <see langword="false"/>
    /// while it may still be a lock being written.
    /// </summary>
    private bool TryReclaimCorruptLock(string lockPath)
    {
        try
        {
            if (!File.Exists(lockPath))
                return true;

            if (timeProvider.GetUtcNow().UtcDateTime - File.GetLastWriteTimeUtc(lockPath) < CorruptLockGracePeriod)
                return false;

            File.Delete(lockPath);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task<(EngineProbeState Status, EngineDescriptor? Record)> SendEngineIpcAsync(
        string stateDir,
        string requestLine,
        CancellationToken cancellationToken)
    {
        string socketPath = EngineSocketPath(stateDir);
        if (!File.Exists(socketPath))
            return (EngineProbeState.NotRunning, null);

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(EngineQueryTimeoutMs);

        try
        {
            using Socket client = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await client.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), timeout.Token).ConfigureAwait(false);
            byte[] payload = Encoding.UTF8.GetBytes(requestLine);
            await client.SendAsync(payload, SocketFlags.None, timeout.Token).ConfigureAwait(false);

            byte[] buffer = new byte[4096];
            int total = 0;
            while (total < buffer.Length)
            {
                int read = await client.ReceiveAsync(buffer.AsMemory(total), SocketFlags.None, timeout.Token).ConfigureAwait(false);
                if (read == 0)
                    break;
                total += read;
                if (Array.IndexOf(buffer, (byte)'\n', 0, total) >= 0)
                    break;
            }

            string replyText = Encoding.UTF8.GetString(buffer, 0, total).Trim();
            if (replyText.Length == 0)
                return (EngineProbeState.Unresponsive, null);

            EngineDescriptor? record = JsonSerializer.Deserialize<EngineDescriptor>(replyText, EngineJsonOptions);
            return record is not null
                ? (EngineProbeState.Running, record)
                : (EngineProbeState.Unresponsive, null);
        }
        catch (SocketException exception) when (exception.SocketErrorCode is not (SocketError.TimedOut or SocketError.WouldBlock))
        {
            // Refused, absent, does not exist, or the path is not a socket at all (a regular file left by an
            // interrupted write): no engine is listening there.
            return (EngineProbeState.NotRunning, null);
        }
        catch (IOException)
        {
            return (EngineProbeState.Unresponsive, null);
        }
        catch (JsonException)
        {
            return (EngineProbeState.Unresponsive, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (EngineProbeState.Unresponsive, null);
        }
    }
}
