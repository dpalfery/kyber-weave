using KyberDash.Mcp;
using Xunit;

namespace KyberDash.Presentation.Tests;

/// <summary>
/// Holds the forwarding budget above the time the engine needs for one analytics operation. A real
/// store of a few hundred thousand records answers in roughly ten seconds, so a shorter client bound
/// reports a timeout for an answer that is still being produced.
/// </summary>
public class McpForwardingBudgetTests
{
    [Fact]
    public void TheForwardingBudgetOutlastsASlowStoreRead()
    {
        Assert.True(
            McpStdioHost.RequestTimeout >= TimeSpan.FromSeconds(60),
            $"The forwarding budget is {McpStdioHost.RequestTimeout}, which is shorter than a large store needs.");
    }
}
