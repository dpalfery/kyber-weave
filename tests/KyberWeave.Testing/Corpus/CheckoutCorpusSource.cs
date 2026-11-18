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
    public string ReadText(string relativePath) => _files.ReadAllText(ResolveFile(relativePath));

    /// <inheritdoc />
    public byte[] ReadBytes(string relativePath) => _files.ReadAllBytes(ResolveFile(relativePath));

    /// <summary>Resolves a readable file, raising the seam's one "no such file" error for anything else.</summary>
    /// <param name="relativePath">The file's path relative to <see cref="Root"/>.</param>
    /// <returns>The absolute path.</returns>
    /// <exception cref="FileNotFoundException">The corpus holds no file at the path, or the path names a directory.</exception>
    private string ResolveFile(string relativePath)
    {
        string canonical = CorpusPath.Normalize(relativePath);
        string path = CorpusPath.Resolve(Root, canonical);

        if (!_files.FileExists(path))
            throw new FileNotFoundException($"The corpus holds no file at '{relativePath}'.", canonical);

        return path;
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
            .Select(file => Path.GetRelativePath(Root, file).Replace('\\', '/'))
            .ToList();
    }

    /// <inheritdoc />
    public IEnumerable<string> EnumerateDirectories(string relativeDirectory)
    {
        string directory = CorpusPath.Resolve(Root, CorpusPath.Normalize(relativeDirectory));

        return _files
            .EnumerateDirectories(directory)
            .Select(child => Path.GetRelativePath(Root, child).Replace('\\', '/'))
            .ToList();
    }
}
