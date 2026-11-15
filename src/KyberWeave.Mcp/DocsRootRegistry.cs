using System.Diagnostics;
using KyberWeave.Core.CodeGraph;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Core.Docs.Search;
using KyberWeave.Core.Processes;

namespace KyberWeave.Mcp;

/// <summary>
/// Resolves the optional per-call <c>projectPath</c> to a set of tools bound to that checkout, and
/// keeps the most recently used few so a worker's repeated calls reuse one corpus instead of
/// rebuilding it.
/// </summary>
/// <remarks>
/// <para>
/// The server binds one root at startup, but agent runs work in linked worktrees created after it
/// started, so the corpus a call answers from has to be nameable per call. Only a worktree of the
/// <b>same repository</b> as the server root is accepted: git is asked for the candidate's top
/// level and common git directory, and the common directory must equal the server root's. Anything
/// else is refused with a message, never silently answered from the server root.
/// </para>
/// <para>
/// Per-root state is the document host, the analysis reader and the CodeGraph refresh schedule, all
/// of which live inside the <see cref="DocsTools"/> the factory builds. The registry bounds how many
/// of those it holds (<see cref="Capacity"/>, least recently used evicted); the server root's own
/// tools are never evicted.
/// </para>
/// </remarks>
public sealed class DocsRootRegistry
{
    /// <summary>How many non-server roots are kept at once.</summary>
    public const int Capacity = 4;

    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(5);

    private readonly string _serverRoot;
    private readonly Func<string, DocsTools> _factory;
    private readonly Lock _gate = new();
    private readonly LinkedList<(string Root, DocsTools Tools)> _recent = new();
    private (string? TopLevel, string? CommonDirectory, string? Error)? _serverIdentity;

