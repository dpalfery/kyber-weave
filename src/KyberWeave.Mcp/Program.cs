using KyberWeave.Core.CodeGraph;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Core.Docs.Search;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Kyber-Weave's MCP surface is a separate executable rather than a `kyber-weave mcp`
// subcommand. Stdio JSON-RPC owns stdout, and the CLI is built on Spectre.Console, which
// writes there. A separate entry point makes stream corruption structurally impossible
// instead of a matter of discipline.

var builder = Host.CreateApplicationBuilder(args);

// Every log line goes to stderr. One stray line on stdout breaks the transport.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

var repoRoot = ResolveRepoRoot(args);

// Composition root: factories for DocumentIndexHost. Core never invents these collaborators.
builder.Services.AddSingleton(new DocumentIndexHost(
    repoRoot,
    () => CodeGraphResolverAdapter.ForRepository(repoRoot),
    () => new DocumentLoader(repoRoot).Load()));

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync().ConfigureAwait(false);

/// <summary>
/// The repository root, from <c>--repo-root</c>, else <c>KYBER_WEAVE_REPO_ROOT</c>, else
/// the nearest ancestor of the working directory that contains a <c>.git</c> entry.
/// Hosts launch the server with an unpredictable working directory, so guessing wrong
/// here means an empty corpus rather than an error.
/// </summary>
static string ResolveRepoRoot(string[] args)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == "--repo-root") return Path.GetFullPath(args[i + 1]);
    }

    var fromEnvironment = Environment.GetEnvironmentVariable("KYBER_WEAVE_REPO_ROOT");
    if (!string.IsNullOrWhiteSpace(fromEnvironment)) return Path.GetFullPath(fromEnvironment);

    var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (directory is not null)
    {
        if (Directory.Exists(Path.Combine(directory.FullName, ".git")) ||
            File.Exists(Path.Combine(directory.FullName, ".git")))
        {
            return directory.FullName;
        }
        directory = directory.Parent;
    }

    return Directory.GetCurrentDirectory();
}
