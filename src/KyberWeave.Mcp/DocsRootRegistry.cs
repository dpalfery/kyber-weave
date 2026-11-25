using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Contracts.Host;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Search;
using Microsoft.Extensions.DependencyInjection;

namespace KyberWeave.Mcp;

/// <summary>
/// Resolves the per-call <c>projectPath</c> to a set of tools bound to that checkout, and keeps the most recently used
/// few so a worker's repeated calls reuse one corpus instead of rebuilding it.
/// </summary>
/// <remarks>
/// <para>
/// In bound mode the server binds one root at startup, but agent runs work in linked worktrees created after it
/// started, so the corpus a call answers from has to be nameable per call. Only a worktree of the <b>same
/// repository</b> as the server root is accepted: git is asked for the candidate's top level and common git directory,
/// and the common directory must equal the server root's. Anything else is refused with a message, never silently
/// answered from the server root.
/// </para>
/// <para>
/// In any-repo mode (<see cref="ForAnyRepo(bool, IServiceProvider?)"/>) the server holds no root, and any initialized
/// repository can be named: the path must be an existing directory, git must name its top level, and that top level
/// must hold a Kyber-Weave host configuration. Each failed step is a refusal that names it; the registry never walks up
/// to a parent and never falls back to another root.
/// </para>
/// <para>
/// Per-root state is the document host, the analysis reader and the CodeGraph refresh schedule, all of which live
/// inside the <see cref="DocsTools"/> the factory builds. The registry bounds how many of those it holds
/// (<see cref="Capacity"/>, least recently used evicted); a bound server's own tools are not among them and are never
/// evicted. Both modes remember which top level a path resolved to, so a repeated path starts no git process.
/// </para>
/// <para>
/// The factory runs outside the lock, so a slow first build of one root never stalls a call for another. Two calls
/// that race to build the same root may both build; the second to finish finds the first's entry and uses it, so every
/// caller ends on one instance and the duplicate is never used. Nothing is disposed, neither a dropped duplicate nor an
/// evicted entry: nothing a <see cref="DocsTools"/> holds is disposable, and a call already holding an evicted entry
/// finishes on it. If that ever changes, the registry must still not dispose an entry a call may hold.
/// </para>
/// </remarks>
public sealed class DocsRootRegistry
{
    /// <summary>
    /// How many roots are kept at once: the roots besides the server's own in bound mode, every root in any-repo mode.
    /// </summary>
    public const int Capacity = 4;

    /// <summary>How many path-to-top-level answers are remembered before the memory is cleared.</summary>
    private const int PathCacheCapacity = 64;

    private readonly string? _serverRoot;
    private readonly IKyberWeaveConfigRepository? _configs;
    private readonly Func<string, DocsTools> _factory;
    private readonly IGitWorktreeIdentity _git;
    private readonly IFileSystem _files;
    private readonly IPathResolver _paths;
    private readonly IProcessEnvironment _environment;
    private readonly Lock _gate = new();
    private readonly LinkedList<(string Root, DocsTools Tools)> _recent = new();
    private readonly Dictionary<string, (string TopLevel, string CommonDirectory)> _pathRoots;
    private (string? TopLevel, string? CommonDirectory, string? Error)? _serverIdentity;

