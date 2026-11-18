using System.Text;
using Xunit;

namespace KyberWeave.Tests.Corpus;

/// <summary>
/// The contract every <see cref="ICorpusSource"/> must satisfy. Run it once against the corpus held in memory and
/// once against this checkout, so a content test can trust that the fixture corpus and the shipped content read the
/// same way.
/// </summary>
/// <remarks>
/// A derived class supplies the corpus under test and one file that corpus holds, together with the directory that
/// file sits directly in. The suite never writes: the checkout is read-only, and the in-memory corpus is seeded
/// before the suite runs.
/// </remarks>
public abstract class CorpusSourceContractTests
{
    /// <summary>Creates the corpus under test.</summary>
    /// <returns>The corpus.</returns>
    protected abstract ICorpusSource CreateCorpus();

    /// <summary>Gets the path, relative to the corpus root, of a file the corpus holds directly in <see cref="SampleDirectory"/>.</summary>
    protected abstract string SampleFile { get; }

    /// <summary>Gets the path, relative to the corpus root, of the directory holding <see cref="SampleFile"/>.</summary>
    protected abstract string SampleDirectory { get; }

    /// <summary>
    /// Gets a directory and a file name pattern whose recursive listing would include a file under a build-output or
    /// dependency folder, if the corpus listed one.
    /// </summary>
    protected abstract (string Directory, string Pattern) BuildOutputProbe { get; }

    /// <summary>Gets whether this corpus compares path names case-sensitively, as its host file system does.</summary>
    protected abstract bool CaseSensitiveNames { get; }

    [Fact]
    public void MatchesNamesAsItsHostFileSystemDoes()
    {
        ICorpusSource corpus = CreateCorpus();
        string otherCase = SampleFile.ToUpperInvariant();

        Assert.Equal(!CaseSensitiveNames, corpus.Exists(otherCase));

        if (!CaseSensitiveNames)
            Assert.Equal(corpus.ReadText(SampleFile), corpus.ReadText(otherCase));
    }

    [Fact]
    public void SkipsBuildOutputAndDependencyFoldersWhenListingFiles()
    {
        ICorpusSource corpus = CreateCorpus();
        (string directory, string pattern) = BuildOutputProbe;

        string[] listed = [.. corpus.EnumerateFiles(directory, pattern)];

        Assert.DoesNotContain(listed, path => path
            .Split('/')
            .SkipLast(1)
            .Any(segment => CorpusListing.SkippedDirectoryNames.Contains(segment, StringComparer.Ordinal)));
    }

    [Fact]
    public void ReportsAnAbsoluteRoot()
    {
        ICorpusSource corpus = CreateCorpus();

        Assert.True(Path.IsPathRooted(corpus.Root), $"The corpus root '{corpus.Root}' is not an absolute path.");
    }

    [Fact]
    public void ReadsAFileAsText()
    {
        ICorpusSource corpus = CreateCorpus();

        Assert.NotEmpty(corpus.ReadText(SampleFile));
    }

    [Fact]
    public void ReadsTheSameBytesItsTextComesFrom()
    {
        ICorpusSource corpus = CreateCorpus();

        Assert.Equal(corpus.ReadText(SampleFile), Encoding.UTF8.GetString(corpus.ReadBytes(SampleFile)));
    }

    [Fact]
    public void DoesNotLetACallerChangeTheStoredBytes()
    {
        ICorpusSource corpus = CreateCorpus();
        byte[] original = [.. corpus.ReadBytes(SampleFile)];
        byte[] handle = corpus.ReadBytes(SampleFile);

        handle[0] ^= 0xFF;

        Assert.Equal(original, corpus.ReadBytes(SampleFile));
    }

    [Fact]
    public void ReportsWhetherAFileExists()
    {
        ICorpusSource corpus = CreateCorpus();

        Assert.True(corpus.Exists(SampleFile));
        Assert.False(corpus.Exists(SampleDirectory + "/no-such-file.md"));
    }

    [Fact]
    public void ReportsWhetherADirectoryExists()
    {
        ICorpusSource corpus = CreateCorpus();

        Assert.True(corpus.DirectoryExists(string.Empty), "The corpus root is itself a directory.");
        Assert.True(corpus.DirectoryExists(SampleDirectory));
        Assert.False(corpus.DirectoryExists(SampleDirectory + "/no-such-directory"));
        Assert.False(corpus.DirectoryExists(SampleFile), "A file is not a directory.");
    }

    [Fact]
    public void ThrowsWhenAFileIsMissing()
    {
        ICorpusSource corpus = CreateCorpus();
        const string Missing = "products/no-such-product/no-such-file.md";

        Assert.Throws<FileNotFoundException>(() => corpus.ReadText(Missing));
        Assert.Throws<FileNotFoundException>(() => corpus.ReadBytes(Missing));
    }

    [Fact]
    public void NamesTheRelativePathItCouldNotRead()
    {
        ICorpusSource corpus = CreateCorpus();
        const string Missing = "products/no-such-product/no-such-file.md";

        FileNotFoundException fromText = Assert.Throws<FileNotFoundException>(() => corpus.ReadText(Missing));
        FileNotFoundException fromBytes = Assert.Throws<FileNotFoundException>(() => corpus.ReadBytes(Missing));

        Assert.Equal(Missing, fromText.FileName);
        Assert.Equal(Missing, fromBytes.FileName);
    }

    [Fact]
    public void ThrowsTheSameErrorWhenThePathNamesADirectory()
    {
        ICorpusSource corpus = CreateCorpus();

        Assert.Throws<FileNotFoundException>(() => corpus.ReadText(SampleDirectory));
        Assert.Throws<FileNotFoundException>(() => corpus.ReadBytes(SampleDirectory));
    }

