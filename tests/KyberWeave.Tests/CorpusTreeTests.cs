using KyberWeave.Tests.Corpus;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The scans that have to see every entry under a product directory, not only its files.
/// </summary>
/// <remarks>
/// A retired surface can leave a directory behind with no file inside it that names the surface: an empty
/// reference folder, or one whose only file is a generic <c>notes.md</c>. A files-only scan passes those, so
/// these cases pin that a directory is an entry in its own right.
/// </remarks>
public sealed class CorpusTreeTests
{
    [Fact]
    public void EntriesCarryTheDirectoriesBesideTheirFiles()
    {
        InMemoryCorpusSource corpus = new InMemoryCorpusSource()
            .Add("products/kyber-squad/agents/architect.md", "architect")
            .Add("products/kyber-squad/agents/architect/references/intake.md", "intake");

        string[] entries = [.. CorpusTree.Entries(corpus, "products/kyber-squad")];

        Assert.Contains("products/kyber-squad/agents", entries);
        Assert.Contains("products/kyber-squad/agents/architect.md", entries);
        Assert.Contains("products/kyber-squad/agents/architect/references", entries);
        Assert.Contains("products/kyber-squad/agents/architect/references/intake.md", entries);
    }

    /// <summary>
    /// The failure the retired task-reviewer leaves behind: a folder whose own name carries the retired
    /// token, holding only a file that does not. Its file name is not the offender; the folder is.
    /// </summary>
    [Fact]
    public void EntriesCarryADirectoryWhoseOnlyFileDoesNotNameIt()
    {
        InMemoryCorpusSource corpus = new InMemoryCorpusSource()
            .Add("products/kyber-squad/skills/task-reviewer/notes.md", "nothing retired here");

        string[] offenders =
        [
            .. CorpusTree.Entries(corpus, "products/kyber-squad")
                .Where(path => Path.GetFileName(path).Contains("task-reviewer", StringComparison.Ordinal))
        ];

        Assert.Equal(["products/kyber-squad/skills/task-reviewer"], offenders);
    }

    [Fact]
    public void DirectoriesExcludeTheDirectoryTheListingStartedAt()
    {
        InMemoryCorpusSource corpus = new InMemoryCorpusSource()
            .Add("products/kyber-squad/skills/code-review/SKILL.md", "review");

        string[] directories = [.. CorpusTree.Directories(corpus, "products/kyber-squad")];

        Assert.Equal(["products/kyber-squad/skills", "products/kyber-squad/skills/code-review"], directories);
    }

    /// <summary>
    /// A retired skill folder that ships under a different capitalisation than the name the test bans. On the
    /// case-insensitive file systems this repository develops and ships on, the folder exists; an ordinal
    /// comparison says it does not, and the retired skill slips through.
    /// </summary>
    [Fact]
    public void DirectoryExistsFindsAWrongCaseDirectoryOnACaseInsensitiveCorpus()
    {
        InMemoryCorpusSource corpus = new InMemoryCorpusSource(caseSensitive: false)
            .Add("products/kyber-squad/skills/Second-Brain/SKILL.md", "retired");

        Assert.True(CorpusTree.DirectoryExists(corpus, "products/kyber-squad/skills/second-brain"));
    }

    [Fact]
    public void DirectoryExistsRejectsAWrongCaseDirectoryOnACaseSensitiveCorpus()
    {
        InMemoryCorpusSource corpus = new InMemoryCorpusSource(caseSensitive: true)
            .Add("products/kyber-squad/skills/Second-Brain/SKILL.md", "a different skill");

        Assert.False(CorpusTree.DirectoryExists(corpus, "products/kyber-squad/skills/second-brain"));
        Assert.True(CorpusTree.DirectoryExists(corpus, "products/kyber-squad/skills/Second-Brain"));
    }

    [Fact]
    public void DirectoryExistsAnswersTheCheckoutAsTheHostFileSystemDoes()
    {
        Assert.True(CorpusTree.DirectoryExists(CheckoutCorpusSource.Checkout, "products/kyber-squad/agents"));
        Assert.Equal(
            !OperatingSystem.IsLinux(),
            CorpusTree.DirectoryExists(CheckoutCorpusSource.Checkout, "PRODUCTS/kyber-squad/agents"));
    }

    [Fact]
    public void DirectoryExistsIsFalseForAFile()
    {
        InMemoryCorpusSource corpus = new InMemoryCorpusSource()
            .Add("products/kyber-squad/README.md", "readme");

        Assert.False(CorpusTree.DirectoryExists(corpus, "products/kyber-squad/README.md"));
    }

    /// <summary>A trailing slash names the same directory, as it does for the file system.</summary>
    [Fact]
    public void DirectoryExistsIgnoresATrailingSlash()
    {
        InMemoryCorpusSource corpus = new InMemoryCorpusSource()
            .Add("products/kyber-squad/skills/code-review/SKILL.md", "review");

        Assert.True(CorpusTree.DirectoryExists(corpus, "products/kyber-squad/skills/code-review/"));
    }

    [Fact]
    public void EntriesOfAMissingDirectoryAreEmpty()
    {
        InMemoryCorpusSource corpus = new InMemoryCorpusSource()
            .Add("products/kyber-squad/README.md", "readme");

        Assert.Empty(CorpusTree.Entries(corpus, "products/no-such-product"));
    }
}
