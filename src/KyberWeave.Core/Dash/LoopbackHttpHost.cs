using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace KyberWeave.Core.Dash;

internal sealed class LoopbackHttpRequest
{
    public required string Method { get; init; }
    public required string Path { get; init; }
    public required string RawTarget { get; init; }
    public required IReadOnlyDictionary<string, string> Query { get; init; }
    public required IReadOnlyDictionary<string, string> Headers { get; init; }
    public required byte[] Body { get; init; }
    public IPAddress? RemoteAddress { get; init; }

    public string? GetHeader(string name)
    {
        return Headers.TryGetValue(name, out string? value) ? value : null;
    }

    public string? GetQuery(string name)
    {
        return Query.TryGetValue(name, out string? value) ? value : null;
    }
}

internal sealed class LoopbackHttpResponse
{
    public int StatusCode { get; init; } = 200;
    public string ContentType { get; init; } = "application/json; charset=utf-8";
    public byte[] Body { get; init; } = [];
    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool ForceClose { get; init; }
    public Func<Stream, CancellationToken, Task>? SseStreamHandler { get; init; }

    public static LoopbackHttpResponse Json(int statusCode, object payload, bool forceClose = false)
    {
        return new LoopbackHttpResponse
        {
            StatusCode = statusCode,
            ContentType = "application/json; charset=utf-8",
            Body = JsonSerializer.SerializeToUtf8Bytes(payload),
            ForceClose = forceClose,
        };
    }

    public static LoopbackHttpResponse Text(int statusCode, string text, string contentType = "text/plain; charset=utf-8")
    {
        return new LoopbackHttpResponse
        {
            StatusCode = statusCode,
            ContentType = contentType,
            Body = Encoding.UTF8.GetBytes(text),
        };
    }

    public static LoopbackHttpResponse Sse(Func<Stream, CancellationToken, Task> handler)
    {
        return new LoopbackHttpResponse
        {
            StatusCode = 200,
            ContentType = "text/event-stream; charset=utf-8",
            ForceClose = true,
            SseStreamHandler = handler,
        };
    }
}

/// <summary>
/// Minimal loopback-only HTTP/1.1 host over <see cref="TcpListener"/> with pre-body size
/// enforcement, <c>Content-Length</c> and chunked framing, keep-alive, and SSE streaming.
/// </summary>
internal sealed class LoopbackHttpHost : IAsyncDisposable, IDisposable
{
    private const int MaxHeaderBytes = 64 * 1024;

    private readonly TcpListener _listener;
    private readonly Func<LoopbackHttpRequest, int> _maxBodyBytesResolver;
    private readonly Func<LoopbackHttpRequest, CancellationToken, Task<LoopbackHttpResponse>> _handler;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _clientsLock = new();
    private readonly HashSet<TcpClient> _activeClients = [];
    private Task? _acceptLoop;
    private bool _disposed;

