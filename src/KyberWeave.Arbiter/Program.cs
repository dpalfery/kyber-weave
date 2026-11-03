using KyberWeave.Arbiter;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Arbiter.Mcp;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Credentials;
using KyberWeave.Core.Configuration;
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
    // Last resort: the hook path already fails closed with a harness block, so reaching
    // here means the streams themselves broke. Nothing may go to stdout. Exit 2, not 1:
    // on Claude a crash without exit code 2 is non-blocking, and on Copilot exit 1 is a
    // non-blocking hook error, so exit 1 would let a gated dispatch proceed.
    await Console.Error.WriteLineAsync($"KW-ARB-HOOK-001: hook host failed: {ex.Message}")
        .ConfigureAwait(false);
    exitCode = 2;
}

return exitCode;

// The D4 fallback: the same decisions the hook makes, offered as MCP tools for a caller
// that no harness hook will gate. Stdio JSON-RPC owns stdout, so nothing here writes there;
// logging is pinned to stderr, as the docs server pins it.
static async Task<int> ServeAsync(string[] args)
{
    string repoRoot;
    try
    {
        repoRoot = ArbiterRootResolver.Resolve(
            args,
            Directory.GetCurrentDirectory(),
            Environment.GetEnvironmentVariable(ArbiterRootResolver.EnvironmentVariable));
    }
    catch (Exception ex)
    {
        // Every failure here is a configuration mistake the message already names:
        // Path.GetFullPath rejects more than the two types the resolver documents, and
        // the exit is 1 either way. Stdout carries protocol frames only, so the message
        // goes to stderr and never a stack trace.
        await Console.Error.WriteLineAsync($"kyber-weave-arbiter serve: {ex.Message}").ConfigureAwait(false);
        return 1;
    }

    try
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Services.AddSingleton(new ArbiterServeContext(
            repoRoot,
            new ArbiterHookDecisionEngine(),
            Console.Error,
            Composition.LoadHostConfig,
            ServeKeyResolved,
            ServeEffectiveProvider));

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

        await builder.Build().RunAsync().ConfigureAwait(false);
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

// Presence only: the key is read from the store or the environment and discarded here.
// Status reports the answer, never the value.
static ArbiterProviderConfig ServeEffectiveProvider(KyberWeaveConfig config) =>
    ArbiterUserSettings.ApplyTo(
        config.Arbiter.Provider,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

static bool ServeKeyResolved(KyberWeaveConfig config)
{
    try
    {
        ArbiterProviderConfig repository = config.Arbiter.Provider;
        ArbiterProviderConfig effective = ServeEffectiveProvider(config);
        string? userOverride = string.Equals(effective.Endpoint, repository.Endpoint, StringComparison.Ordinal)
            ? null
            : effective.Endpoint;
        return ArbiterKeyResolver.Resolve(effective.Endpoint, CreateCredentialStore(), userOverride) is not null;
    }
    catch (Exception)
    {
        return false;
    }
}

static ICredentialStore CreateCredentialStore() =>
    OperatingSystem.IsWindows()
        ? new WindowsCredentialStore()
        : OperatingSystem.IsMacOS()
            ? new MacKeychainCredentialStore(new ProcessRunnerCredentialProcessRunner())
            : new SecretServiceCredentialStore(new ProcessRunnerCredentialProcessRunner());
