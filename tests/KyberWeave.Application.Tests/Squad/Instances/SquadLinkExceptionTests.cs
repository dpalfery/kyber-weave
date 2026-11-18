using KyberWeave.Core.Squad.Deployment;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>The exception a Squad link file system raises carries the failing link and target paths.</summary>
public sealed class SquadLinkExceptionTests
{
    [Fact]
    public void SquadLinkExceptionPreservesMessageAndPathProperties()
    {
        const string message = "Failed to create link";
        const string linkPath = "/workspace/instance/agent.md";
        const string targetPath = "/repo/agents/agent.md";

        SquadLinkException exception = new(message, linkPath, targetPath);

        Assert.Equal(message, exception.Message);
        Assert.Equal(linkPath, exception.LinkPath);
        Assert.Equal(targetPath, exception.TargetPath);
        Assert.IsAssignableFrom<Exception>(exception);
    }
}
