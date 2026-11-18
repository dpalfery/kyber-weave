using KyberWeave.Arbiter;
using KyberWeave.Arbiter.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

if (args.Length > 0 && string.Equals(args[0], "serve", StringComparison.Ordinal))
{
    return await ServeAsync(args).ConfigureAwait(false);
}

// The hook host is a separate executable rather than a `kyber-weave hook` subcommand.
// Stdout carries the harness's decision document, and the CLI is built on
// Spectre.Console, which writes there. A separate entry point makes stream corruption
// structurally impossible instead of a matter of discipline.
int exitCode;
try
{
    exitCode = await Composition.DispatchAsync(args, Console.In, Console.Out, Console.Error)
        .ConfigureAwait(false);
}
catch (Exception ex)
{
    // Last resort: the streams themselves broke. Nothing may go to stdout. Exit 0: the
    // Arbiter never blocks a dispatch on its own failure (ADR 0072), and exit 2 is a block
    // on Claude. The failure is logged with its code.
    await Console.Error.WriteLineAsync($"KW-ARB-HOOK-001: hook host failed: {ex.Message}. The dispatch continues.")
        .ConfigureAwait(false);
    exitCode = 0;
}

return exitCode;

// The D4 fallback: the same decisions the hook makes, offered as MCP tools for a caller
// that no harness hook will gate. Stdio JSON-RPC owns stdout, so nothing here writes there;
// logging is pinned to stderr, as the docs server pins it.
static async Task<int> ServeAsync(string[] args)
{
    try
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

        // Every failure resolving the context is a configuration mistake the message already names: the root flag
        // carries no path, or the root is not a directory. Stdout carries protocol frames only, so the message goes
        // to stderr and never a stack trace.
        _ = builder.Services.AddArbiterServe(args, Console.Error);

        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithTools<ArbiterTools>();

        // The stdio extension supplies the hosting, and its ITransport is swapped for one
        // that still answers requests read before stdin closed, which the SDK transport
        // silently drops (see DrainingStreamServerTransport). The SDK's factory is replaced
        // before anything resolves it, so its transport is never constructed and never
        // starts reading stdin.
        builder.Services.Replace(ServiceDescriptor.Singleton<ITransport>(services =>
            DrainingStreamServerTransport.ForStandardStreams(
                services.GetRequiredService<IOptions<McpServerOptions>>().Value.ServerInfo?.Name ?? "kyber-weave-arbiter",
                services.GetService<ILoggerFactory>())));

        using IHost host = builder.Build();
        _ = host.Services.GetRequiredService<ArbiterServeContext>();
        await host.RunAsync().ConfigureAwait(false);
        return 0;
    }
    catch (Exception ex)
    {
        // A host that never came up is the client's mistake to fix, not a crash to read:
        // stdout owns protocol frames, so the message goes to stderr, one line, and the
        // process exits 1. An unhandled exception here would print a stack trace that
        // names the client's own arguments back at them.
        await Console.Error.WriteLineAsync($"kyber-weave-arbiter serve: {ex.Message}").ConfigureAwait(false);
        return 1;
    }
}
