using KyberWeave.Cli.Commands;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Regression pin: an unknown gate key is loaded but does nothing, so the CLI must surface it as a
/// warning. Without this, a misspelt <c>includes</c> key silently widens a gate to every change.
/// </summary>
public sealed class ReviewGateKeysCliWarningTests : IDisposable
{
    private readonly InMemoryReviewServices _services = new();

    /// <summary>Releases the services.</summary>
    public void Dispose()
    {
        _services.Dispose();
    }

    [Fact]
    public void CombinedTryLoadSurfacesAnUnknownGateKeyAsKwConfig002Warning()
    {
        string configDirectory = System.IO.Path.Combine(_services.Root, ".kyber-weave");
        _services.Files.CreateDirectory(configDirectory);
        string configPath = System.IO.Path.Combine(configDirectory, "kyber-weave.yml");
        _services.Files.WriteAllText(
            configPath,
            """
            review:
              gates:
                - id: build
                  run: [dotnet, build]
                  includes: ["**/*.cs"]

            """);

        DiagnosticReport report = new DiagnosticReport();
        bool success = CommandHelpers.TryLoadConfig(_services.Get<IKyberWeaveConfigRepository>(), _services.Root, null, report, out KyberWeaveConfig config);

        Assert.True(success);
        Diagnostic diagnostic = Assert.Single(report.Items);
        Assert.Equal(KyberWeaveConfigDiagnosticCodes.Warning, diagnostic.Code);
        Assert.Equal(Severity.Warning, diagnostic.Severity);
        Assert.Contains("includes", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("build", diagnostic.Message, StringComparison.Ordinal);
        Assert.Single(config.Review.Gates);
    }
}
