using System.Net;
using System.Net.Sockets;
using KyberWeave.Data.Docs.Analysis.Embeddings;
using Xunit;

namespace KyberWeave.Integration.Tests;

/// <summary>The real embedding transport against a real loopback socket: it connects to loopback and to nothing else.</summary>
public sealed class LoopbackEmbeddingTransportTests
{
    [Fact]
    public async Task ALoopbackServerIsReachedAndARedirectIsNotFollowed()
    {
        TcpListener server = new(IPAddress.Loopback, 0);
        server.Start();
        try
        {
            int port = ((IPEndPoint)server.LocalEndpoint).Port;
            Task serve = ServeOnce(server, "HTTP/1.1 302 Found\r\nLocation: http://203.0.113.9/\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            HttpStatusCode status = await StatusOf(new Uri($"http://127.0.0.1:{port}/"));
            await serve;

            Assert.Equal(HttpStatusCode.Found, status);
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task AnAddressOutsideLoopbackIsRefusedBeforeAnyConnectionIsMade()
    {
        HttpRequestException failure = await Assert.ThrowsAsync<HttpRequestException>(
            () => StatusOf(new Uri("http://203.0.113.9/")));

        Assert.Contains("loopback", failure.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LocalhostResolvesToLoopbackAddressesOnly()
    {
        IReadOnlyList<IPAddress> addresses = new LoopbackEmbeddingTransport().ResolveHost("localhost");

        Assert.NotEmpty(addresses);
        Assert.All(addresses, address => Assert.True(IPAddress.IsLoopback(address)));
    }

    private static async Task<HttpStatusCode> StatusOf(Uri uri)
    {
        using HttpClient client = new(new LoopbackEmbeddingTransport().CreateHandler());
        using HttpResponseMessage response = await client.GetAsync(uri, TestContext.Current.CancellationToken);
        return response.StatusCode;
    }

    private static async Task ServeOnce(TcpListener server, string response)
    {
        using TcpClient connection = await server.AcceptTcpClientAsync();
        NetworkStream stream = connection.GetStream();
        byte[] buffer = new byte[4096];
        _ = await stream.ReadAsync(buffer);
        await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(response));
        await stream.FlushAsync();
    }
}
