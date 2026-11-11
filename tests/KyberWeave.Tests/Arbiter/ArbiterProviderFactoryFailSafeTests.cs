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
[Collection(ArbiterCommandCompositionCollection.Name)]
public sealed class ArbiterProviderFactoryFailSafeTests : IDisposable
{
    private readonly TempDirectory _repo = new();
    private readonly TempDirectory _home = new();

    public void Dispose()
    {
        _repo.Dispose();
        _home.Dispose();
    }

    /// <summary>The tools over an enabled Arbiter, so the provider factory is reached rather than skipped.</summary>
    private ArbiterTools Tools(Func<KyberWeaveConfig, IArbiterProvider> factory)
    {
        ArbiterHookDecisionEngine engine = new(
            providerFactory: factory,
            homeDirectory: () => _home.Path);
        KyberWeaveConfig configured = new()
        {
            Arbiter = ArbiterConfig.ProductDefaults with { Enabled = true },
        };
        return new ArbiterTools(new ArbiterServeContext(
            _repo.Path,
            engine,
            TextWriter.Null,
            _ => configured,
            _ => true));
    }

    private void WriteCitedFile()
    {
        string path = Path.Combine(_repo.Path, "docs", "guide.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "# Guide\nIntro.\n## Install\nSteps.\n");
    }

    /// <summary>A factory that fails with a type outside the transport and decoding set is still a fail-safe answer.</summary>
    [Fact]
    public void AProviderFactoryFailureOutsideTheNamedTypesIsAFailSafeAnswer()
    {
        WriteCitedFile();
        ArbiterTools tools = Tools(_ => throw new NotSupportedException("the configured provider kind is not built here"));

        string json = tools.ClassifyBotReviewComment("docs/guide.md", 2, "Shorten this heading.", null);

        using JsonDocument document = JsonDocument.Parse(json);
        Assert.False(document.RootElement.TryGetProperty("error", out _), $"expected a class answer, got: {json}");
        Assert.Equal("real", document.RootElement.GetProperty("class").GetString());
    }

    /// <summary>A cancellation raised while building the provider propagates; it is never absorbed into an answer.</summary>
    [Fact]
    public void ACancellationWhileBuildingTheProviderPropagates()
    {
        WriteCitedFile();
        ArbiterTools tools = Tools(_ => throw new OperationCanceledException("cancelled while building the provider"));

        Assert.Throws<OperationCanceledException>(() =>
            tools.ClassifyBotReviewComment("docs/guide.md", 2, "Shorten this heading.", null));
    }
}
