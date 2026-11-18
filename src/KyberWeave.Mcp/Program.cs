using System.Reflection;

using KyberWeave.Application;
using KyberWeave.Data.Configuration;
using KyberWeave.Data.Hooks;
using KyberWeave.Data.Host;
using KyberWeave.Data.Paths;
using KyberWeave.Data.Review;
using KyberWeave.Mcp;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

if (args.Contains("--version", StringComparer.OrdinalIgnoreCase) || args.Contains("-v", StringComparer.OrdinalIgnoreCase))
{
    Console.WriteLine($"kyber-weave-mcp {GetVersion()}");
    return 0;
}

// The server refreshes the CodeGraph index the docs tools answer from on its first tool call.
// --no-codegraph-refresh turns that off, and exists for one caller: `kyber-weave doctor --check`
// promises it never runs `init` or `index`, and its live probe makes a real docs call. With the
// flag in the probe's argv, the promise holds through the server rather than by luck.
const string NoCodeGraphRefreshFlag = "--no-codegraph-refresh";
bool codeGraphRefreshEnabled = !args.Contains(NoCodeGraphRefreshFlag, StringComparer.Ordinal);

// Kyber-Weave's MCP surface is a separate executable rather than a `kyber-weave mcp`
// subcommand. Stdio JSON-RPC owns stdout, and the CLI is built on Spectre.Console, which
// writes there. A separate entry point makes stream corruption structurally impossible
// instead of a matter of discipline.

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

// Every log line goes to stderr. One stray line on stdout breaks the transport.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

string workingDirectory = Directory.GetCurrentDirectory();
string? expectRootEnvironment = Environment.GetEnvironmentVariable(
    RepositoryRootResolver.ExpectRootEnvironmentVariable);
bool expectRootFromFlag = RepositoryRootResolver.HasExpectRootArgument(args);

RepositoryRootResolver rootResolver = RealRootResolver();
string repoRoot;
try
{
    repoRoot = rootResolver.Resolve(
        args,
        workingDirectory,
        Environment.GetEnvironmentVariable(RepositoryRootResolver.EnvironmentVariable));
}
catch (ExpectedRootMismatchException ex)
{
    // A client-asserted root that does not match is a refuse-to-serve, distinct from an
    // unbound root: KW-MCP-ROOT-002 says the binding was intentional but wrong.
    await Console.Error.WriteLineAsync($"KW-MCP-ROOT-002: {ex.Message}").ConfigureAwait(false);
    return 1;
}
catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
{
    // stdout is the JSON-RPC transport; startup diagnostics belong on stderr.
    await Console.Error.WriteLineAsync($"KW-MCP-ROOT-001: {ex.Message}").ConfigureAwait(false);
    return 1;
}

// The environment assertion is honoured only when the flag is absent: an explicit CLI
// expectation wins over the ambient one, so a shell export cannot override a project pin.
if (!expectRootFromFlag && !string.IsNullOrWhiteSpace(expectRootEnvironment))
{
    string expectedRoot;
    try
    {
        expectedRoot = Path.GetFullPath(expectRootEnvironment, workingDirectory);
    }
    catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
    {
        await Console.Error.WriteLineAsync(
            "KW-MCP-ROOT-002: The KYBER_WEAVE_EXPECT_ROOT value is not a valid path. " +
            "Refusing to serve.")
            .ConfigureAwait(false);
        return 1;
    }

    if (!rootResolver.PathsEqual(expectedRoot, repoRoot))
    {
        await Console.Error.WriteLineAsync(
            $"KW-MCP-ROOT-002: The KYBER_WEAVE_EXPECT_ROOT value '{expectedRoot}' does not match " +
            $"the resolved MCP root '{repoRoot}'. Refusing to serve because the client expects " +
            "a different repository and answers would come from the wrong corpus.")
            .ConfigureAwait(false);
        return 1;
    }
}

// Composition root. The shared layers register their own services first; AddDocsHostServices adds every
// registration below it that is a host decision made once from this process's own argv and root. Core never
// invents these collaborators. The composition-root test builds the same container, so a tool whose constructor
// asks for a service this root does not register fails there rather than on its first call.
builder.Services.AddWeaveApplication();
builder.Services.AddDocsHostServices(repoRoot, codeGraphRefreshEnabled);

builder.Services
    .AddMcpServer(DocsServerInstructions.Configure)
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

// The SDK registers its single-session hosting only through its transport extensions, and
// WithStreamServerTransport would construct a transport of its own on the spot. So the stdio
// extension supplies the hosting, and its ITransport is swapped for one that still answers
// requests read before stdin closed, which the SDK transport silently drops. See
// DrainingStreamServerTransport for why. The SDK's factory is replaced before anything resolves
// it, so its transport is never constructed and never starts reading stdin.
builder.Services.Replace(ServiceDescriptor.Singleton<ITransport>(services =>
    DrainingStreamServerTransport.ForStandardStreams(
        services.GetRequiredService<IOptions<McpServerOptions>>().Value.ServerInfo?.Name ?? "kyber-weave-mcp",
        services.GetService<ILoggerFactory>())));

await builder.Build().RunAsync().ConfigureAwait(false);
return 0;

static RepositoryRootResolver RealRootResolver()
{
    PhysicalFileSystem files = new();
    PhysicalPathResolver paths = new(files);
    ProcessEnvironment environment = new();
    return new RepositoryRootResolver(
        new FileKyberWeaveConfigRepository(new FileRepositoryTreeProbe(files), files, paths, environment),
        paths,
        environment);
}

static string GetVersion()
{
    Assembly assembly = typeof(Program).Assembly;
    string? infoVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
    if (!string.IsNullOrWhiteSpace(infoVersion))
    {
        int plusIndex = infoVersion.IndexOf('+', StringComparison.Ordinal);
        return plusIndex >= 0 ? infoVersion[..plusIndex] : infoVersion;
    }

    Version? nameVersion = assembly.GetName().Version;
    if (nameVersion is not null)
    {
        return nameVersion.ToString();
    }

    return "0.0.0";
}
