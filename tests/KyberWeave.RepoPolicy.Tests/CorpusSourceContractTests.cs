using KyberWeave.Tests.Corpus;

namespace KyberWeave.Tests.RepoPolicy;

/// <summary>Runs the corpus seam's contract suite against the checkout every content test reads.</summary>
/// <remarks>
/// The suite lives in the shared test library and is run once here against the real checkout and once in
/// <see cref="InMemoryCorpusSourceContractTests"/> against the corpus held in memory. Both use the same two paths,
/// which this repository ships, so a fixture corpus and the shipped content answer a test the same way.
/// </remarks>
public sealed class CheckoutCorpusSourceContractTests : CorpusSourceContractTests
{
    /// <inheritdoc />
    protected override ICorpusSource CreateCorpus() => CheckoutCorpusSource.Checkout;

    /// <inheritdoc />
    protected override string SampleFile => "products/kyber-squad/agents/conductor.md";

    /// <inheritdoc />
    protected override string SampleDirectory => "products/kyber-squad/agents";
}

/// <summary>Runs the corpus seam's contract suite against the corpus held in memory.</summary>
/// <remarks>
/// It sits beside the checkout run while the content tests still live in this project: task 5.4 of the
/// interface-driven unit tests specification moves both into the content test project.
/// </remarks>
public sealed class InMemoryCorpusSourceContractTests : CorpusSourceContractTests
{
    /// <inheritdoc />
    protected override ICorpusSource CreateCorpus() => new InMemoryCorpusSource()
        .Add("products/kyber-squad/agents/conductor.md", "---\nschema: kyber-squad.agent/v1\nname: conductor\n---\n")
        .Add("products/kyber-squad/agents/conductor/references/spec-path.md", "# Spec path\n")
        .Add("products/kyber-squad/agents/conductor/notes.txt", "not part of the md pattern\n");

    /// <inheritdoc />
    protected override string SampleFile => "products/kyber-squad/agents/conductor.md";

    /// <inheritdoc />
    protected override string SampleDirectory => "products/kyber-squad/agents";
}
