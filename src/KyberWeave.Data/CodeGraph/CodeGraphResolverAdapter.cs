using System.Diagnostics;
using System.Globalization;

using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.CodeGraph;
using KyberWeave.Core.Processes;
using KyberWeave.Domain.CodeGraph;

namespace KyberWeave.Data.CodeGraph;

/// <summary>
/// Default <see cref="ICodeGraphResolver"/>: reads a CodeGraph SQLite database via the
/// <c>sqlite3</c> CLI and answers symbol and route lookups.
/// </summary>
/// <remarks>
/// <para>
/// The whole point of resolving against the index rather than grepping source is that
/// CodeGraph canonicalizes what the source text does not express. An ASP.NET route
/// composed from <c>[Route("api/me")]</c> and <c>[HttpGet("usage")]</c> is indexed as
/// <c>GET /api/me/usage</c> — a string that appears nowhere in the file.
/// </para>
/// <para>
/// The index is read through one batched invocation of the <c>sqlite3</c> CLI rather
/// than the <c>Microsoft.Data.Sqlite</c> package. That package's native dependency,
/// <c>SQLitePCLRaw.lib.e_sqlite3</c>, carries advisory GHSA-2m69-gcr7-jv3q at every
/// published version with no patched release available, and this repository runs
/// blocking dependency scanning. One subprocess call loads the node table and approved
/// neighborhood edges into memory, after which every lookup is in-process — so this is
/// also faster than per-symbol querying would have been.
/// </para>
/// </remarks>
public sealed class CodeGraphResolverAdapter : ICodeGraphResolver, ICodeGraphNeighborhoodProvider, ICodeGraphSymbolEnumerator, ICodeGraphIndexFreshness
{
    private const char FieldSeparator = '\u001f';

    /// <summary>
    ///     The bound for the one batched read of the index. The whole node table comes back in one query, so this
    ///     is generous, and a hung read is stopped rather than leaving the adapter blocked.
    /// </summary>
    private static readonly TimeSpan SqliteReadTimeout = TimeSpan.FromSeconds(60);

    private static readonly string[] SymbolKinds =
        ["class", "interface", "method", "function", "struct", "enum", "type_alias"];

    private static readonly HashSet<string> NeighborhoodEdgeKinds =
        ["contains", "calls", "references", "instantiates", "extends", "implements"];

    private readonly Dictionary<string, List<CodeGraphNode>> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<CodeGraphNode>> _byQualifiedName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CodeGraphNode> _routes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<CodeGraphNode>> _byKind = new(StringComparer.Ordinal);
    private readonly List<string> _filePaths = [];
    private readonly List<CodeGraphEdge> _edges = [];
    private readonly Dictionary<string, int> _edgeDegree = new(StringComparer.Ordinal);
    private readonly IBoundedProcessExecutor _processes;
    private readonly IProcessRunner? _processesAsync;
    private readonly IFileSystem _files;
    private readonly CodeGraphIndexStaleness _stalenessRule;
    private bool _stalenessRead;
    private string? _staleness;
    private bool _loadDeferred;

    /// <inheritdoc />
    public bool IsAvailable { get; private set; }

    /// <inheritdoc />
    public string? UnavailableReason { get; private set; }

    /// <inheritdoc />
    public string DatabasePath { get; }

    /// <summary>
    /// Loads the given CodeGraph database path. Missing or unreadable indexes leave
    /// <see cref="IsAvailable"/> false with <see cref="UnavailableReason"/> set.
    /// </summary>
    /// <param name="databasePath">The CodeGraph database, as the caller spelled it.</param>
    /// <param name="files">Tells whether the database exists.</param>
    /// <param name="paths">Resolves the database path to a full path.</param>
    /// <param name="environment">Supplies the directory a relative path resolves against.</param>
    /// <param name="processes">Runs <c>sqlite3</c> under a time limit.</param>
    /// <param name="staleness">Judges whether the index has fallen behind the checkout.</param>
    public CodeGraphResolverAdapter(
        string databasePath,
        IFileSystem files,
        IPathResolver paths,
        IProcessEnvironment environment,
        IBoundedProcessExecutor processes,
        CodeGraphIndexStaleness staleness)
        : this(databasePath, files, paths, environment, processes, staleness, load: true)
    {
    }

