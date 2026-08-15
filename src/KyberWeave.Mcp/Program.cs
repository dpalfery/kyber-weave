using System.Reflection;
using KyberWeave.Core.CodeGraph;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Core.Docs.Search;
using KyberWeave.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

if (args.Contains("--version", StringComparer.OrdinalIgnoreCase) || args.Contains("-v", StringComparer.OrdinalIgnoreCase))
{
    Console.WriteLine($"kyber-weave-mcp {GetVersion()}");
    return 0;
}

// Kyber-Weave's MCP surface is a separate executable rather than a `kyber-weave mcp`
// subcommand. Stdio JSON-RPC owns stdout, and the CLI is built on Spectre.Console, which
// writes there. A separate entry point makes stream corruption structurally impossible
// instead of a matter of discipline.

var builder = Host.CreateApplicationBuilder(args);

// Every log line goes to stderr. One stray line on stdout breaks the transport.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

var repoRoot = ResolveRepoRoot(args);
var ontology = ResolveOntology(repoRoot);

// Composition root: factories for DocumentIndexHost. Core never invents these collaborators.
builder.Services.AddSingleton(new DocumentIndexHost(
    repoRoot,
    () => CodeGraphResolverAdapter.ForRepository(repoRoot),
    () => new DocumentLoader(repoRoot, ontology).Load(),
    ontology.DocsRoots,
    ontology.ResolvedCatalogPath));
builder.Services.AddSingleton<IDocsAnalysisReader>(new RepositoryDocsAnalysisReader(repoRoot));

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync().ConfigureAwait(false);
return 0;

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

/// <summary>
/// The host's ontology from <c>.kyber-weave/kyber-weave.yml</c>, or product defaults when
/// the repository has no config.
/// </summary>
/// <remarks>
/// <para>
/// The server reads the same configuration the CLI does. Without this it served the product
/// default root, so every repository that had moved its documentation — the common case,
/// since the default is inherited from one origin repository — got an empty corpus from a
/// server that reported no error, and multi-root configuration would not have reached
/// retrieval at all.
/// </para>
/// <para>
/// A config that cannot be read is reported and then stepped over rather than being fatal.
/// The corpus will be wrong, but a client that has already spawned this process gets a
/// server that answers and says why, instead of a transport that dies at startup.
/// </para>
/// </remarks>
static OntologyConfig ResolveOntology(string repoRoot)
{
    var loaded = KyberWeaveConfigLoader.TryLoad(repoRoot);
    if (loaded.Success && loaded.Config is not null)
        return loaded.Config.Ontology;

    // stderr, never stdout: stdout is the JSON-RPC transport.
    Console.Error.WriteLine(
        $"{KyberWeaveConfigLoader.ConfigLoadErrorCode}: Failed to load " +
        $"'{loaded.ConfigPath ?? "kyber-weave.yml"}': {loaded.Error ?? "unknown error"}. " +
        "Serving the default ontology; the corpus may be empty.");

    return OntologyConfig.ProductDefaults;
}

static string GetVersion()
{
    var assembly = typeof(Program).Assembly;
    var infoVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
    if (!string.IsNullOrWhiteSpace(infoVersion))
    {
        var plusIndex = infoVersion.IndexOf('+', StringComparison.Ordinal);
        return plusIndex >= 0 ? infoVersion[..plusIndex] : infoVersion;
    }

    var nameVersion = assembly.GetName().Version;
    if (nameVersion is not null)
    {
        return nameVersion.ToString();
    }

    return "0.0.0";
}
