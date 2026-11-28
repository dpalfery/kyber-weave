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
    public async Task TheCommandPrintsThePromptTheServiceBuilds()
    {
        using DocFixture fixture = await DocsTaskPromptFixture.ReadyAsync();
        KyberWeaveConfig config = DocsTaskPromptFixture.Config(fixture);

        string printed = (await DocsTaskPromptFixture.PacketAsync(fixture, "T1")).TrimEnd('\r', '\n');
        TaskPromptResult built = await DocsTaskPromptFixture.Service(fixture).BuildAsync(
            new TaskPromptRequest(fixture.Root, TaskId: "T1"), config, CancellationToken.None);

        Assert.True(built.Built, built.Refusal);

        // The packet prints the service's prompt unchanged, then the task's test command after a blank line.
        Assert.StartsWith(built.Prompt + "\n\n## Test command\n", printed, StringComparison.Ordinal);
    }
}
