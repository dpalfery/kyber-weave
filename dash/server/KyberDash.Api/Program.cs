using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using KyberDash.Api.Endpoints;
using KyberDash.Application;
using KyberDash.Contracts.Services;
using KyberDash.Core.Exceptions;
using KyberDash.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KyberDash.Api;

/// <summary>
/// Entry point for <c>kyberdash-api</c>: the loopback REST/SSE dashboard server and OTLP/HTTP
/// collector.
/// </summary>
/// <remarks>
/// This host owns transport lifetime only. It binds sockets, takes the single-engine lease so two
/// dashboards cannot share a store, announces readiness on stdout, and tears those down again.
/// Everything it answers comes from the dependencies <see cref="ServiceCollectionExtensions.AddDashApplication"/>
/// registers.
/// </remarks>
public static partial class Program
{

    /// <summary>Marker written to stdout once the web listener answers, so launchers can wait for it.</summary>
    public const string ListeningEvent = "kyberdash.web.listening";

    /// <summary>API contract version reported in the listening line and by <c>/api/kyber/version</c>.</summary>
    public const int ApiContractVersion = 1;

    /// <summary>Port used when neither the command line, the environment, nor the settings name one.</summary>
    public const int DefaultWebPort = 4747;

    /// <summary>Port the OTLP collector prefers before falling back to an ephemeral one.</summary>
    public const int DefaultOtlpPort = OtlpCollector.DefaultOtlpPort;

