using KyberWeave.Core.Networking;
using Xunit;

namespace KyberWeave.Tests.Networking;

/// <summary>
/// The one host check configuration validation and key resolution share, so that
/// "is this endpoint local?" cannot answer differently depending on which of the
/// two asks. A trailing dot is the case that had them disagree: <c>localhost.</c>
/// is the absolute spelling of <c>localhost</c> and resolves to the loopback
/// interface, and neither the exact spelling test nor <c>IPAddress.TryParse</c>
/// accepted it.
/// </summary>
public sealed class LoopbackAddressTests
{
    [Theory]
    [InlineData("localhost")]
    [InlineData("LocalHost")]
    [InlineData("LOCALHOST")]
    // One trailing dot is insignificant: the fully qualified form of the same name.
    [InlineData("localhost.")]
    [InlineData("LOCALHOST.")]
    [InlineData("LocalHost.")]
    // An IPv6 literal keeps its brackets on Uri.Host.
    [InlineData("[::1]")]
    [InlineData("[0:0:0:0:0:0:0:1]")]
    [InlineData("127.0.0.1")]
    [InlineData("127.1.2.3")]
    [InlineData("::1")]
    public void ALoopbackHostIsRecognized(string host)
    {
        Assert.True(LoopbackAddress.IsLoopbackHost(host));
    }

    [Theory]
    // Every spelling that merely starts with a loopback name. A prefix test reads all of
    // these as local, which sends plain HTTP to a remote host and suppresses the key there.
    [InlineData("localhost.evil.com")]
    [InlineData("localhost.evil.com.")]
    [InlineData("127.0.0.1.evil.com")]
    [InlineData("127.0.evil")]
    [InlineData("127.1.2.3.4.5")]
    // Two trailing dots is a different name, not a spelling of this one.
    [InlineData("localhost..")]
    [InlineData("localhost.localdomain")]
    [InlineData("example.com")]
    [InlineData("192.168.1.1")]
    [InlineData("10.0.0.1")]
    public void ANonLoopbackHostIsNot(string host)
    {
        Assert.False(LoopbackAddress.IsLoopbackHost(host));
    }

    [Fact]
    public void TrailingWhitespaceDoesNotDecideTheAnswer()
    {
        Assert.True(LoopbackAddress.IsLoopbackHost(" localhost. "));
    }

    [Fact]
    public void AnEmptyHostIsRejectedRatherThanAnswered()
    {
        Assert.Throws<ArgumentException>(() => LoopbackAddress.IsLoopbackHost("  "));
        Assert.Throws<ArgumentNullException>(() => LoopbackAddress.IsLoopbackHost(null!));
    }
}