    [Fact]
    public void ListsTheFilesUnderADirectoryRecursively()
    {
        ICorpusSource corpus = CreateCorpus();

        Assert.Contains(SampleFile, corpus.EnumerateFiles(SampleDirectory, "*.md"), StringComparer.Ordinal);
        Assert.Contains(SampleFile, corpus.EnumerateFiles("products", "*.md"), StringComparer.Ordinal);
    }

    [Fact]
    public void ListsOnlyItsOwnFilesWhenNotRecursive()
    {
        ICorpusSource corpus = CreateCorpus();

        string[] files = [.. corpus.EnumerateFiles(SampleDirectory, "*", recursive: false)];
        int directoryDepth = SampleDirectory.Count(character => character == '/');

        Assert.Contains(SampleFile, files, StringComparer.Ordinal);
        Assert.All(files, file => Assert.Equal(directoryDepth + 1, file.Count(character => character == '/')));
    }

    [Fact]
    public void ReturnsPathsRelativeToTheRoot()
    {
        ICorpusSource corpus = CreateCorpus();

        Assert.All(corpus.EnumerateFiles("products", "*"), file =>
        {
            Assert.False(Path.IsPathRooted(file), $"'{file}' is not relative to the corpus root.");
            Assert.DoesNotContain("\\", file, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void FiltersByFileNamePattern()
    {
        ICorpusSource corpus = CreateCorpus();

        Assert.All(corpus.EnumerateFiles(SampleDirectory, "*.md"), file => Assert.EndsWith(".md", file, StringComparison.Ordinal));
        Assert.Empty(corpus.EnumerateFiles(SampleDirectory, "*.no-such-extension"));
    }

    [Fact]
    public void ListsTheChildDirectoriesUnderADirectory()
    {
        ICorpusSource corpus = CreateCorpus();

        string[] directories = [.. corpus.EnumerateDirectories("products")];

        Assert.Contains("products/kyber-squad", directories, StringComparer.Ordinal);
        Assert.All(directories, directory =>
        {
            Assert.StartsWith("products/", directory, StringComparison.Ordinal);
            Assert.DoesNotContain('/', directory["products/".Length..]);
            Assert.False(Path.IsPathRooted(directory), $"'{directory}' is not relative to the corpus root.");
        });
    }

    [Fact]
    public void ReturnsNoDirectoriesForAMissingDirectory()
    {
        ICorpusSource corpus = CreateCorpus();

        Assert.Empty(corpus.EnumerateDirectories("products/no-such-product"));
    }

    [Fact]
    public void ReturnsListingsThatAreAlreadyMaterialized()
    {
        ICorpusSource corpus = CreateCorpus();

        Assert.IsAssignableFrom<ICollection<string>>(corpus.EnumerateFiles(SampleDirectory, "*"));
        Assert.IsAssignableFrom<ICollection<string>>(corpus.EnumerateDirectories("products"));
    }

    [Fact]
    public void ReturnsNoFilesForAMissingDirectory()
    {
        ICorpusSource corpus = CreateCorpus();

        Assert.Empty(corpus.EnumerateFiles("products/no-such-product"));
    }

    [Fact]
    public void AcceptsADotSegmentAndATrailingSlash()
    {
        ICorpusSource corpus = CreateCorpus();

        Assert.Equal(corpus.ReadText(SampleFile), corpus.ReadText("./" + SampleFile));
        Assert.Contains(SampleFile, corpus.EnumerateFiles(SampleDirectory + "/", "*"), StringComparer.Ordinal);
    }

    [Fact]
    public void RefusesAMissingDirectoryName()
    {
        ICorpusSource corpus = CreateCorpus();

        Assert.ThrowsAny<ArgumentException>(() => corpus.DirectoryExists(null!));
        Assert.Throws<ArgumentException>(() => corpus.DirectoryExists(" "));
        Assert.Throws<ArgumentException>(() => corpus.EnumerateFiles(" "));
        Assert.Throws<ArgumentException>(() => corpus.EnumerateDirectories(" "));
    }

    [Fact]
    public void RefusesARootedPath()
    {
        ICorpusSource corpus = CreateCorpus();

        Assert.Throws<ArgumentException>(() => corpus.ReadText("/" + SampleFile));
        Assert.Throws<ArgumentException>(() => corpus.Exists("/" + SampleFile));
        Assert.Throws<ArgumentException>(() => corpus.DirectoryExists("/" + SampleDirectory));
        Assert.Throws<ArgumentException>(() => corpus.EnumerateFiles("/" + SampleDirectory));
        Assert.Throws<ArgumentException>(() => corpus.ReadText("C:/" + SampleFile));
        Assert.Throws<ArgumentException>(() => corpus.DirectoryExists("C:/" + SampleDirectory));
        Assert.Throws<ArgumentException>(() => corpus.ReadText(@"C:\" + SampleFile.Replace('/', '\\')));
    }

    [Fact]
    public void RefusesAPathThatClimbsAboveTheRoot()
    {
        ICorpusSource corpus = CreateCorpus();

        Assert.Throws<ArgumentException>(() => corpus.ReadText("../outside.md"));
        Assert.Throws<ArgumentException>(() => corpus.ReadText("products/../../outside.md"));
        Assert.Throws<ArgumentException>(() => corpus.DirectoryExists("../outside"));
        Assert.Throws<ArgumentException>(() => corpus.EnumerateFiles(".."));
        Assert.Throws<ArgumentException>(() => corpus.EnumerateDirectories(".."));
    }
}
