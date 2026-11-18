using KyberWeave.Tests.Corpus;
using Xunit;

namespace KyberWeave.Tests.RepoPolicy;

/// <summary>Runs the corpus seam's contract suite against the checkout every content test reads.</summary>
/// <remarks>
/// The suite lives in the shared test library and is run once here against the real checkout and twice in the
/// in-memory runs against the corpus held in memory, once per case rule. All of them use the same two paths, which
/// this repository ships, so a fixture corpus and the shipped content answer a test the same way.
/// </remarks>
public sealed class CheckoutCorpusSourceContractTests : CorpusSourceContractTests
{
    /// <inheritdoc />
    protected override ICorpusSource CreateCorpus() => CheckoutCorpusSource.Checkout;

    /// <inheritdoc />
    protected override string SampleFile => "products/kyber-squad/agents/conductor.md";

    /// <inheritdoc />
    protected override string SampleDirectory => "products/kyber-squad/agents";

    /// <inheritdoc />
    protected override (string Directory, string Pattern) BuildOutputProbe => ("tests/KyberWeave.Testing", "*.cs");

    /// <inheritdoc />
    protected override bool CaseSensitiveNames => OperatingSystem.IsLinux();
}

/// <summary>Runs the corpus seam's contract suite against the corpus held in memory, once per case rule.</summary>
/// <remarks>
/// It sits beside the checkout run while the content tests still live in this project: task 5.4 of the
/// interface-driven unit tests specification moves both into the content test project.
/// </remarks>
public abstract class InMemoryCorpusContractTests : CorpusSourceContractTests
{
    /// <inheritdoc />
    protected override ICorpusSource CreateCorpus() => new InMemoryCorpusSource(caseSensitive: CaseSensitiveNames)
        .Add("products/kyber-squad/agents/conductor.md", "---\nschema: kyber-squad.agent/v1\nname: conductor\n---\n")
        .Add("products/kyber-squad/agents/conductor/references/spec-path.md", "# Spec path\n")
        .Add("products/kyber-squad/agents/conductor/notes.txt", "not part of the md pattern\n")
        .Add("products/kyber-squad/node_modules/leftover/x.md", "a dependency folder the listing must skip\n");

    /// <inheritdoc />
    protected override string SampleFile => "products/kyber-squad/agents/conductor.md";

    /// <inheritdoc />
    protected override string SampleDirectory => "products/kyber-squad/agents";

    /// <inheritdoc />
    protected override (string Directory, string Pattern) BuildOutputProbe => ("products/kyber-squad", "*");

    /// <summary>
    /// An empty directory the fixture declares answers as the checkout answers one it ships, while its file listing
    /// stays empty because it holds no files.
    /// </summary>
    [Fact]
    public void ReportsAnEmptyDirectoryTheFixtureDeclares()
    {
        ICorpusSource corpus = new InMemoryCorpusSource()
            .AddDirectory("products/kyber-squad/templates/empty");

        Assert.True(corpus.DirectoryExists("products/kyber-squad/templates/empty"));
        Assert.True(corpus.DirectoryExists("products/kyber-squad/templates"));
        Assert.Contains(
            "products/kyber-squad/templates/empty",
            corpus.EnumerateDirectories("products/kyber-squad/templates"));
        Assert.Empty(corpus.EnumerateFiles("products/kyber-squad/templates/empty"));
    }

    [Fact]
    public void DoesNotKeepTheArrayTheFixtureSupplied()
    {
        byte[] supplied = System.Text.Encoding.UTF8.GetBytes("the original text");
        ICorpusSource corpus = new InMemoryCorpusSource().AddBytes("products/sample.md", supplied);

        supplied[0] = (byte)'X';

        Assert.Equal("the original text", corpus.ReadText("products/sample.md"));
    }
}

/// <summary>Runs the in-memory contract with APFS and NTFS name rules.</summary>
public sealed class InMemoryCorpusSourceContractTests : InMemoryCorpusContractTests
{
    /// <inheritdoc />
    protected override bool CaseSensitiveNames => false;
}

/// <summary>Runs the in-memory contract with Linux name rules.</summary>
public sealed class InMemoryCorpusSourceCaseSensitiveContractTests : InMemoryCorpusContractTests
{
    /// <inheritdoc />
    protected override bool CaseSensitiveNames => true;
}
