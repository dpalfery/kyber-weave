using System.Globalization;
using System.Runtime.InteropServices;
using KyberDash.Application;
using KyberDash.Contracts.Services;
using KyberDash.Core;
using KyberDash.Core.Exceptions;
using KyberDash.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace KyberDash.Mcp;

/// <summary>
/// Entry point for <c>kyberdash-mcp</c>: resolves a service container, then serves the analytics
/// MCP tools over stdio.
/// </summary>
/// <remarks>
/// This host owns the command line and the stdio transport only. The eight tools, their argument
/// reshaping, and the contract-v1 envelope live in <see cref="McpStdioHost"/>, which is shared with
/// the tests so they exercise the same code an agent reaches.
/// </remarks>
public static partial class Program
{

    /// <summary>Runs the MCP stdio server.</summary>
    /// <param name="args">The process command line.</param>
    /// <returns>Process exit code: <c>0</c> on a clean end of input, <c>2</c> on bad arguments.</returns>
    public static async Task<int> Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Contains("--version", StringComparer.Ordinal))
        {
            string version;
            try
            {
                version = ResolveVersion();
            }
            catch (InvalidOperationException exception)
            {
                await Console.Error.WriteLineAsync($"--version: {exception.Message}").ConfigureAwait(false);
                return 1;
            }

            try
            {
                await Console.Out.WriteLineAsync(version).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // A closed pipe (for example `| head`) is the reader's choice, not a failure.
            }

            return 0;
        }

        if (args.Contains("--help", StringComparer.Ordinal) || args.Contains("-h", StringComparer.Ordinal))
        {
            await Console.Out.WriteLineAsync(
                "Usage: kyberdash-mcp [--server-url <url>] [--state-dir <dir>] [--db <path>] [--timezone <tz>] [--verbose]\n\n"
                + "Serves read-only KyberDash analytics over MCP stdio. With no running dashboard the tools\n"
                + "query the canonical store directly.").ConfigureAwait(false);
            return 0;
        }

        McpOptions options;
        try
        {
            options = McpOptions.Parse(args);
        }
        catch (DashValidationException ex)
        {
            await Console.Error.WriteLineAsync($"kyberdash-mcp: {ex.Message}").ConfigureAwait(false);
            return 2;
        }

        using CancellationTokenSource shutdown = new();
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;
            shutdown.Cancel();
        };
        Console.CancelKeyPress += onCancel;
        using PosixSignalRegistration sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            shutdown.Cancel();
        });
        try
        {
            await using ServiceProvider provider = BuildProvider(options);
            using HttpClient httpClient = CreateHttpClient();
            McpStdioHost host = new(
                provider.GetRequiredService<IAnalyticsService>(),
                provider.GetRequiredService<IEngineLifecycleService>(),
                httpClient,
                options.ServerUrl,
                options.StateDirectory,
                options.Verbose);

            return await host.RunAsync(Console.In, Console.Out, shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    /// <summary>Builds the container this host resolves its services from.</summary>
    /// <param name="options">The parsed command line.</param>
    /// <returns>The service provider; the caller disposes it.</returns>
    internal static ServiceProvider BuildProvider(McpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        ServiceCollection services = new();
        services.AddDashApplication(options.ToDashOptions());
        return services.BuildServiceProvider();
    }

    /// <summary>Creates the loopback HTTP client used to forward tool calls to a live dashboard.</summary>
    /// <remarks>Redirects and proxies are off: the target is always literal loopback.</remarks>
    internal static HttpClient CreateHttpClient()
    {
        SocketsHttpHandler? handler = new()
        {
            AllowAutoRedirect = false,
            UseProxy = false,
        };
        try
        {
            HttpClient client = new(handler, disposeHandler: true)
            {
                Timeout = McpStdioHost.RequestTimeout,
            };
            handler = null;
            return client;
        }
        finally
        {
            handler?.Dispose();
        }
    }

    /// <summary>Resolves the reported version from the assembly's informational version.</summary>
    public static string ResolveVersion()
    {
        return DashVersion.Current;
    }
}

/// <summary>The parsed <c>kyberdash-mcp</c> command line.</summary>
public class McpOptions
{
    /// <summary>A literal loopback dashboard URI to forward tool calls to.</summary>
    public Uri? ServerUrl { get; set; }

    /// <summary>State directory used to discover a live dashboard.</summary>
    public string? StateDirectory { get; set; }

    /// <summary>Canonical store path used when queries run directly.</summary>
    public string? DatabasePath { get; set; }

    /// <summary>Optional IANA time zone for time-filtered queries.</summary>
    public string? TimeZone { get; set; }

    /// <summary>Whether to write diagnostics to stderr.</summary>
    public bool Verbose { get; set; }

    /// <summary>Parses the command line, throwing <see cref="DashValidationException"/> on a bad option.</summary>
    /// <param name="args">The process command line.</param>
    /// <returns>The parsed options.</returns>
    public static McpOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        McpOptions options = new();
        int start = args.Count > 0 && string.Equals(args[0], "mcp", StringComparison.Ordinal) ? 1 : 0;
        for (int index = start; index < args.Count; index++)
        {
            string arg = args[index];
            switch (arg)
            {
                case "--verbose":
                    options.Verbose = true;
                    break;

                case "--server-url":
                    string rawUrl = ReadValue(args, ref index, arg);
                    if (!McpStdioHost.TryValidateLoopbackServerUrl(rawUrl, out Uri? normalized) || normalized is null)
                    {
                        throw new DashValidationException(
                            "invalid_request",
                            "--server-url must be a literal loopback http://127.0.0.1:<port> or http://[::1]:<port> URL.");
                    }

                    options.ServerUrl = normalized;
                    break;

                case "--state-dir":
                    options.StateDirectory = Path.GetFullPath(ReadValue(args, ref index, arg));
                    break;

                case "--db":
                    options.DatabasePath = Path.GetFullPath(ReadValue(args, ref index, arg));
                    break;

                case "--timezone":
                    options.TimeZone = ReadValue(args, ref index, arg);
                    break;

                default:
                    throw new DashValidationException(
                        "invalid_option",
                        string.Create(CultureInfo.InvariantCulture, $"unknown or incomplete option '{arg}'. Run `kyberdash-mcp --help` for usage."));
            }
        }

        return options;
    }

    /// <summary>Builds the application-layer composition options.</summary>
    public DashOptions ToDashOptions() => new()
    {
        DatabasePath = DatabasePath,
        StateDirectory = StateDirectory,
    };

    private static string ReadValue(IReadOnlyList<string> args, ref int index, string option)
    {
        if (index + 1 >= args.Count)
        {
            throw new DashValidationException(
                "invalid_option",
                string.Create(CultureInfo.InvariantCulture, $"{option} needs a value. Run `kyberdash-mcp --help` for usage."));
        }

        index++;
        return args[index];
    }
}
