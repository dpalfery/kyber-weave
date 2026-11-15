using System.Text.Json;
using KyberDash.Api;
using KyberDash.Api.Endpoints;
using KyberDash.Contracts.Services;
using KyberDash.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace KyberDash.Presentation.Tests;

/// <summary>
/// Hosts the real endpoint map in a <see cref="TestServer"/> while substituting the application
/// services behind it.
/// </summary>
/// <remarks>
/// The endpoint map, routing, request parsing, and response shaping stay real; only the services
/// that read the canonical store or bind sockets are replaced. Tests build an application with the
/// real composition root, then add their mocks to the same collection the application's own
/// registrations went into, so a mock always wins over the real registration.
/// </remarks>
public static class PresentationTestHost
{
    /// <summary>Starts an application whose endpoints resolve the supplied mocks.</summary>
    /// <param name="substitutes">One mocked instance per service the endpoints resolve.</param>
    /// <param name="settings">Transport settings to bind.</param>
    /// <returns>The started application.</returns>
    public static async Task<WebApplication> StartAsync(IReadOnlyCollection<object> substitutes, DashRuntimeSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(substitutes);

        DashRuntimeSettings runtime = settings ?? new DashRuntimeSettings
        {
            StateDirectory = Path.GetTempPath(),
            WebPort = 0,
            WebPortExplicit = true,
            OpenBrowser = false,
        };

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.WebHost.UseSetting(WebHostDefaults.ServerUrlsKey, "http://127.0.0.1:0");
        builder.Services.AddSingleton(runtime);

        // Register the mocks before the composition root, which uses TryAdd: a substitute then wins
        // over the production registration instead of being shadowed by it.
        foreach (object substitute in substitutes)
        {
            Type serviceType = PrimaryContract(substitute);
            builder.Services.AddSingleton(serviceType, substitute);
        }

        Program.ConfigureServices(builder.Services, runtime);

        WebApplication app = builder.Build();
        app.MapDashApiEndpoints();
        app.MapOtlpEndpoints();
        await app.StartAsync().ConfigureAwait(false);
        return app;
    }

    /// <summary>Creates a client bound to a started test application.</summary>
    /// <param name="app">The started application.</param>
    /// <returns>A client whose base address is the test server.</returns>
    public static HttpClient CreateClient(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.GetTestClient();
    }

    /// <summary>Encodes a value the way an endpoint would return it.</summary>
    /// <param name="value">The value to encode.</param>
    /// <returns>The value as a JSON element.</returns>
    public static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    /// <summary>Empty JSON object, used where the payload content is irrelevant.</summary>
    public static JsonElement Empty { get; } = JsonSerializer.SerializeToElement(new { });

    /// <summary>Finds the first <c>KyberDash.*</c> interface a mock implements.</summary>
    private static Type PrimaryContract(object substitute)
    {
        Type[] interfaces = substitute.GetType().GetInterfaces();
        foreach (Type candidate in interfaces)
        {
            string? ns = candidate.Namespace;
            if (ns is not null && ns.StartsWith("KyberDash.", StringComparison.Ordinal))
                return candidate;
        }

        throw new ArgumentException($"'{substitute.GetType().FullName}' does not implement a KyberDash contract.", nameof(substitute));
    }
}
