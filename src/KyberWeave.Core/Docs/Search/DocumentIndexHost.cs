using KyberWeave.Core.CodeGraph;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.Docs.Parsing;

namespace KyberWeave.Core.Docs.Search;

/// <summary>
/// Holds the current <see cref="DocumentIndex"/> for a long-lived process and rebuilds it
/// when the corpus or the code graph changes underneath it.
/// </summary>
/// <remarks>
/// <para>
/// Reloading is required rather than optional: the CodeGraph daemon rewrites
/// <c>.codegraph/codegraph.db</c> continuously, so an index built once at startup would
/// start answering with stale joins within minutes of a rename.
/// </para>
/// <para>
/// The two inputs are tracked separately, because they change at wildly different rates.
/// The database is rewritten constantly while a session is active; the documentation is
/// edited by hand. Folding both into one fingerprint meant a background daemon write
/// forced a full re-read and re-vectorisation of every document — the expensive half —
/// to refresh joins that are the cheap half. Documentation changes rebuild the corpus;
/// code-graph changes rebuild only the joins.
/// </para>
/// <para>
/// Composition roots supply the loader and resolver factories. This type never constructs
/// <see cref="DocumentLoader"/> or <see cref="ICodeGraphResolver"/> implementations itself.
/// </para>
/// </remarks>
public sealed class DocumentIndexHost
{
    private readonly string _repoRoot;
    private readonly IReadOnlyList<string> _docsRelativeRoots;
    private readonly string? _catalogRelativePath;
    private readonly Func<ICodeGraphResolver> _resolverFactory;
    private readonly Func<DocumentSet> _documentSetFactory;
    private readonly Lock _gate = new();

    private DocumentCorpus? _corpus;
    private DocumentIndex? _index;
    private long _docsStamp;
    private long _codeGraphStamp;

    public DocumentIndexHost(
        string repoRoot,
        Func<ICodeGraphResolver> resolverFactory,
        Func<DocumentSet> documentSetFactory,
        string docsRelativeRoot = OntologyConfig.DefaultDocsRoot)
        : this(repoRoot, resolverFactory, documentSetFactory, [docsRelativeRoot], null)
    {
    }

    /// <summary>
    /// Watches every documentation root, plus the catalog — which is tracked in its own
    /// right so that one living outside all of the roots still invalidates the corpus when
    /// edited.
    /// </summary>
    public DocumentIndexHost(
        string repoRoot,
        Func<ICodeGraphResolver> resolverFactory,
        Func<DocumentSet> documentSetFactory,
        IReadOnlyList<string> docsRelativeRoots,
        string? catalogRelativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentNullException.ThrowIfNull(resolverFactory);
        ArgumentNullException.ThrowIfNull(documentSetFactory);
        ArgumentNullException.ThrowIfNull(docsRelativeRoots);
        _repoRoot = Path.GetFullPath(repoRoot);
        _docsRelativeRoots = docsRelativeRoots;
        _catalogRelativePath = catalogRelativePath;
        _resolverFactory = resolverFactory;
        _documentSetFactory = documentSetFactory;
    }

    /// <summary>Repository root the index is built over.</summary>
    public string RepoRoot => _repoRoot;

    /// <summary>Documentation roots relative to <see cref="RepoRoot"/> (from ontology / host config).</summary>
    public IReadOnlyList<string> DocsRelativeRoots => _docsRelativeRoots;

    /// <summary>The primary documentation root.</summary>
    public string DocsRelativeRoot =>
        _docsRelativeRoots.Count > 0 ? _docsRelativeRoots[0] : OntologyConfig.DefaultDocsRoot;

    /// <summary>How many times the document corpus has been parsed. For diagnostics and tests.</summary>
    public int CorpusBuilds { get; private set; }

    /// <summary>How many times the code joins have been resolved. For diagnostics and tests.</summary>
    public int JoinBuilds { get; private set; }

    /// <summary>The current index, rebuilding whichever half has gone stale.</summary>
    public DocumentIndex Current()
    {
        var docsStamp = ComputeDocsStamp();
        var codeGraphStamp = ComputeCodeGraphStamp();

        lock (_gate)
        {
            var corpusStale = _corpus is null || docsStamp != _docsStamp;

            if (corpusStale)
            {
                _corpus = DocumentCorpus.Build(_documentSetFactory());
                _docsStamp = docsStamp;
                CorpusBuilds++;
            }

            if (_index is null || corpusStale || codeGraphStamp != _codeGraphStamp)
            {
                _index = DocumentIndex.Build(_corpus!, _resolverFactory());
                _codeGraphStamp = codeGraphStamp;
                JoinBuilds++;
            }

            return _index;
        }
    }

    /// <summary>
    /// A fingerprint of the documentation tree: newest mtime and file count, so an edit
    /// or a deletion is both noticed.
    /// </summary>
    /// <remarks>
    /// The stamp fingerprints the same document set <see cref="DocumentLoader.Load"/>
    /// produces — each path once — so overlapping roots and a catalog that already lives
    /// inside a root cannot inflate the count and make stamp mismatches look like corpus
    /// changes when nothing moved.
    /// </remarks>
    internal long ComputeDocsStamp()
    {
        long stamp = 17;
        var count = 0;
        long newest = 0;
        var visited = new HashSet<string>(DocsRootPath.PathComparer);

        foreach (var relativeRoot in _docsRelativeRoots)
        {
            var docsRoot = relativeRoot == DocsRootPath.RepositoryRoot
                ? _repoRoot
                : Path.Combine(_repoRoot, relativeRoot.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(docsRoot)) continue;

            foreach (var file in Directory.EnumerateFiles(docsRoot, "*.md", SearchOption.AllDirectories))
            {
                if (!visited.Add(file)) continue;

                count++;
                var ticks = File.GetLastWriteTimeUtc(file).Ticks;
                if (ticks > newest) newest = ticks;
            }
        }

        if (_catalogRelativePath is not null)
        {
            var catalog = Path.Combine(
                _repoRoot, _catalogRelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(catalog) && visited.Add(catalog))
            {
                count++;
                var ticks = File.GetLastWriteTimeUtc(catalog).Ticks;
                if (ticks > newest) newest = ticks;
            }
        }

        if (count == 0) return 0;

        stamp = (stamp * 31) + newest;
        stamp = (stamp * 31) + count;
        return stamp;
    }

    private long ComputeCodeGraphStamp()
    {
        var db = Path.Combine(_repoRoot, ".codegraph", "codegraph.db");
        if (!File.Exists(db)) return 0;

        var info = new FileInfo(db);
        return (info.LastWriteTimeUtc.Ticks * 31) + info.Length;
    }
}
