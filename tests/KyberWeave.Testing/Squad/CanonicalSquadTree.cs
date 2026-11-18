using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Corpus;
using KyberWeave.Tests.FileSystem;

namespace KyberWeave.Testing.Squad;

/// <summary>
/// The shipped Squad source (<c>products/kyber-squad</c>) copied into an in-memory file system, so a test that renders or
/// loads the real source needs no disk and can still change a copy of it freely.
/// </summary>
/// <remarks>
/// The shipped files are read once through the corpus seam and held as bytes; each instance writes its own copy into its own
/// <see cref="InMemoryFileSystem"/>, so one test's edit never reaches another's tree.
/// </remarks>
public sealed class CanonicalSquadTree
{
    private const string CorpusDirectory = "products/kyber-squad";

    private static readonly Lazy<IReadOnlyList<KeyValuePair<string, byte[]>>> Snapshot = new(ReadSnapshot);

    private CanonicalSquadTree(InMemoryFileSystem files)
    {
        Files = files;
        Services = new InMemorySquadServices(files);
        ProductRoot = Path.Combine(files.Root, "kyber-squad");
        files.CreateDirectory(ProductRoot);
        foreach ((string relativePath, byte[] bytes) in Snapshot.Value)
        {
            string path = ToPath(ProductRoot, relativePath);
            files.CreateDirectory(Path.GetDirectoryName(path)!);
            files.WriteAllBytes(path, bytes);
        }
    }

    /// <summary>Gets the in-memory file system the tree lives in.</summary>
    public InMemoryFileSystem Files { get; }

    /// <summary>Gets the Squad loader and render-input reader over <see cref="Files"/>.</summary>
    public InMemorySquadServices Services { get; }

    /// <summary>Gets the root of the copied source, derived from the file system's own root.</summary>
    public string ProductRoot { get; }

    /// <summary>Copies the shipped Squad source into a new in-memory file system.</summary>
    /// <returns>The tree.</returns>
    public static CanonicalSquadTree Create() => new(new InMemoryFileSystem());

    /// <summary>Gets the path of a file under the copied source from its source-relative path with forward slashes.</summary>
    /// <param name="relativePath">The path relative to the source root.</param>
    /// <returns>The absolute path in the in-memory file system.</returns>
    public string PathOf(string relativePath) => ToPath(ProductRoot, relativePath);

    /// <summary>Loads the copied source.</summary>
    /// <returns>The loaded source.</returns>
    public SquadSource Load() => Services.Load(ProductRoot);

    /// <summary>Reads the render's inputs onto a request, as the registry does before it renders.</summary>
    /// <param name="request">The request to prepare.</param>
    /// <returns>The prepared request.</returns>
    public SquadRenderRequest Prepare(SquadRenderRequest request) => Services.RenderInputReader().Prepare(request);

    /// <summary>Creates a registry over the renderers that reads its render inputs from this tree's file system.</summary>
    /// <param name="renderers">The renderers the registry holds.</param>
    /// <returns>The registry.</returns>
    public SquadRendererRegistry Registry(IEnumerable<ISquadRenderer> renderers) =>
        new(renderers, Services.RenderInputReader());

    private static string ToPath(string root, string relativePath) =>
        Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static IReadOnlyList<KeyValuePair<string, byte[]>> ReadSnapshot()
    {
        ICorpusSource corpus = CheckoutCorpusSource.Checkout;
        string prefix = CorpusDirectory + "/";
        return corpus
            .EnumerateFiles(CorpusDirectory)
            .Select(path => KeyValuePair.Create(path[prefix.Length..], corpus.ReadBytes(path)))
            .ToList();
    }
}
