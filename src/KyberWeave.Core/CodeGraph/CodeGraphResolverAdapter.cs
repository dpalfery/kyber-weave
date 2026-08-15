using System.Diagnostics;
using System.Globalization;
using KyberWeave.Core.Processes;

namespace KyberWeave.Core.CodeGraph;

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
public sealed class CodeGraphResolverAdapter : ICodeGraphResolver, ICodeGraphNeighborhoodProvider
{
    private const char FieldSeparator = '\u001f';

    private static readonly string[] SymbolKinds =
        ["class", "interface", "method", "function", "struct", "enum", "type_alias"];

    private static readonly HashSet<string> NeighborhoodEdgeKinds =
        ["contains", "calls", "references", "instantiates", "extends", "implements"];

    private readonly Dictionary<string, List<CodeGraphNode>> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<CodeGraphNode>> _byQualifiedName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CodeGraphNode> _routes = new(StringComparer.Ordinal);
    private readonly List<string> _filePaths = [];
    private readonly List<CodeGraphEdge> _edges = [];
    private readonly Dictionary<string, int> _edgeDegree = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public bool IsAvailable { get; }

    /// <inheritdoc />
    public string? UnavailableReason { get; }

    /// <inheritdoc />
    public string DatabasePath { get; }

    /// <summary>
    /// Loads the given CodeGraph database path. Missing or unreadable indexes leave
    /// <see cref="IsAvailable"/> false with <see cref="UnavailableReason"/> set.
    /// </summary>
    public CodeGraphResolverAdapter(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = Path.GetFullPath(databasePath);

        if (!File.Exists(DatabasePath))
        {
            UnavailableReason = $"No CodeGraph index at {DatabasePath}.";
            return;
        }

        try
        {
            Load();
            IsAvailable = true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            UnavailableReason = ex.Message;
        }
    }

    /// <summary>
    /// Creates an adapter for the standard <c>.codegraph/codegraph.db</c> path under a
    /// repository root. Composition roots call this; core consumers take
    /// <see cref="ICodeGraphResolver"/> only.
    /// </summary>
    public static CodeGraphResolverAdapter ForRepository(string repoRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        string databasePath = Path.Combine(Path.GetFullPath(repoRoot), ".codegraph", "codegraph.db");
        return new CodeGraphResolverAdapter(databasePath);
    }

    private void Load()
    {
        // Nodes and the bounded neighborhood surface are loaded in the same sqlite3
        // process. Aside from avoiding per-node subprocess cost, this makes the adapter
        // a stable snapshot even when CodeGraph replaces its index after construction.
        // 'import' nodes and edges are excluded: module imports are too broad to be a
        // useful documentation relationship.
        const string sql = """
            SELECT 'node', id, kind, name, qualified_name, file_path, language, start_line
            FROM nodes
            WHERE kind <> 'import'
            UNION ALL
            SELECT 'edge', source, target, kind, '', '', '', ''
            FROM edges
            WHERE kind IN ('contains', 'calls', 'references', 'instantiates', 'extends', 'implements')
            """;

        foreach (string line in RunSqlite(sql))
        {
            string[] parts = line.Split(FieldSeparator);
            if (parts.Length < 8) continue;

            if (parts[0] == "edge")
            {
                CodeGraphEdge edge = new CodeGraphEdge(parts[1], parts[2], parts[3]);
                if (!NeighborhoodEdgeKinds.Contains(edge.Kind)) continue;

                _edges.Add(edge);
                IncrementDegree(edge.SourceId);
                if (!StringComparer.Ordinal.Equals(edge.SourceId, edge.TargetId))
                    IncrementDegree(edge.TargetId);
                continue;
            }

            if (parts[0] != "node") continue;

            _ = int.TryParse(parts[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out int startLine);
            CodeGraphNode node = new CodeGraphNode(parts[1], parts[2], parts[3], parts[4], parts[5], parts[6], startLine);

            Index(_byName, node.Name, node);
            Index(_byQualifiedName, node.QualifiedName, node);

            if (node.Kind == "route")
            {
                _routes.TryAdd(node.Name, node);
            }

            if (node.FilePath.Length > 0)
            {
                _filePaths.Add(node.FilePath);
            }
        }
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
        ProcessStartInfo startInfo = new ProcessStartInfo("sqlite3")
        {
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

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the 'sqlite3' process.");

        // Both pipes are drained concurrently. This query returns the whole node table, so
        // stdout is large by design and stderr must not be left to fill behind it.
        ProcessResult result = ProcessRunner.ReadToEnd(process);

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
    public IReadOnlyList<string> CandidateNames(string like)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(like)) return [];

        // Anchor on a short prefix so the candidate pool stays small; a rename usually
        // preserves a prefix or a suffix, not neither.
        string prefix = like.Length <= 4 ? like : like[..4];

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
}
