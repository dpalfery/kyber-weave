using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KyberWeave.Mcp;

/// <summary>
/// A stream transport that still answers the requests it has already read after its input
/// reaches end of stream.
/// </summary>
/// <remarks>
/// <para>
/// The SDK's <see cref="StreamServerTransport"/> marks itself disconnected the moment its read
/// loop sees end of stream, and its send path returns without writing anything once it is
/// disconnected. The session, meanwhile, waits for every handler already in flight before it
/// ends. A client that writes its requests and then closes stdin — a scripted health check,
/// <c>kyber-weave-mcp &lt; requests.jsonl</c>, a CI probe — therefore saw every handler run to
/// completion and every response discarded: exit 0 and zero bytes on stdout. Nothing on stderr
/// said so, because the SDK does not log the drop.
/// </para>
/// <para>
/// End of stream on stdin means the client will send nothing more. It does not mean the client
/// has stopped reading. So a response or error for a request that was read is written
/// regardless. Everything else — notifications, and requests the server would originate — keeps
/// the SDK's rule, because once the input is closed nothing can answer a request.
/// </para>
/// <para>
/// This overrides the send path rather than wrapping the SDK transport, because the base read
/// loop sends its own parse-error replies through the virtual <see cref="SendMessageAsync"/>.
/// A wrapper would leave those writing to stdout under a different lock from ours, and two
/// messages could interleave on the wire. Checked against ModelContextProtocol 2.2.0, whose
/// behaviour here is unchanged on the SDK's main branch as of September 2026.
/// </para>
/// </remarks>
public sealed class DrainingStreamServerTransport : StreamServerTransport
{
    private static readonly byte[] Newline = "\n"u8.ToArray();

    private static readonly JsonTypeInfo<JsonRpcMessage> MessageTypeInfo =
        (JsonTypeInfo<JsonRpcMessage>)McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(JsonRpcMessage));

    [SuppressMessage(
        "Usage",
        "CA2213:Disposable fields should be disposed",
        Justification = "The base transport owns the output stream and disposes it in DisposeAsync.")]
    private readonly Stream _output;

    [SuppressMessage(
        "Usage",
        "CA2213:Disposable fields should be disposed",
        Justification = "A SemaphoreSlim holds nothing to release unless its wait handle is used, and disposing it would turn a late send into ObjectDisposedException instead of the IOException callers expect. The SDK does not dispose its own send lock either.")]
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private readonly ILogger _logger;

    /// <summary>Creates a transport over an explicit pair of streams.</summary>
    /// <param name="input">The stream requests are read from.</param>
    /// <param name="output">The stream responses are written to. The transport owns it.</param>
    /// <param name="serverName">The server name used in the transport's log lines.</param>
    /// <param name="loggerFactory">The logger factory for the transport's log lines.</param>
    public DrainingStreamServerTransport(
        Stream input,
        Stream output,
        string? serverName = null,
        ILoggerFactory? loggerFactory = null)
        : base(input, output, serverName, loggerFactory)
    {
        _output = output;
        _logger = loggerFactory?.CreateLogger<DrainingStreamServerTransport>() ?? NullLogger<DrainingStreamServerTransport>.Instance;
    }

    /// <summary>Creates a transport over the process's stdin and stdout.</summary>
    /// <param name="serverName">The server name used in the transport's log lines.</param>
    /// <param name="loggerFactory">The logger factory for the transport's log lines.</param>
    /// <returns>A transport that reads stdin and writes stdout.</returns>
    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The transport takes ownership of both streams and disposes them in DisposeAsync.")]
    public static DrainingStreamServerTransport ForStandardStreams(string serverName, ILoggerFactory? loggerFactory) =>
        new(
            new CancellableStandardInput(Console.OpenStandardInput()),
            new BufferedStream(Console.OpenStandardOutput()),
            serverName,
            loggerFactory);

    /// <inheritdoc />
    public override async Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
    {
        if (!IsConnected && message is not (JsonRpcResponse or JsonRpcError))
        {
            return;
        }

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(message, MessageTypeInfo);

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _output.WriteAsync(json, cancellationToken).ConfigureAwait(false);
            await _output.WriteAsync(Newline, cancellationToken).ConfigureAwait(false);
            await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The session sends replies from fire-and-forget handlers, so the exception alone is
            // never observed; stderr is the only place a failed reply can show up. The throw keeps
            // the SDK's contract that a failed send surfaces as an IOException.
            string id = message is JsonRpcMessageWithId withId ? withId.Id.ToString() : "(no id)";
            _logger.LogError(ex, "{Transport} failed to send message {MessageId}.", Name, id);
            throw new IOException("Failed to send message.", ex);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// Makes a read from the console input stream observe cancellation.
    /// </summary>
    /// <remarks>
    /// Console input streams ignore cancellation tokens, and disposing one does not wake a read
    /// blocked in the operating system. Without this, a host shutting down while the client
    /// still holds stdin open would wait on a read that never returns. The SDK's stdio transport
    /// wraps stdin the same way; its wrapper is private, so this transport carries its own.
    /// </remarks>
    private sealed class CancellableStandardInput(Stream input) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            input.ReadAsync(buffer, offset, count, cancellationToken).WaitAsync(cancellationToken);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ValueTask<int> read = input.ReadAsync(buffer, cancellationToken);
            return read.IsCompletedSuccessfully ? read : new ValueTask<int>(read.AsTask().WaitAsync(cancellationToken));
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
            // Nothing is written to stdin, so there is nothing to flush.
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                input.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
