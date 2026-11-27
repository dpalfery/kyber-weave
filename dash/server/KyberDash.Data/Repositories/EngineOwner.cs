using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using KyberDash.Contracts.Services;
using KyberDash.Domain;

namespace KyberDash.Data.Repositories;

/// <summary>
/// Single-engine lease backed by a Unix domain socket plus the <c>engine.json</c> descriptor.
/// Binding the socket elects the owner; every other process sees the descriptor and can ask it for
/// its info or request a graceful shutdown.
/// </summary>
public class EngineOwner : IEngineOwnerLease
{
    private readonly Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
    private readonly CancellationTokenSource shutdown = new();
    private readonly Lock recordGate = new();
    private readonly string socketPath;
    private readonly string recordFile;
    private readonly Action onShutdownRequested;
    private readonly IEngineDescriptorPublisher? publisher;
    private EngineDescriptor record;
    private Task? acceptLoop;
    private bool disposed;

    private EngineOwner(
        string socketPath,
        string recordFile,
        EngineDescriptor record,
        Action onShutdownRequested,
        IEngineDescriptorPublisher? publisher)
    {
        this.socketPath = socketPath;
        this.recordFile = recordFile;
        this.record = record;
        this.onShutdownRequested = onShutdownRequested;
        this.publisher = publisher;
    }

    /// <summary>
    /// Binds <paramref name="socketPath"/>, publishes the descriptor, and starts the accept loop.
    /// </summary>
    /// <param name="socketPath">Unix domain socket path to bind.</param>
    /// <param name="recordFile">Path to <c>engine.json</c>.</param>
    /// <param name="record">Descriptor describing this process.</param>
    /// <param name="onShutdownRequested">Invoked when a client sends the <c>shutdown</c> operation.</param>
    /// <returns>The owner lease, or <see langword="null"/> when another process already holds the socket.</returns>
    public static EngineOwner? TryBind(
        string socketPath,
        string recordFile,
        EngineDescriptor record,
        Action onShutdownRequested) =>
        TryBindInternal(socketPath, recordFile, record, onShutdownRequested, publisher: null);

    /// <summary>Binds the socket and publishes the descriptor through <paramref name="publisher"/>.</summary>
    internal static EngineOwner? TryBindInternal(
        string socketPath,
        string recordFile,
        EngineDescriptor record,
        Action onShutdownRequested,
        IEngineDescriptorPublisher? publisher)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(socketPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(recordFile);
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(onShutdownRequested);

        EngineOwner? candidate = null;
        try
        {
            candidate = new EngineOwner(Path.GetFullPath(socketPath), Path.GetFullPath(recordFile), record, onShutdownRequested, publisher);
            candidate.listener.Bind(new UnixDomainSocketEndPoint(candidate.socketPath));
            candidate.listener.Listen(8);
            candidate.WriteRecord();
            candidate.acceptLoop = candidate.AcceptLoopAsync(candidate.shutdown.Token);
            EngineOwner owned = candidate;
            candidate = null;
            return owned;
        }
        catch (Exception exception) when (exception is SocketException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            candidate?.Dispose();
        }
    }

    /// <inheritdoc />
    public void UpdateUrl(Uri endpointUrl)
    {
        ArgumentNullException.ThrowIfNull(endpointUrl);
        lock (recordGate)
        {
            record = record with { Url = endpointUrl };
            WriteRecordLocked();
        }
    }

    /// <summary>Descriptor currently published for this owner.</summary>
    public EngineDescriptor Descriptor
    {
        get
        {
            lock (recordGate)
                return record;
        }
    }

    /// <summary>
    /// Stops the accept loop and closes the listener. The socket file and descriptor are left in place:
    /// on Unix an unlinked socket cannot be rebound while a connection to it is still open, so a lease that
    /// deletes its own socket hands the next process a path it cannot use until the connection drains.
    /// The next owner's stale-socket path replaces them.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (disposed)
                return;

            disposed = true;
            shutdown.Cancel();
            listener.Dispose();
            shutdown.Dispose();
        }
    }

    /// <summary>Stops the accept loop, waits for it, and closes the listener, leaving the files for the next owner.</summary>
    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;

        disposed = true;
        await shutdown.CancelAsync().ConfigureAwait(false);
        listener.Dispose();
        if (acceptLoop is not null)
        {
            try
            {
                await acceptLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cancellation is the expected way the loop ends.
            }
        }

        shutdown.Dispose();
        GC.SuppressFinalize(this);
    }

    private void WriteRecord()
    {
        lock (recordGate)
            WriteRecordLocked();
    }

    private void WriteRecordLocked()
    {
        try
        {
            if (publisher is null)
                AtomicFile.Write(recordFile, JsonSerializer.SerializeToUtf8Bytes(record, ServerStateRepository.EngineJsonOptions));
            else
                publisher.WriteEngineDescriptor(Path.GetDirectoryName(recordFile)!, record);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // The socket still owns the engine; a descriptor that cannot be written is retried on the next update.
        }
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await listener.AcceptAsync(token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                break;
            }

            _ = HandleClientAsync(client, token);
        }
    }

    private async Task HandleClientAsync(Socket client, CancellationToken token)
    {
        using (client)
        {
            try
            {
                byte[] buffer = new byte[1024];
                int read = await client.ReceiveAsync(buffer, SocketFlags.None, token).ConfigureAwait(false);
                if (read <= 0)
                    return;

                string line = Encoding.UTF8.GetString(buffer, 0, read).Trim();
                using JsonDocument doc = JsonDocument.Parse(line);
                if (!doc.RootElement.TryGetProperty("op", out JsonElement opEl) || opEl.ValueKind != JsonValueKind.String)
                    return;

                string? op = opEl.GetString();
                if (op is not ("info" or "shutdown"))
                    return;

                string replyJson;
                lock (recordGate)
                    replyJson = JsonSerializer.Serialize(record, ServerStateRepository.EngineJsonOptions);

                byte[] reply = Encoding.UTF8.GetBytes(replyJson + "\n");
                await client.SendAsync(reply, SocketFlags.None, token).ConfigureAwait(false);

                // The callback may stop the host this loop runs on. It is invoked from the thread pool so a
                // synchronous stop can never deadlock the client that asked for it.
                if (op == "shutdown")
                    _ = Task.Run(onShutdownRequested, CancellationToken.None);
            }
            catch (Exception exception) when (exception is JsonException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // A client that sends a malformed request or disconnects is ignored.
            }
        }
    }
}
