using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Validation;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// <c>docs packet</c> prints the same prompt the task-prompt service builds, so the conductor's tool and the
/// worker's command never disagree.
/// </summary>
public sealed class DocsPacketPromptTests
{
    [Fact]
    public void TheCommandPrintsThePromptTheServiceBuilds()
    {
        using DocFixture fixture = DocsTaskPromptFixture.Ready();
        KyberWeaveConfig config = DocsTaskPromptFixture.Config(fixture);

        string printed = DocsTaskPromptFixture.Packet(fixture, "T1").TrimEnd('\r', '\n');
        TaskPromptResult built = DocsTaskPromptFixture.Service(fixture).Build(new TaskPromptRequest(fixture.Root, TaskId: "T1"), config);

        Assert.True(built.Built, built.Refusal);

        // The packet prints the service's prompt unchanged, then the task's test command after a blank line.
        Assert.StartsWith(built.Prompt + "\n\n## Test command\n", printed, StringComparison.Ordinal);
    }
}
