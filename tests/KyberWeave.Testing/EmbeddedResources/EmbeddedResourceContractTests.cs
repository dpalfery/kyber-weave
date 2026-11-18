using KyberWeave.Contracts.Host;
using Xunit;

namespace KyberWeave.Tests.EmbeddedResources;

/// <summary>
/// The contract every <see cref="IEmbeddedResources"/> must satisfy. Run it once against the fake and once against the
/// real adapter, so a unit test can trust what the fake reports.
/// </summary>
/// <remarks>
/// A derived class names one resource that exists, the text it must return, and one name that does not exist. The
/// adapter's expected text is read from the shipped source file the resource is compiled from.
/// </remarks>
public abstract class EmbeddedResourceContractTests
{
    /// <summary>Gets a manifest resource name that exists.</summary>
    protected abstract string KnownResource { get; }

    /// <summary>Gets the exact text <see cref="KnownResource"/> must return.</summary>
    protected abstract string KnownResourceText { get; }

    /// <summary>Gets a manifest resource name that does not exist.</summary>
    protected abstract string MissingResource { get; }

    /// <summary>Creates the seam under test.</summary>
    /// <returns>The embedded-resource seam.</returns>
    protected abstract IEmbeddedResources CreateResources();

    [Fact]
    public void ReadsAKnownResourceAsText()
    {
        Assert.Equal(KnownResourceText, CreateResources().ReadAllText(KnownResource));
    }

    [Fact]
    public void ReadsAKnownResourceAsAStream()
    {
        using Stream stream = CreateResources().OpenRead(KnownResource);

        Assert.True(stream.CanRead);
        using StreamReader reader = new(stream);
        Assert.Equal(KnownResourceText, reader.ReadToEnd());
    }

    [Fact]
    public void ReturnsAFreshStreamFromTheStartEachTime()
    {
        IEmbeddedResources resources = CreateResources();

        using Stream first = resources.OpenRead(KnownResource);
        using StreamReader firstReader = new(first);
        _ = firstReader.ReadToEnd();

        using Stream second = resources.OpenRead(KnownResource);
        using StreamReader secondReader = new(second);
        Assert.Equal(KnownResourceText, secondReader.ReadToEnd());
    }

    [Fact]
    public void RaisesTheDocumentedExceptionForAResourceThatIsNotThere()
    {
        MissingEmbeddedResourceException exception = Assert.Throws<MissingEmbeddedResourceException>(
            () => CreateResources().ReadAllText(MissingResource));

        Assert.Equal(MissingResource, exception.ResourceName);
        Assert.Contains(MissingResource, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RaisesTheDocumentedExceptionWhenOpeningAResourceThatIsNotThere()
    {
        _ = Assert.Throws<MissingEmbeddedResourceException>(() => CreateResources().OpenRead(MissingResource));
    }

    [Fact]
    public void RejectsAResourceNameThatIsNotGiven()
    {
        IEmbeddedResources resources = CreateResources();

        _ = Assert.ThrowsAny<ArgumentException>(() => resources.OpenRead(null!));
        _ = Assert.ThrowsAny<ArgumentException>(() => resources.OpenRead(" "));
        _ = Assert.ThrowsAny<ArgumentException>(() => resources.ReadAllText(null!));
        _ = Assert.ThrowsAny<ArgumentException>(() => resources.ReadAllText(string.Empty));
    }
}
