using KyberWeave.Contracts.Host;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests.EmbeddedResources;

/// <summary>Runs the embedded-resource contract against the fake, so unit tests can trust the text it returns.</summary>
public sealed class FakeEmbeddedResourcesContractTests : EmbeddedResourceContractTests
{
    private const string Resource = "KyberWeave.Testing.fixture.txt";
    private const string Text = "fixture text, read without a real assembly";

    private readonly FakeEmbeddedResources _resources = new FakeEmbeddedResources().Add(Resource, Text);

    /// <inheritdoc />
    protected override string KnownResource => Resource;

    /// <inheritdoc />
    protected override string KnownResourceText => Text;

    /// <inheritdoc />
    protected override string MissingResource => "KyberWeave.Testing.fixture-missing.txt";

    /// <inheritdoc />
    protected override IEmbeddedResources CreateResources() => _resources;
}