    /// <summary>A registry for the repository bound at <paramref name="serverRoot"/>.</summary>
    /// <param name="serverRoot">The root the server was started with.</param>
    /// <param name="factory">Builds the tools for another worktree root of the same repository.</param>
    public DocsRootRegistry(string serverRoot, Func<string, DocsTools> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverRoot);
        ArgumentNullException.ThrowIfNull(factory);
        _serverRoot = Path.GetFullPath(serverRoot);
        _factory = factory;
    }

    /// <summary>A registry whose other roots get the same composition the server root has.</summary>
    /// <param name="serverRoot">The root the server was started with.</param>
    /// <param name="codeGraphRefreshEnabled">False when the process runs with <c>--no-codegraph-refresh</c>.</param>
    public static DocsRootRegistry ForRepository(string serverRoot, bool codeGraphRefreshEnabled) =>
        new(serverRoot, root => CreateTools(root, codeGraphRefreshEnabled, null));

    /// <summary>The tools for one checkout: its own document host, analysis reader and refresh schedule.</summary>
    /// <param name="repoRoot">The checkout root.</param>
    /// <param name="codeGraphRefreshEnabled">False when the process runs with <c>--no-codegraph-refresh</c>.</param>
    /// <param name="registry">The registry the tools hand a per-call <c>projectPath</c> to, or null.</param>
    public static DocsTools CreateTools(string repoRoot, bool codeGraphRefreshEnabled, DocsRootRegistry? registry)
    {
        OntologyConfig ontology = ResolveOntology(repoRoot);
        DocumentIndexHost host = new(
            repoRoot,
            () => CodeGraphResolverAdapter.ForRepository(repoRoot),
            () => new DocumentLoader(repoRoot, ontology).Load(),
            ontology.DocsRoots,
            ontology.ResolvedCatalogPath);
        return new DocsTools(
            host,
            new RepositoryDocsAnalysisReader(repoRoot),
            new CodeGraphFreshness(repoRoot, codeGraphRefreshEnabled),
            registry);
    }

    /// <summary>
    /// The host's ontology from <c>.kyber-weave/kyber-weave.yml</c>. A config that cannot be read is
    /// reported on stderr and stepped over rather than being fatal: a client that has already spawned
    /// this process gets a server that answers and says why, instead of a transport that dies.
    /// </summary>
    public static OntologyConfig ResolveOntology(string repoRoot)
    {
        KyberWeaveConfigLoadResult loaded = KyberWeaveConfigLoader.TryLoad(repoRoot);
        if (loaded is { Success: true, Config: not null })
        {
            return loaded.Config.Ontology;
        }

        // stderr, never stdout: stdout is the JSON-RPC transport.
        Console.Error.WriteLine(
            $"{KyberWeaveConfigLoader.ConfigLoadErrorCode}: Failed to load " +
            $"'{loaded.ConfigPath ?? "kyber-weave.yml"}': {loaded.Error ?? "unknown error"}. " +
            "Serving the default ontology; the corpus may be empty.");

        return OntologyConfig.ProductDefaults;
    }

    /// <summary>How many non-server roots are currently held. For tests.</summary>
    public int Count
    {
        get
        {
            lock (_gate) return _recent.Count;
        }
    }

    /// <summary>
    /// The tools for <paramref name="projectPath"/>: null for the server root, otherwise the tools
    /// bound to that worktree. A refusal comes back as a message and no tools.
    /// </summary>
    internal bool TryResolve(string? projectPath, out DocsTools? tools, out string? refusal)
    {
        tools = null;
        refusal = null;
        if (string.IsNullOrWhiteSpace(projectPath)) return true;

        string candidate;
        try
        {
            candidate = Path.GetFullPath(projectPath.Trim());
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            refusal = $"projectPath '{projectPath}' is not a valid path. Pass the absolute path of your worktree.";
            return false;
        }

        if (!Directory.Exists(candidate))
        {
            refusal = $"projectPath '{candidate}' is not a directory. Pass the absolute path of your worktree.";
            return false;
        }

        (string? serverTop, string? serverCommon, string? serverError) = ServerIdentity();
        if (serverCommon is null)
        {
            refusal = $"projectPath was refused: the server root '{_serverRoot}' is not a git repository ({serverError}), "
                + "so a worktree cannot be matched to it. Omit projectPath.";
            return false;
        }

        if (!TryGitRoots(candidate, out string? topLevel, out string? common, out string? error))
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

        lock (_gate)
        {
            for (LinkedListNode<(string Root, DocsTools Tools)>? node = _recent.First; node is not null; node = node.Next)
            {
                if (!string.Equals(node.Value.Root, topLevel, PathComparison)) continue;
                _recent.Remove(node);
                _recent.AddFirst(node);
                tools = node.Value.Tools;
                return true;
            }

            DocsTools created = _factory(topLevel!);
            _recent.AddFirst((topLevel!, created));
            while (_recent.Count > Capacity) _recent.RemoveLast();
            tools = created;
            return true;
        }
    }

    private (string? TopLevel, string? CommonDirectory, string? Error) ServerIdentity()
    {
        lock (_gate)
        {
            if (_serverIdentity is { CommonDirectory: not null } known) return known;
            _serverIdentity = TryGitRoots(_serverRoot, out string? top, out string? common, out string? error)
                ? (top, common, null)
                : (null, null, error);
            return _serverIdentity.Value;
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    /// <summary>Asks git for the working tree's top level and the git directory its worktrees share.</summary>
    private static bool TryGitRoots(string directory, out string? topLevel, out string? commonDirectory, out string? error)
    {
        topLevel = null;
        commonDirectory = null;
        ProcessStartInfo startInfo = new("git")
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in new[] { "rev-parse", "--path-format=absolute", "--show-toplevel", "--git-common-dir" })
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            ProcessResult result = ProcessRunner.Run(startInfo, string.Empty, GitTimeout);
            if (result.ExitCode != 0)
            {
                error = CorpusProvenance.FirstLine(result.StandardError);
                return false;
            }

            string[] lines = result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length < 2)
            {
                error = "git reported no working tree";
                return false;
            }

            topLevel = Path.GetFullPath(lines[0].Trim());
            commonDirectory = Path.GetFullPath(lines[1].Trim());
            error = null;
            return true;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
            or InvalidOperationException
            or TimeoutException)
        {
            error = exception.Message;
            return false;
        }
    }
}
