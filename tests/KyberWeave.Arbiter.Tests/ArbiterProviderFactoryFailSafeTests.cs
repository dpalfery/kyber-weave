using System.Text.Json;
using KyberWeave.Arbiter;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Arbiter.Mcp;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// A provider the served comment tool cannot build answers fail-safe, whatever the exception type. The factory that
/// builds it is a seam with no enumerable failure types, so any failure except a cancellation means no model call and
/// a <c>real</c> answer, never an exception out of the tool.
/// </summary>
public sealed class ArbiterProviderFactoryFailSafeTests : IDisposable
{
    private readonly InMemoryArbiterHost _host = InMemoryArbiterHost.Create();

    public void Dispose() => _host.Dispose();

    /// <summary>The tools over an enabled Arbiter, so the provider factory is reached rather than skipped.</summary>
    private ArbiterTools Tools(Func<KyberWeaveConfig, IArbiterProvider> factory)
    {
        ArbiterHookDecisionEngine engine = _host.Engine(providerFactory: factory);
        KyberWeaveConfig configured = new()
        {
            Arbiter = ArbiterConfigDefaults.Product with { Enabled = true },
        };
        return _host.Tools(engine, loadConfig: _ => configured);
    }

    private void WriteCitedFile()
    {
        _ = _host.Write(Path.Combine(_host.RepoRoot, "docs", "guide.md"), "# Guide\nIntro.\n## Install\nSteps.\n");
    }

    /// <summary>A factory that fails with a type outside the transport and decoding set is still a fail-safe answer.</summary>
    [Fact]
    public async Task AProviderFactoryFailureOutsideTheNamedTypesIsAFailSafeAnswer()
    {
        WriteCitedFile();
        ArbiterTools tools = Tools(_ => throw new NotSupportedException("the configured provider kind is not built here"));

        string json = await tools.ClassifyBotReviewCommentAsync("docs/guide.md", 2, "Shorten this heading.", null);

        using JsonDocument document = JsonDocument.Parse(json);
        Assert.False(document.RootElement.TryGetProperty("error", out _), $"expected a class answer, got: {json}");
        Assert.Equal("real", document.RootElement.GetProperty("class").GetString());
    }

    /// <summary>A cancellation raised while building the provider propagates; it is never absorbed into an answer.</summary>
    [Fact]
    public async Task ACancellationWhileBuildingTheProviderPropagates()
    {
        WriteCitedFile();
        ArbiterTools tools = Tools(_ => throw new OperationCanceledException("cancelled while building the provider"));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            tools.ClassifyBotReviewCommentAsync("docs/guide.md", 2, "Shorten this heading.", null));
    }
}