    public LoopbackHttpHost(
        int port,
        Func<LoopbackHttpRequest, int> maxBodyBytesResolver,
        Func<LoopbackHttpRequest, CancellationToken, Task<LoopbackHttpResponse>> handler)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
        _maxBodyBytesResolver = maxBodyBytesResolver;
        _handler = handler;
    }

    public int Port { get; private set; }

    public bool IsListening { get; private set; }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsListening)
            return;

        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        IsListening = true;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        IsListening = false;
        await _cts.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        _listener.Dispose();

        TcpClient[] clients;
        lock (_clientsLock)
        {
            clients = [.. _activeClients];
            _activeClients.Clear();
        }

        foreach (TcpClient client in clients)
        {
            try
            {
                client.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (SocketException)
            {
            }
        }

        _cts.Dispose();
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            client.NoDelay = true;
            lock (_clientsLock)
                _activeClients.Add(client);

            _ = Task.Run(() => HandleClientAsync(client, token), CancellationToken.None);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        try
        {
            using NetworkStream stream = client.GetStream();
            IPAddress? remoteAddress = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
            byte[] readBuf = ArrayPool<byte>.Shared.Rent(16 * 1024);
            try
            {
                int bufferedStart = 0;
                int bufferedEnd = 0;

                while (!token.IsCancellationRequested)
                {
                    (LoopbackHttpRequest? request, bool keepAlive, bool tooLarge, int tooLargeLimit, bool badRequest) =
                        await ReadRequestAsync(
                            stream,
                            readBuf,
                            state =>
                            {
                                bufferedStart = state.Start;
                                bufferedEnd = state.End;
                            },
                            bufferedStart,
                            bufferedEnd,
                            remoteAddress,
                            token).ConfigureAwait(false);

                    if (badRequest)
                    {
                        LoopbackHttpResponse bad = LoopbackHttpResponse.Json(400, new { error = "Malformed HTTP request" }, forceClose: true);
                        await WriteResponseAsync(stream, bad, keepAlive: false, token).ConfigureAwait(false);
                        break;
                    }

                    if (tooLarge)
                    {
                        LoopbackHttpResponse oversize = LoopbackHttpResponse.Json(
                            413,
                            new
                            {
                                error = new
                                {
                                    code = "PAYLOAD_TOO_LARGE",
                                    message = string.Create(CultureInfo.InvariantCulture, $"body exceeds the {tooLargeLimit}-byte limit"),
                                },
                            },
                            forceClose: true);
                        await WriteResponseAsync(stream, oversize, keepAlive: false, token).ConfigureAwait(false);
                        break;
                    }

                    if (request is null)
                        break;

                    LoopbackHttpResponse response = await _handler(request, token).ConfigureAwait(false);

                    bool sendKeepAlive = keepAlive && !response.ForceClose;
                    await WriteResponseAsync(stream, response, sendKeepAlive, token).ConfigureAwait(false);
                    if (response.SseStreamHandler is not null)
                    {
                        await response.SseStreamHandler(stream, token).ConfigureAwait(false);
                        break;
                    }

                    if (!sendKeepAlive)
                        break;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(readBuf);
            }
        }
        catch (IOException)
        {
        }
        catch (SocketException)
        {
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            lock (_clientsLock)
                _activeClients.Remove(client);
            client.Dispose();
        }
    }

    private readonly record struct BufferSlice(int Start, int End);

    private async Task<(LoopbackHttpRequest? Request, bool KeepAlive, bool TooLarge, int TooLargeLimit, bool BadRequest)>
        ReadRequestAsync(
            NetworkStream stream,
            byte[] readBuf,
            Action<BufferSlice> updateOffsets,
            int initialStart,
            int initialEnd,
            IPAddress? remoteAddress,
            CancellationToken token)
    {
        int start = initialStart;
        int end = initialEnd;

        using MemoryStream headerStream = new();
        int headerEndOffset = -1;
        while (true)
        {
            if (start < end)
            {
                int prevLen = (int)headerStream.Length;
                await headerStream.WriteAsync(readBuf.AsMemory(start, end - start), token).ConfigureAwait(false);
                byte[] combined = headerStream.GetBuffer();
                int combinedLen = (int)headerStream.Length;
                int searchFrom = Math.Max(0, prevLen - 3);
                int idx = IndexOfHeaderEnd(combined.AsSpan(0, combinedLen), searchFrom);
                if (idx >= 0)
                {
                    int bodyStartInCombined = idx + 4;
                    int consumedFromCurrentChunk = bodyStartInCombined - prevLen;
                    start += consumedFromCurrentChunk;
                    headerStream.SetLength(idx);
                    headerEndOffset = idx;
                    break;
                }

                start = end = 0;
            }

            if (headerStream.Length > MaxHeaderBytes)
            {
                updateOffsets(new BufferSlice(0, 0));
                return (null, false, false, 0, true);
            }

            int n = await stream.ReadAsync(readBuf.AsMemory(0, readBuf.Length), token).ConfigureAwait(false);
            if (n == 0)
            {
                updateOffsets(new BufferSlice(0, 0));
                return (null, false, false, 0, headerStream.Length > 0);
            }

            start = 0;
            end = n;
        }

        string headerText = Encoding.ASCII.GetString(headerStream.GetBuffer(), 0, headerEndOffset);
        string[] lines = headerText.Split("\r\n");
        if (lines.Length == 0)
        {
            updateOffsets(new BufferSlice(start, end));
            return (null, false, false, 0, true);
        }

        string[] requestLine = lines[0].Split(' ', 3, StringSplitOptions.None);
        if (requestLine.Length < 3 || string.IsNullOrEmpty(requestLine[0]) || string.IsNullOrEmpty(requestLine[1]))
        {
            updateOffsets(new BufferSlice(start, end));
            return (null, false, false, 0, true);
        }

        string method = requestLine[0].ToUpperInvariant();
        string rawTarget = requestLine[1];
        string httpVersion = requestLine[2];

        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Length == 0)
                continue;

            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                updateOffsets(new BufferSlice(start, end));
                return (null, false, false, 0, true);
            }

            string name = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim();
            headers[name] = headers.TryGetValue(name, out string? existing)
                ? $"{existing}, {value}"
                : value;
        }

        (string path, Dictionary<string, string> query) = ParseTarget(rawTarget);
        LoopbackHttpRequest preview = new()
        {
            Method = method,
            Path = path,
            RawTarget = rawTarget,
            Query = query,
            Headers = headers,
            Body = [],
            RemoteAddress = remoteAddress,
        };

        int maxBodyBytes = _maxBodyBytesResolver(preview);
        bool keepAlive = !string.Equals(httpVersion, "HTTP/1.0", StringComparison.OrdinalIgnoreCase);
        if (headers.TryGetValue("Connection", out string? connVal))
        {
            if (connVal.Contains("close", StringComparison.OrdinalIgnoreCase))
                keepAlive = false;
            else if (connVal.Contains("keep-alive", StringComparison.OrdinalIgnoreCase))
                keepAlive = true;
        }

        byte[] bodyBytes;
        bool isChunked = headers.TryGetValue("Transfer-Encoding", out string? te)
            && te.Contains("chunked", StringComparison.OrdinalIgnoreCase);

        if (isChunked)
        {
            if (headers.TryGetValue("Expect", out string? expectVal)
                && expectVal.Contains("100-continue", StringComparison.OrdinalIgnoreCase))
            {
                await stream.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray(), token).ConfigureAwait(false);
            }

            (byte[]? chunkedBody, BufferSlice next, bool chunkTooLarge, bool chunkBadRequest) = await ReadChunkedBodyAsync(
                stream,
                readBuf,
                start,
                end,
                maxBodyBytes,
                token).ConfigureAwait(false);
            if (chunkTooLarge)
            {
                updateOffsets(new BufferSlice(0, 0));
                return (null, false, true, maxBodyBytes, false);
            }

            if (chunkBadRequest || chunkedBody is null)
            {
                updateOffsets(new BufferSlice(0, 0));
                return (null, false, false, 0, true);
            }

            bodyBytes = chunkedBody;
            updateOffsets(next);
        }
        else if (headers.TryGetValue("Content-Length", out string? contentLenRaw))
        {
            if (!long.TryParse(contentLenRaw.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long contentLength))
            {
                updateOffsets(new BufferSlice(0, 0));
                return (null, false, false, 0, true);
            }

            if (contentLength > maxBodyBytes)
            {
                updateOffsets(new BufferSlice(0, 0));
                return (null, false, true, maxBodyBytes, false);
            }

            if (headers.TryGetValue("Expect", out string? expectVal)
                && expectVal.Contains("100-continue", StringComparison.OrdinalIgnoreCase))
            {
                await stream.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray(), token).ConfigureAwait(false);
            }

            int expected = (int)contentLength;
            bodyBytes = new byte[expected];
            int copied = 0;
            if (start < end && expected > 0)
            {
                int take = Math.Min(expected, end - start);
                Buffer.BlockCopy(readBuf, start, bodyBytes, 0, take);
                start += take;
                copied += take;
            }

            while (copied < expected)
            {
                int n = await stream.ReadAsync(bodyBytes.AsMemory(copied, expected - copied), token).ConfigureAwait(false);
                if (n == 0)
                {
                    updateOffsets(new BufferSlice(0, 0));
                    return (null, false, false, 0, true);
                }

                copied += n;
            }

            updateOffsets(new BufferSlice(start, end));
        }
        else
        {
            bodyBytes = [];
            updateOffsets(new BufferSlice(start, end));
        }

        LoopbackHttpRequest completed = new()
        {
            Method = method,
            Path = path,
            RawTarget = rawTarget,
            Query = query,
            Headers = headers,
            Body = bodyBytes,
            RemoteAddress = remoteAddress,
        };
        return (completed, keepAlive, false, 0, false);
    }

    private static async Task<(byte[]? Body, BufferSlice Next, bool TooLarge, bool BadRequest)> ReadChunkedBodyAsync(
        NetworkStream stream,
        byte[] readBuf,
        int start,
        int end,
        int maxBodyBytes,
        CancellationToken token)
    {
        using MemoryStream accumulator = new();

        async Task<string?> ReadLineAsync()
        {
            using MemoryStream lineBuf = new();
            while (true)
            {
                while (start < end)
                {
                    byte b = readBuf[start++];
                    if (b == (byte)'\n')
                    {
                        byte[] arr = lineBuf.GetBuffer();
                        int len = (int)lineBuf.Length;
                        if (len > 0 && arr[len - 1] == (byte)'\r')
                            len--;
                        return Encoding.ASCII.GetString(arr, 0, len);
                    }

                    lineBuf.WriteByte(b);
                    if (lineBuf.Length > 4096)
                        return null;
                }

                int n = await stream.ReadAsync(readBuf.AsMemory(0, readBuf.Length), token).ConfigureAwait(false);
                if (n == 0)
                    return null;
                start = 0;
                end = n;
            }
        }

        while (true)
        {
            string? sizeLine = await ReadLineAsync().ConfigureAwait(false);
            if (sizeLine is null)
                return (null, new BufferSlice(start, end), false, true);

            int semi = sizeLine.IndexOf(';', StringComparison.Ordinal);
            string hexSize = (semi >= 0 ? sizeLine[..semi] : sizeLine).Trim();
            if (!int.TryParse(hexSize, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int chunkSize) || chunkSize < 0)
                return (null, new BufferSlice(start, end), false, true);

            if (chunkSize == 0)
            {
                // Consume trailer lines until blank line
                while (true)
                {
                    string? trailer = await ReadLineAsync().ConfigureAwait(false);
                    if (trailer is null)
                        return (null, new BufferSlice(start, end), false, true);
                    if (trailer.Length == 0)
                        break;
                }

                return (accumulator.ToArray(), new BufferSlice(start, end), false, false);
            }

            if (accumulator.Length + chunkSize > maxBodyBytes)
                return (null, new BufferSlice(start, end), true, false);

            int remaining = chunkSize;
            while (remaining > 0)
            {
                if (start == end)
                {
                    int n = await stream.ReadAsync(readBuf.AsMemory(0, readBuf.Length), token).ConfigureAwait(false);
                    if (n == 0)
                        return (null, new BufferSlice(start, end), false, true);
                    start = 0;
                    end = n;
                }

                int take = Math.Min(remaining, end - start);
                await accumulator.WriteAsync(readBuf.AsMemory(start, take), token).ConfigureAwait(false);
                start += take;
                remaining -= take;
            }

            string? crlf = await ReadLineAsync().ConfigureAwait(false);
            if (crlf is null || crlf.Length != 0)
                return (null, new BufferSlice(start, end), false, true);
        }
    }

    private static int IndexOfHeaderEnd(ReadOnlySpan<byte> span, int startFrom)
    {
        for (int i = startFrom; i <= span.Length - 4; i++)
        {
            if (span[i] == (byte)'\r' && span[i + 1] == (byte)'\n' && span[i + 2] == (byte)'\r' && span[i + 3] == (byte)'\n')
                return i;
        }

        return -1;
    }

    private static (string Path, Dictionary<string, string> Query) ParseTarget(string rawTarget)
    {
        int qIndex = rawTarget.IndexOf('?', StringComparison.Ordinal);
        string rawPath = qIndex >= 0 ? rawTarget[..qIndex] : rawTarget;
        string path = Uri.UnescapeDataString(rawPath);
        Dictionary<string, string> query = new(StringComparer.Ordinal);
        if (qIndex >= 0 && qIndex + 1 < rawTarget.Length)
        {
            string qs = rawTarget[(qIndex + 1)..];
            foreach (string pair in qs.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = pair.IndexOf('=', StringComparison.Ordinal);
                string rawKey = eq >= 0 ? pair[..eq] : pair;
                string rawVal = eq >= 0 ? pair[(eq + 1)..] : string.Empty;
                string key = Uri.UnescapeDataString(rawKey.Replace('+', ' '));
                string val = Uri.UnescapeDataString(rawVal.Replace('+', ' '));
                query[key] = val;
            }
        }

        return (path, query);
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream,
        LoopbackHttpResponse response,
        bool keepAlive,
        CancellationToken token)
    {
        StringBuilder sb = new();
        sb.Append(CultureInfo.InvariantCulture, $"HTTP/1.1 {response.StatusCode} {ReasonPhrase(response.StatusCode)}\r\n");
        sb.Append(CultureInfo.InvariantCulture, $"Content-Type: {response.ContentType}\r\n");
        if (response.SseStreamHandler is null)
            sb.Append(CultureInfo.InvariantCulture, $"Content-Length: {response.Body.Length}\r\n");
        else
            sb.Append("Cache-Control: no-cache\r\n");

        sb.Append(keepAlive ? "Connection: keep-alive\r\n" : "Connection: close\r\n");
        foreach (KeyValuePair<string, string> header in response.Headers)
            sb.Append(CultureInfo.InvariantCulture, $"{header.Key}: {header.Value}\r\n");

        sb.Append("\r\n");
        byte[] headerBytes = Encoding.ASCII.GetBytes(sb.ToString());
        await stream.WriteAsync(headerBytes, token).ConfigureAwait(false);
        if (response.Body.Length > 0)
            await stream.WriteAsync(response.Body, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    private static string ReasonPhrase(int statusCode)
    {
        return statusCode switch
        {
            200 => "OK",
            201 => "Created",
            204 => "No Content",
            400 => "Bad Request",
            403 => "Forbidden",
            404 => "Not Found",
            405 => "Method Not Allowed",
            413 => "Payload Too Large",
            415 => "Unsupported Media Type",
            500 => "Internal Server Error",
            503 => "Service Unavailable",
            _ => "OK",
        };
    }
}