    /// <summary>A registry for the repository bound at <paramref name="serverRoot"/>.</summary>
    /// <param name="serverRoot">The root the server was started with.</param>
    /// <param name="factory">Builds the tools for another worktree root of the same repository.</param>
    /// <param name="git">Names the working tree a directory belongs to.</param>
    /// <param name="files">Checks that a named project path is a directory.</param>
    /// <param name="paths">Turns the server root and a named project path into full paths.</param>
    /// <param name="environment">Names the directory a relative project path is resolved against, and the platform whose path-name rule compares worktree roots.</param>
    public DocsRootRegistry(
        string serverRoot,
        Func<string, DocsTools> factory,
        IGitWorktreeIdentity git,
        IFileSystem files,
        IPathResolver paths,
        IProcessEnvironment environment)
        : this(factory, git, files, paths, environment, configs: null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverRoot);
        _serverRoot = paths.GetFullPath(serverRoot, environment.CurrentDirectory);
    }

    private DocsRootRegistry(
        Func<string, DocsTools> factory,
        IGitWorktreeIdentity git,
        IFileSystem files,
        IPathResolver paths,
        IProcessEnvironment environment,
        IKyberWeaveConfigRepository? configs)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(git);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(environment);
        _git = git;
        _files = files;
        _paths = paths;
        _environment = environment;
        _factory = factory;
        _configs = configs;
        _pathRoots = new Dictionary<string, (string TopLevel, string CommonDirectory)>(StringComparer.FromComparison(PathComparison));
    }

    /// <summary>True when this registry serves any initialized repository and the server holds no root of its own.</summary>
    public bool IsAnyRepo => _configs is not null;

    /// <summary>A registry for a server with no root, whose every root gets the composition a bound server root has.</summary>
    /// <param name="codeGraphRefreshEnabled">False when the process runs with <c>--no-codegraph-refresh</c>.</param>
    /// <param name="services">The container the roots' services are resolved from, or null for the default one.</param>
    public static DocsRootRegistry ForAnyRepo(bool codeGraphRefreshEnabled, IServiceProvider? services = null)
    {
        IServiceProvider provider = DocsToolsComposition.ServicesOrDefault(services);
        return ForAnyRepo(
            root => CreateTools(root, codeGraphRefreshEnabled, null, services),
            provider.GetRequiredService<IGitWorktreeIdentity>(),
            provider.GetRequiredService<IFileSystem>(),
            provider.GetRequiredService<IPathResolver>(),
            provider.GetRequiredService<IProcessEnvironment>(),
            provider.GetRequiredService<IKyberWeaveConfigRepository>());
    }

    /// <summary>A registry for a server with no root, over the given seams.</summary>
    /// <param name="factory">Builds the tools for one repository's top level.</param>
    /// <param name="git">Names the working tree a directory belongs to.</param>
    /// <param name="files">Checks that a named project path is a directory.</param>
    /// <param name="paths">Turns a named project path into a full path.</param>
    /// <param name="environment">Names the directory a relative project path is resolved against, and the platform whose path-name rule compares roots.</param>
    /// <param name="configs">Says whether a top level holds a Kyber-Weave host configuration.</param>
    internal static DocsRootRegistry ForAnyRepo(
        Func<string, DocsTools> factory,
        IGitWorktreeIdentity git,
        IFileSystem files,
        IPathResolver paths,
        IProcessEnvironment environment,
        IKyberWeaveConfigRepository configs)
    {
        ArgumentNullException.ThrowIfNull(configs);
        return new DocsRootRegistry(factory, git, files, paths, environment, configs);
    }

    /// <summary>A registry whose other roots get the same composition the server root has.</summary>
    /// <param name="serverRoot">The root the server was started with.</param>
    /// <param name="codeGraphRefreshEnabled">False when the process runs with <c>--no-codegraph-refresh</c>.</param>
    /// <param name="services">The container the other roots' services are resolved from, or null for the default one.</param>
    public static DocsRootRegistry ForRepository(
        string serverRoot,
        bool codeGraphRefreshEnabled,
        IServiceProvider? services = null) =>
        new(
            serverRoot,
            root => CreateTools(root, codeGraphRefreshEnabled, null, services),
            DocsToolsComposition.ServicesOrDefault(services).GetRequiredService<IGitWorktreeIdentity>(),
            DocsToolsComposition.ServicesOrDefault(services).GetRequiredService<IFileSystem>(),
            DocsToolsComposition.ServicesOrDefault(services).GetRequiredService<IPathResolver>(),
            DocsToolsComposition.ServicesOrDefault(services).GetRequiredService<IProcessEnvironment>());

    /// <summary>The tools for one checkout: its own document host, analysis reader and refresh schedule.</summary>
    /// <param name="repoRoot">The checkout root.</param>
    /// <param name="codeGraphRefreshEnabled">False when the process runs with <c>--no-codegraph-refresh</c>.</param>
    /// <param name="registry">The registry the tools hand a per-call <c>projectPath</c> to, or null.</param>
    /// <param name="services">The container the shared collaborators are resolved from, or null for the default one.</param>
    /// <param name="deliveryExecutor">Runs git and gh for the deliver tool, or null for the bounded default.</param>
    /// <param name="deliveryHome">The home directory holding the run records, or null for the user profile.</param>
    /// <param name="toolingExecutor">Runs git and CodeGraph for the tooling-check tool, or null for the bounded default.</param>
    /// <param name="toolingMcpExecutable">The docs server executable the tooling check proves, or null for the one on <c>PATH</c>.</param>
    public static DocsTools CreateTools(
        string repoRoot,
        bool codeGraphRefreshEnabled,
        DocsRootRegistry? registry,
        IServiceProvider? services = null,
        IProcessExecutor? deliveryExecutor = null,
        string? deliveryHome = null,
        IProcessExecutor? toolingExecutor = null,
        string? toolingMcpExecutable = null)
    {
        IServiceProvider provider = DocsToolsComposition.ServicesOrDefault(services);
        OntologyConfig ontology = ResolveOntology(repoRoot, provider);
        ICodeGraphPortsFactory codeGraph = provider.GetRequiredService<ICodeGraphPortsFactory>();
        IDocumentSourceFactory documents = provider.GetRequiredService<IDocumentSourceFactory>();
        DocumentIndexHost host = new(
            repoRoot,
            () => codeGraph.ForRepository(repoRoot).Resolver,
            () => documents.Create(repoRoot, ontology).Load(),
            provider.GetRequiredService<IRepositoryFileStore>(),
            provider.GetRequiredService<IPathResolver>(),
            provider.GetRequiredService<IProcessEnvironment>(),
            ontology.DocsRoots,
            ontology.ResolvedCatalogPath);
        return DocsToolsComposition.Create(
            host,
            DocsToolsComposition.AnalysisReader(provider, repoRoot),
            DocsToolsComposition.Freshness(provider, repoRoot, codeGraphRefreshEnabled),
            registry,
            services,
            deliveryExecutor,
            deliveryHome,
            toolingExecutor,
            toolingMcpExecutable);
    }

    /// <summary>
    /// The host's ontology from <c>.kyber-weave/kyber-weave.yml</c>. A config that cannot be read is
    /// reported on stderr and stepped over rather than being fatal: a client that has already spawned
    /// this process gets a server that answers and says why, instead of a transport that dies.
    /// </summary>
    /// <param name="repoRoot">The checkout whose configuration names the corpus.</param>
    /// <param name="services">The container the configuration repository is resolved from.</param>
    public static OntologyConfig ResolveOntology(string repoRoot, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        KyberWeaveConfigLoadResult loaded =
            DocsToolsComposition.ServicesOrDefault(services).GetRequiredService<IKyberWeaveConfigRepository>().TryLoad(repoRoot);
        if (loaded is { Success: true, Config: not null })
        {
            return loaded.Config.Ontology;
        }

        // stderr, never stdout: stdout is the JSON-RPC transport.
        DocsToolsComposition.ServicesOrDefault(services).GetRequiredService<IConsole>().Error.WriteLine(
            $"{KyberWeaveConfigDiagnosticCodes.LoadError}: Failed to load " +
            $"'{loaded.ConfigPath ?? "kyber-weave.yml"}': {loaded.Error ?? "unknown error"}. " +
            "Serving the default ontology; the corpus may be empty.");

        return OntologyConfig.ProductDefaults;
    }

    /// <summary>How many roots are currently held, besides a bound server's own. For tests.</summary>
    public int Count
    {
        get
        {
            lock (_gate) return _recent.Count;
        }
    }

    /// <summary>
    /// The tools for <paramref name="projectPath"/>. In bound mode: null for the server root or a blank path, otherwise
    /// the tools bound to that worktree. In any-repo mode a success always carries tools, and a blank path is refused.
    /// A refusal comes back as a message and no tools.
    /// </summary>
    internal bool TryResolve(string? projectPath, out DocsTools? tools, out string? refusal)
    {
        tools = null;
        refusal = null;
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            if (!IsAnyRepo) return true;
            refusal = "projectPath is required: this server runs in any-repo mode.";
            return false;
        }

        if (!TryCandidate(projectPath, out string candidate, out refusal)) return false;
        return IsAnyRepo
            ? TryResolveAnyRepo(candidate, out tools, out refusal)
            : TryResolveBound(candidate, out tools, out refusal);
    }

    private bool TryCandidate(string projectPath, out string candidate, out string? refusal)
    {
        refusal = null;
        try
        {
            candidate = _paths.GetFullPath(projectPath.Trim(), _environment.CurrentDirectory);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            candidate = projectPath;
            refusal = $"projectPath '{projectPath}' is not a valid path. Pass the absolute path of your worktree.";
            return false;
        }

        if (_files.DirectoryExists(candidate)) return true;
        refusal = $"projectPath '{candidate}' is not a directory. Pass the absolute path of your worktree.";
        return false;
    }

    private bool TryResolveBound(string candidate, out DocsTools? tools, out string? refusal)
    {
        tools = null;
        refusal = null;
        (string? serverTop, string? serverCommon, string? serverError) = ServerIdentity();
        if (serverCommon is null)
        {
            refusal = $"projectPath was refused: the server root '{_serverRoot}' is not a git repository ({serverError}), "
                + "so a worktree cannot be matched to it. Omit projectPath.";
            return false;
        }

        if (!TryRoots(candidate, out string topLevel, out string common, out string? error))
        {
            refusal = $"projectPath '{candidate}' is not inside a git repository ({error}). "
                + "Pass the absolute path of a worktree of this repository.";
            return false;
        }

        if (!string.Equals(common, serverCommon, PathComparison))
        {
            refusal = $"projectPath '{candidate}' belongs to a different repository than this server "
                + $"(its git directory is '{common}', the server's is '{serverCommon}'). "
                + "Only a worktree of the server's own repository can be queried.";
            return false;
        }

        if (string.Equals(topLevel, serverTop, PathComparison)) return true;
        return TryHeldOrBuild(topLevel, out tools, out refusal);
    }

    private bool TryResolveAnyRepo(string candidate, out DocsTools? tools, out string? refusal)
    {
        tools = null;
        if (!TryRoots(candidate, out string topLevel, out _, out string? error))
        {
            refusal = $"projectPath '{candidate}' is not inside a git repository ({error}). "
                + "Pass the absolute path of a directory inside an initialized repository.";
            return false;
        }

        if (_configs is null || _configs.FindConfigPath(topLevel) is null)
        {
            refusal = $"the repository '{topLevel}' that projectPath '{candidate}' is in has no Kyber-Weave host configuration. "
                + $"Run 'kyber-weave docs init \"{topLevel}\"' once in it, then call again.";
            return false;
        }

        return TryHeldOrBuild(topLevel, out tools, out refusal);
    }

    /// <summary>
    /// The held tools for <paramref name="root"/>, or new ones from the factory, which runs outside the lock. A root
    /// another call stored while this one was building wins, so every caller ends on one instance.
    /// </summary>
    private bool TryHeldOrBuild(string root, out DocsTools? tools, out string? refusal)
    {
        refusal = null;
        lock (_gate)
        {
            if (TryTakeHeld(root, out tools)) return true;
        }

        DocsTools created;
        try
        {
            created = _factory(root);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException
            or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException)
        {
            refusal = $"the tools for '{root}' could not be built: {exception.Message}";
            return false;
        }

        if (IsAnyRepo && created is null)
        {
            refusal = $"the tools for '{root}' could not be built.";
            return false;
        }

        lock (_gate)
        {
            if (TryTakeHeld(root, out tools)) return true;
            _recent.AddFirst((root, created));
            while (_recent.Count > Capacity) _recent.RemoveLast();
            tools = created;
            return true;
        }
    }

    /// <summary>Finds <paramref name="root"/> among the held entries and marks it most recently used. Call under <c>_gate</c>.</summary>
    private bool TryTakeHeld(string root, out DocsTools? tools)
    {
        for (LinkedListNode<(string Root, DocsTools Tools)>? node = _recent.First; node is not null; node = node.Next)
        {
            if (!string.Equals(node.Value.Root, root, PathComparison)) continue;
            _recent.Remove(node);
            _recent.AddFirst(node);
            tools = node.Value.Tools;
            return true;
        }

        tools = null;
        return false;
    }

    /// <summary>
    /// The top level and common git directory of <paramref name="directory"/>, from memory when this path was answered
    /// before. Git runs outside the lock; only a success is remembered, and the memory is cleared when it is full.
    /// </summary>
    private bool TryRoots(string directory, out string topLevel, out string commonDirectory, out string? error)
    {
        lock (_gate)
        {
            if (_pathRoots.TryGetValue(directory, out (string TopLevel, string CommonDirectory) known))
            {
                (topLevel, commonDirectory, error) = (known.TopLevel, known.CommonDirectory, null);
                return true;
            }
        }

        if (!_git.TryRoots(directory, out string? top, out string? common, out error) || top is null || common is null)
        {
            (topLevel, commonDirectory) = (string.Empty, string.Empty);
            error ??= "git named no top level";
            return false;
        }

        lock (_gate)
        {
            if (_pathRoots.Count >= PathCacheCapacity) _pathRoots.Clear();
            _pathRoots[directory] = (top, common);
        }

        (topLevel, commonDirectory) = (top, common);
        return true;
    }

    private (string? TopLevel, string? CommonDirectory, string? Error) ServerIdentity()
    {
        lock (_gate)
        {
            if (_serverIdentity is { CommonDirectory: not null } known) return known;
        }

        (string? TopLevel, string? CommonDirectory, string? Error) identity = (null, null, "this server holds no root");
        if (_serverRoot is not null)
        {
            identity = _git.TryRoots(_serverRoot, out string? top, out string? common, out string? error)
                ? (top, common, null)
                : (null, null, error);
        }

        lock (_gate)
        {
            _serverIdentity = identity;
        }

        return identity;
    }

    private StringComparison PathComparison =>
        _environment.OperatingSystem is OperatingSystemKind.Windows or OperatingSystemKind.MacOS
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}
