using KyberWeave.Contracts.Repositories;
using KyberWeave.Data.Hooks;

namespace KyberWeave.Tests.Corpus;

/// <summary>The shipped content of a repository checkout, read through the file-system seam.</summary>
/// <remarks>
/// The repository root is found once per test process, by the same walk up from the test output directory that
/// <see cref="KyberWeaveTestPaths"/> has always done; every content test shares that one answer through
/// <see cref="Checkout"/>. Nothing here writes, starts a program or reads the clock.
/// </remarks>
public sealed class CheckoutCorpusSource : ICorpusSource
{
    private readonly IFileSystem _files;

    /// <summary>Gets the corpus of the checkout this test process runs from.</summary>
    public static ICorpusSource Checkout { get; } = new CheckoutCorpusSource(KyberWeaveTestPaths.ToolRoot, new PhysicalFileSystem());

    /// <summary>Creates a corpus over one checkout.</summary>
    /// <param name="root">The repository root's absolute path.</param>
    /// <param name="files">The file system to read it through.</param>
    public CheckoutCorpusSource(string root, IFileSystem files)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
        _files = files ?? throw new ArgumentNullException(nameof(files));
    }

    /// <inheritdoc />
    public string Root { get; }

    /// <inheritdoc />
    public bool Exists(string relativePath)
    {
        string path = CorpusPath.Resolve(Root, CorpusPath.Normalize(relativePath));
        return _files.FileExists(path);
    }

    /// <inheritdoc />
    public string ReadText(string relativePath)
    {
        string path = CorpusPath.Resolve(Root, CorpusPath.Normalize(relativePath));

        try
        {
            return _files.ReadAllText(path);
        }
        catch (DirectoryNotFoundException exception)
        {
            throw new FileNotFoundException($"The corpus holds no file at '{relativePath}'.", path, exception);
        }
    }

    /// <inheritdoc />
    public byte[] ReadBytes(string relativePath)
    {
        string path = CorpusPath.Resolve(Root, CorpusPath.Normalize(relativePath));

        try
        {
            return _files.ReadAllBytes(path);
        }
        catch (DirectoryNotFoundException exception)
        {
            throw new FileNotFoundException($"The corpus holds no file at '{relativePath}'.", path, exception);
        }
    }

    /// <inheritdoc />
    public IEnumerable<string> EnumerateFiles(
        string relativeDirectory,
        string searchPattern = "*",
        bool recursive = true)
    {
        string directory = CorpusPath.Resolve(Root, CorpusPath.Normalize(relativeDirectory));

        return _files
            .EnumerateFiles(directory, string.IsNullOrEmpty(searchPattern) ? "*" : searchPattern, recursive)
            .Select(file => Path.GetRelativePath(Root, file).Replace('\\', '/'));
    }
}
