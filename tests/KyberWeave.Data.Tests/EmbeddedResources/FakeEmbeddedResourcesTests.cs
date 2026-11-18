using System.Text;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests.EmbeddedResources;

/// <summary>Proves the fake semantics a shared contract cannot state, because adding a resource is not part of the seam.</summary>
public sealed class FakeEmbeddedResourcesTests
{
    [Fact]
    public void KeepsTheBytesItWasGivenWhenTheCallerReusesThem()
    {
        byte[] content = Encoding.UTF8.GetBytes("original");
        FakeEmbeddedResources resources = new FakeEmbeddedResources().Add("KyberWeave.Testing.mutable.txt", content);

        content[0] = (byte)'X';

        Assert.Equal("original", resources.ReadAllText("KyberWeave.Testing.mutable.txt"));
    }
}