    private static readonly JsonSerializerOptions ListeningJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Runs the collector and dashboard host.</summary>
    /// <param name="args">The process command line.</param>
    /// <returns>Process exit code: <c>0</c> on a clean shutdown, <c>1</c> on a startup failure, <c>2</c> on bad arguments.</returns>
    public static async Task<int> Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Contains("--version", StringComparer.Ordinal))
        {
            await Console.Out.WriteLineAsync(ResolveVersion()).ConfigureAwait(false);
            return 0;
        }

        if (args.Contains("--help", StringComparer.Ordinal) || args.Contains("-h", StringComparer.Ordinal))
        {
            DashCommandLine.WriteHelp(Console.Out);
            return 0;
        }

        DashRuntimeSettings runtime;
        try
        {
            runtime = DashCommandLine.Parse(args);
        }
        catch (DashValidationException ex)
        {
            await Console.Error.WriteLineAsync($"kyberdash-api: {ex.Message}").ConfigureAwait(false);
            return 2;
        }

        using CancellationTokenSource shutdown = new();
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;
            shutdown.Cancel();
        };
        Console.CancelKeyPress += onCancel;
        try
        {
            return await RunAsync(runtime, shutdown).ConfigureAwait(false);
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    /// <summary>
    /// Builds the host without starting it. Tests and embedding hosts call this to drive the real
    /// endpoint map against substituted services.
    /// </summary>
    /// <param name="settings">Transport settings; when omitted they are parsed from <paramref name="args"/>.</param>
    /// <param name="args">The process command line, used only when <paramref name="settings"/> is omitted.</param>
    /// <returns>The configured web application.</returns>
    public static WebApplication BuildApplication(DashRuntimeSettings? settings = null, string[]? args = null)
    {
        DashRuntimeSettings runtime = settings ?? DashCommandLine.Parse(args ?? []);
        WebApplicationOptions options = new()
        {
            Args = [],
            // The dashboard serves its own SPA; host-level appsettings loading is not wanted here.
            EnvironmentName = Environments.Production,
        };

        WebApplicationBuilder builder = WebApplication.CreateBuilder(options);
        builder.Logging.AddConsole();
        ConfigureServices(builder.Services, runtime);
        WebApplication app = builder.Build();
        app.MapDashApiEndpoints();
        app.MapOtlpEndpoints();
        return app;
    }

    /// <summary>Registers the application container and the transport settings a host binds.</summary>
    /// <param name="services">The service collection to populate.</param>
    /// <param name="settings">The transport settings to bind.</param>
    public static void ConfigureServices(IServiceCollection services, DashRuntimeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(settings);

        services.AddDashApplication(settings.ToDashOptions());
        services.AddSingleton(settings);
    }

    /// <summary>Resolves the reported version from the assembly's informational version.</summary>
    public static string ResolveVersion()
    {
        string? informational = typeof(Program).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), inherit: false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()
            ?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            int plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus >= 0 ? informational[..plus] : informational;
        }

        return "0.21.0";
    }

    private static async Task<int> RunAsync(DashRuntimeSettings runtime, CancellationTokenSource shutdown)
    {
        Directory.CreateDirectory(runtime.StateDirectory);

        WebApplication app = BuildApplication(runtime);
        IEngineLifecycleService lifecycle = app.Services.GetRequiredService<IEngineLifecycleService>();
        runtime.ApplyDashOptions(ResolveOptions(app.Services));

        runtime.WebPort = ResolveWebPort(app.Services, runtime);
        runtime.Version = string.IsNullOrWhiteSpace(runtime.Version) ? ResolveVersion() : runtime.Version;

        await using OwnedEngineLease lease = await AcquireLeaseAsync(lifecycle, runtime, shutdown).ConfigureAwait(false);
        if (lease.Lease is null)
            return lease.ExitCode;

        BindEndpoints(app, runtime);

        IJobSchedulerService jobs = app.Services.GetRequiredService<IJobSchedulerService>();
        IOtlpCollectorService collector = app.Services.GetRequiredService<IOtlpCollectorService>();

        try
        {
            await app.StartAsync(shutdown.Token).ConfigureAwait(false);
        }
        catch (IOException ex) when (ex.InnerException is SocketException)
        {
            await Console.Error.WriteLineAsync(
                $"kyberdash-api: could not listen on {DashRuntimeSettings.LoopbackUrl(runtime.WebPort)}: {ex.InnerException.Message}").ConfigureAwait(false);
            return 1;
        }

        jobs.Start();

        // Admission only queues: without this loop accepted telemetry stays in memory until the
        // process exits, so nothing a harness posts ever reaches the store readers query.
        if (collector is OtlpCollectorService otlpLifecycle)
            otlpLifecycle.Start();

        Uri webUri = ResolveBoundUrl(app, runtime.WebPort);
        string webUrl = webUri.GetLeftPart(UriPartial.Authority);
        lease.Lease.UpdateUrl(webUri);
        lifecycle.PublishServerInfo(
            runtime.StateDirectory,
            new ServerInfo(
                webUri.Port,
                Environment.ProcessId,
                DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
                webUri,
                ApiContractVersion));

        await AnnounceListeningAsync(runtime, webUrl).ConfigureAwait(false);

        if (runtime.OpenBrowser && !runtime.OtlpOnly)
            BrowserLauncher.TryOpen(JoinViewUrl(webUrl, runtime.View));

        try
        {
            await app.WaitForShutdownAsync(shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        if (runtime.OtlpEnabled)
        {
            try
            {
                await collector.FlushPendingAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException)
            {
                await Console.Error.WriteLineAsync($"kyberdash-api: final OTLP flush failed: {ex.Message}").ConfigureAwait(false);
            }
        }

        // Stops the flush loop and writes the last batch, so a restart sees everything accepted.
        if (collector is OtlpCollectorService otlpAtShutdown)
            await otlpAtShutdown.StopAsync().ConfigureAwait(false);

        await jobs.StopAsync().ConfigureAwait(false);
        lifecycle.ClearServerInfo(runtime.StateDirectory);
        await app.DisposeAsync().ConfigureAwait(false);
        return 0;
    }

    private static void BindEndpoints(WebApplication app, DashRuntimeSettings runtime)
    {
        app.Urls.Clear();
        if (runtime.OtlpOnly)
        {
            app.Urls.Add(DashRuntimeSettings.LoopbackUrl(runtime.OtlpPort).GetLeftPart(UriPartial.Authority));
            return;
        }

        app.Urls.Add(DashRuntimeSettings.LoopbackUrl(runtime.WebPort).GetLeftPart(UriPartial.Authority));
        if (runtime.OtlpEnabled && runtime.OtlpPort != runtime.WebPort)
        {
            app.Urls.Add(DashRuntimeSettings.LoopbackUrl(runtime.OtlpPort).GetLeftPart(UriPartial.Authority));
        }
    }

    private static int ResolveWebPort(IServiceProvider services, DashRuntimeSettings runtime)
    {
        if (runtime.WebPortExplicit)
            return runtime.WebPort;

        try
        {
            ISettingsService settings = services.GetRequiredService<ISettingsService>();
            int configured = settings.GetSettings().DashboardPort;
            return configured > 0 ? configured : DefaultWebPort;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or DashException)
        {
            return DefaultWebPort;
        }
    }

    private static DashOptions ResolveOptions(IServiceProvider services)
    {
        try
        {
            return services.GetRequiredService<DashOptions>();
        }
        catch (InvalidOperationException)
        {
            return new DashOptions();
        }
    }

    private static async Task<OwnedEngineLease> AcquireLeaseAsync(
        IEngineLifecycleService lifecycle,
        DashRuntimeSettings runtime,
        CancellationTokenSource shutdown)
    {
        string exePath = Environment.ProcessPath ?? "kyberdash-api";
        (IEngineOwnerLease? held, EngineDescriptor? existing) = await lifecycle
            .TryAcquireEngineAsync(runtime.StateDirectory, runtime.Version, exePath, shutdown.Cancel, shutdown.Token)
            .ConfigureAwait(false);

        if (held is not null)
            return new OwnedEngineLease(held);

        if (existing is not null)
        {
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(
                new
                {
                    @event = "kyberdash.web.attached",
                    pid = existing.Pid,
                    exe = existing.Exe,
                    version = existing.Version,
                    startedAt = existing.StartedAt,
                    url = existing.Url,
                },
                ListeningJsonOptions)).ConfigureAwait(false);
            await Console.Out.WriteLineAsync(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"\n  kyberdash-api: engine already running (pid {existing.Pid}, version {existing.Version}){(existing.Url is null ? string.Empty : $", at {existing.Url}")}.")).ConfigureAwait(false);
            return new OwnedEngineLease(null, exitCode: 0);
        }

        await Console.Error.WriteLineAsync(
            $"kyberdash-api: could not acquire single-instance engine lock under {runtime.StateDirectory}; another engine holds it but did not answer.").ConfigureAwait(false);
        return new OwnedEngineLease(null, exitCode: 1);
    }

    private static Uri ResolveBoundUrl(WebApplication app, int configuredPort)
    {
        Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature? addresses = app.Services
            .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features
            .Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>();

        if (addresses is not null)
        {
            foreach (string address in addresses.Addresses)
            {
                if (Uri.TryCreate(address, UriKind.Absolute, out Uri? uri) && uri.Port > 0)
                    return DashRuntimeSettings.LoopbackUrl(uri.Port);
            }
        }

        return DashRuntimeSettings.LoopbackUrl(configuredPort);
    }

    private static async Task AnnounceListeningAsync(DashRuntimeSettings runtime, string webUrl)
    {
        string line = JsonSerializer.Serialize(
            new
            {
                @event = ListeningEvent,
                url = webUrl,
                pid = Environment.ProcessId,
                version = runtime.Version,
                apiVersion = ApiContractVersion,
            },
            ListeningJsonOptions);

        await Console.Out.WriteLineAsync(line).ConfigureAwait(false);
        await Console.Out.FlushAsync().ConfigureAwait(false);
    }

    private static string JoinViewUrl(string baseUrl, string? view)
    {
        if (string.IsNullOrWhiteSpace(view) || string.Equals(view.Trim(), "/", StringComparison.Ordinal))
            return baseUrl;

        string clean = view.Trim();
        return clean.StartsWith('/') ? baseUrl + clean : $"{baseUrl}/{clean}";
    }

    /// <summary>Holds an engine lease (when one was acquired) and reports the exit code to use otherwise.</summary>
    private class OwnedEngineLease(IEngineOwnerLease? lease, int exitCode = 0) : IAsyncDisposable
    {
        public IEngineOwnerLease? Lease { get; } = lease;

        public int ExitCode { get; } = exitCode;

        public async ValueTask DisposeAsync()
        {
            if (Lease is not null)
                await Lease.DisposeAsync().ConfigureAwait(false);
        }
    }

}

/// <summary>Opens the operator's browser once, ignoring a desktop session that cannot accept one.</summary>
internal static class BrowserLauncher
{
    /// <summary>Best-effort open of <paramref name="url"/>; never throws.</summary>
    public static void TryOpen(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;

        try
        {
            if (OperatingSystem.IsMacOS())
                Spawn("open", [url]);
            else if (OperatingSystem.IsLinux())
                Spawn("xdg-open", [url]);
            else if (OperatingSystem.IsWindows())
                Spawn("cmd", ["/c", "start", string.Empty, url]);
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static void Spawn(string fileName, string[] arguments)
    {
        System.Diagnostics.ProcessStartInfo startInfo = new(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        // The command hands the URL to the desktop and exits; the dashboard does not wait for it.
        using System.Diagnostics.Process? process = System.Diagnostics.Process.Start(startInfo);
    }
}