    private CodeGraphResolverAdapter(
        string databasePath,
        IFileSystem files,
        IPathResolver paths,
        IProcessEnvironment environment,
        IBoundedProcessExecutor processes,
        CodeGraphIndexStaleness staleness,
        bool load)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(environment);
        _files = files;
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _processesAsync = processes as IProcessRunner;
        _stalenessRule = staleness ?? throw new ArgumentNullException(nameof(staleness));
        DatabasePath = paths.GetFullPath(databasePath, environment.CurrentDirectory);

        if (!load)
        {
            _loadDeferred = true;
            return;
        }

        if (!files.FileExists(DatabasePath))
        {
            UnavailableReason = $"No CodeGraph index at {DatabasePath}.";
            return;
        }

        try
        {
            Load();
            IsAvailable = true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException
                                       or System.ComponentModel.Win32Exception
                                       or TimeoutException)
        {
            UnavailableReason = ex.Message;
        }
    }

    /// <summary>
    /// Loads the given CodeGraph database path without blocking a thread. Missing or unreadable indexes leave
    /// <see cref="IsAvailable"/> false with <see cref="UnavailableReason"/> set.
    /// </summary>
    /// <param name="databasePath">The CodeGraph database, as the caller spelled it.</param>
    /// <param name="files">Tells whether the database exists.</param>
    /// <param name="paths">Resolves the database path to a full path.</param>
    /// <param name="environment">Supplies the directory a relative path resolves against.</param>
    /// <param name="processes">Runs <c>sqlite3</c> under a time limit.</param>
    /// <param name="staleness">Judges whether the index has fallen behind the checkout.</param>
    /// <param name="cancellationToken">Cancels the index read.</param>
    /// <returns>The loaded adapter, available only when the index could be read.</returns>
    public static async Task<CodeGraphResolverAdapter> CreateAsync(
        string databasePath,
        IFileSystem files,
        IPathResolver paths,
        IProcessEnvironment environment,
        IProcessRunner processes,
        CodeGraphIndexStaleness staleness,
        CancellationToken cancellationToken = default)
    {
        CodeGraphResolverAdapter adapter = new(databasePath, files, paths, environment, processes, staleness, load: false);
        await adapter.LoadDeferredAsync(cancellationToken).ConfigureAwait(false);
        return adapter;
    }

    private async Task LoadDeferredAsync(CancellationToken cancellationToken)
    {
        if (!_loadDeferred) return;
        _loadDeferred = false;

        if (!_files.FileExists(DatabasePath))
        {
            UnavailableReason = $"No CodeGraph index at {DatabasePath}.";
            return;
        }

        try
        {
            await LoadAsync(cancellationToken).ConfigureAwait(false);
            IsAvailable = true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException
                                       or System.ComponentModel.Win32Exception
                                       or TimeoutException)
        {
            UnavailableReason = ex.Message;
        }
    }

    // Nodes and the bounded neighborhood surface are loaded in the same sqlite3
    // process. Aside from avoiding per-node subprocess cost, this makes the adapter
    // a stable snapshot even when CodeGraph replaces its index after construction.
    // 'import' nodes and edges are excluded: module imports are too broad to be a
    // useful documentation relationship.
    private const string LoadSql = """
        SELECT 'node', id, kind, name, qualified_name, file_path, language, start_line, end_line
        FROM nodes
        WHERE kind <> 'import'
        UNION ALL
        SELECT 'edge', source, target, kind, '', '', '', '', ''
        FROM edges
        WHERE kind IN ('contains', 'calls', 'references', 'instantiates', 'extends', 'implements')
        """;

    private const string StalenessSql = """
        SELECT 'file', path, CAST(modified_at AS INTEGER), node_count FROM files
        UNION ALL
        SELECT 'meta', key, value, '' FROM project_metadata WHERE key = 'indexed_at_commit'
        """;

    private void Load()
    {
        foreach (string line in RunSqlite(LoadSql))
        {
            IndexLine(line);
        }
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        foreach (string line in await RunSqliteAsync(LoadSql, cancellationToken).ConfigureAwait(false))
        {
            IndexLine(line);
        }
    }

    private void IndexLine(string line)
    {
        string[] parts = line.Split(FieldSeparator);
        if (parts.Length < 9) return;

        if (parts[0] == "edge")
        {
            CodeGraphEdge edge = new CodeGraphEdge(parts[1], parts[2], parts[3]);
            if (!NeighborhoodEdgeKinds.Contains(edge.Kind)) return;

            _edges.Add(edge);
            IncrementDegree(edge.SourceId);
            if (!StringComparer.Ordinal.Equals(edge.SourceId, edge.TargetId))
                IncrementDegree(edge.TargetId);
            return;
        }

        if (parts[0] != "node") return;

        _ = int.TryParse(parts[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out int startLine);
        _ = int.TryParse(parts[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out int endLine);
        CodeGraphNode node = new CodeGraphNode(parts[1], parts[2], parts[3], parts[4], parts[5], parts[6], startLine, endLine);

        Index(_byName, node.Name, node);
        Index(_byQualifiedName, node.QualifiedName, node);
        Index(_byKind, node.Kind, node);

        if (node.Kind == "route")
        {
            _routes.TryAdd(node.Name, node);
        }

        if (node.FilePath.Length > 0)
        {
            _filePaths.Add(node.FilePath);
        }
    }

    /// <inheritdoc />
    public string? DescribeStaleness()
    {
        if (!IsAvailable) return null;
        if (_stalenessRead) return _staleness;

        // The checkout is the folder that holds the index folder: <root>/.codegraph/codegraph.db.
        string? root = Path.GetDirectoryName(Path.GetDirectoryName(DatabasePath));
        _stalenessRead = true;
        if (root is null) return _staleness = null;

        (string? indexedCommit, IReadOnlyDictionary<string, (long ModifiedMilliseconds, int NodeCount)> files) = ReadStalenessFacts();
        return _staleness = _stalenessRule.Describe(root, indexedCommit, files);
    }

    /// <inheritdoc />
    public async Task<string?> DescribeStalenessAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAvailable) return null;
        if (_stalenessRead) return _staleness;

        // The checkout is the folder that holds the index folder: <root>/.codegraph/codegraph.db.
        string? root = Path.GetDirectoryName(Path.GetDirectoryName(DatabasePath));
        _stalenessRead = true;
        if (root is null) return _staleness = null;

        (string? indexedCommit, IReadOnlyDictionary<string, (long ModifiedMilliseconds, int NodeCount)> files) =
            await ReadStalenessFactsAsync(cancellationToken).ConfigureAwait(false);
        _staleness = await _stalenessRule.DescribeAsync(root, indexedCommit, files, cancellationToken).ConfigureAwait(false);
        return _staleness;
    }

    /// <summary>Reads the recorded commit and per-file state the staleness rule needs.</summary>
    /// <returns>The commit the index recorded (null when it recorded none), and each indexed path with its recorded modification time (ms) and symbol count.</returns>
    /// <remarks>
    /// Read on demand, and separately from the node table: only a caller that has something to explain needs it, and
    /// an index without these tables simply cannot be judged.
    /// </remarks>
    public (string? IndexedCommit, IReadOnlyDictionary<string, (long ModifiedMilliseconds, int NodeCount)> Files) ReadStalenessFacts()
    {
        Dictionary<string, (long ModifiedMilliseconds, int NodeCount)> files = new(StringComparer.Ordinal);
        string? indexedCommit = null;

        if (!IsAvailable) return (null, files);

        try
        {
            foreach (string line in RunSqlite(StalenessSql))
            {
                CollectStalenessRow(line, files, ref indexedCommit);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException
                                       or System.ComponentModel.Win32Exception
                                       or TimeoutException)
        {
            return (null, files);
        }

        return (indexedCommit, files);
    }

    /// <summary>Reads the recorded commit and per-file state the staleness rule needs, without blocking a thread.</summary>
    /// <param name="cancellationToken">Cancels the index read.</param>
    /// <returns>The commit the index recorded (null when it recorded none), and each indexed path with its recorded modification time (ms) and symbol count.</returns>
    public async Task<(string? IndexedCommit, IReadOnlyDictionary<string, (long ModifiedMilliseconds, int NodeCount)> Files)>
        ReadStalenessFactsAsync(CancellationToken cancellationToken = default)
    {
        Dictionary<string, (long ModifiedMilliseconds, int NodeCount)> files = new(StringComparer.Ordinal);
        string? indexedCommit = null;

        if (!IsAvailable) return (null, files);

        try
        {
            foreach (string line in await RunSqliteAsync(StalenessSql, cancellationToken).ConfigureAwait(false))
            {
                CollectStalenessRow(line, files, ref indexedCommit);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException
                                       or System.ComponentModel.Win32Exception
                                       or TimeoutException)
        {
            return (null, files);
        }

        return (indexedCommit, files);
    }

    private static void CollectStalenessRow(
        string line,
        IDictionary<string, (long ModifiedMilliseconds, int NodeCount)> files,
        ref string? indexedCommit)
    {
        string[] parts = line.Split(FieldSeparator);
        if (parts.Length < 4) return;
        if (parts[0] == "meta")
        {
            indexedCommit = parts[2].Trim();
            return;
        }

        _ = long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long modified);
        _ = int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int nodeCount);
        files[parts[1]] = (modified, nodeCount);
    }

    private void IncrementDegree(string nodeId)
    {
        _edgeDegree.TryGetValue(nodeId, out int degree);
        _edgeDegree[nodeId] = degree + 1;
    }

    private static void Index(Dictionary<string, List<CodeGraphNode>> map, string key, CodeGraphNode node)
    {
        if (string.IsNullOrEmpty(key)) return;
        if (!map.TryGetValue(key, out List<CodeGraphNode>? list))
        {
            list = [];
            map[key] = list;
        }

        list.Add(node);
    }

    private IEnumerable<string> RunSqlite(string sql)
    {
        ProcessStartInfo startInfo = SqliteStartInfo(sql);

        // The runner drains both pipes concurrently. This query returns the whole node table, so stdout is
        // large by design and stderr must not be left to fill behind it. A read that outlasts the bound is
        // killed and throws TimeoutException, which the constructor reports as the index being unavailable.
        ProcessResult result = _processes.Run(startInfo, string.Empty, SqliteReadTimeout);
        return SqliteOutput(result);
    }

    private async Task<IEnumerable<string>> RunSqliteAsync(string sql, CancellationToken cancellationToken)
    {
        IProcessRunner processesAsync = _processesAsync
            ?? throw new InvalidOperationException("The CodeGraph index read needs a process runner that can run sqlite3 without blocking.");
        ProcessStartInfo startInfo = SqliteStartInfo(sql);
        ProcessResult result = await processesAsync.RunAsync(
            startInfo,
            string.Empty,
            SqliteReadTimeout,
            cancellationToken).ConfigureAwait(false);
        return SqliteOutput(result);
    }

    private ProcessStartInfo SqliteStartInfo(string sql)
    {
        ProcessStartInfo startInfo = new ProcessStartInfo("sqlite3")
        {
            // Input is redirected only so the runner can close it: sqlite3 runs the SQL argument and exits.
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        // Arguments are passed as a list, never through a shell, so the database path
        // cannot be interpreted as anything but a path.
        //
        // The connection is deliberately not opened with '-readonly'. The CodeGraph
        // daemon leaves the index in WAL journal mode, and opening a WAL database
        // requires a shared-memory (-shm) file; '-readonly' forbids creating one, so it
        // fails outright with "unable to open database file" whenever no other connection
        // is already holding the index open. Only SELECT statements are ever issued.
        startInfo.ArgumentList.Add("-noheader");
        startInfo.ArgumentList.Add("-separator");
        startInfo.ArgumentList.Add(FieldSeparator.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(DatabasePath);
        startInfo.ArgumentList.Add(sql);
        return startInfo;
    }

    private static IEnumerable<string> SqliteOutput(ProcessResult result)
    {
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Reading the CodeGraph index with 'sqlite3' failed (exit {result.ExitCode}): {result.StandardError.Trim()}");
        }

        return result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <inheritdoc />
    public IReadOnlyList<CodeGraphNode> ResolveSymbol(string name)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(name)) return [];

        if (_byName.TryGetValue(name, out List<CodeGraphNode>? byName)) return byName;
        if (_byQualifiedName.TryGetValue(name, out List<CodeGraphNode>? byQualified)) return byQualified;
        return [];
    }

    /// <inheritdoc />
    public IReadOnlyList<CodeGraphNode> ResolveRoute(string route)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(route)) return [];
        return _routes.TryGetValue(route, out CodeGraphNode? node) ? [node] : [];
    }

    /// <inheritdoc />
    public bool HasFilesUnder(string relativePathPrefix)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(relativePathPrefix)) return false;

        string normalized = relativePathPrefix.Replace('\\', '/').TrimEnd('/');

        // "." is the repository root: a system-level document legitimately describes the
        // whole tree, so every indexed file is beneath it.
        if (normalized is "." or "") return _filePaths.Count > 0;

        return _filePaths.Exists(p => p.StartsWith(normalized + "/", StringComparison.OrdinalIgnoreCase));
    }

    /// <inheritdoc />
    public IReadOnlyList<string> CandidateNames(string likePattern)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(likePattern)) return [];

        // Anchor on a short prefix so the candidate pool stays small; a rename usually
        // preserves a prefix or a suffix, not neither.
        string prefix = likePattern.Length <= 4 ? likePattern : likePattern[..4];

        return _byName
            .Where(kv => kv.Value.Exists(n => SymbolKinds.Contains(n.Kind, StringComparer.Ordinal)))
            .Select(kv => kv.Key)
            .Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Take(200)
            .ToList();
    }

    /// <inheritdoc />
    public IReadOnlyList<string> AllRoutes() => IsAvailable ? _routes.Keys.ToList() : [];

    /// <inheritdoc />
    public IReadOnlyList<CodeGraphEdge> GetEdges(
        IReadOnlyCollection<string> nodeIds,
        int maxDegree)
    {
        ArgumentNullException.ThrowIfNull(nodeIds);
        ArgumentOutOfRangeException.ThrowIfNegative(maxDegree);

        if (!IsAvailable || nodeIds.Count == 0) return [];

        HashSet<string> requested = nodeIds.ToHashSet(StringComparer.Ordinal);
        return _edges
            .Where(edge =>
                requested.Contains(edge.SourceId)
                && requested.Contains(edge.TargetId)
                && _edgeDegree.GetValueOrDefault(edge.SourceId) <= maxDegree
                && _edgeDegree.GetValueOrDefault(edge.TargetId) <= maxDegree)
            .ToArray();
    }

    /// <inheritdoc />
    public IReadOnlyList<CodeGraphNode> NodesOfKind(string kind)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(kind)) return [];
        return _byKind.TryGetValue(kind, out List<CodeGraphNode>? nodes) ? nodes : [];
    }
}
