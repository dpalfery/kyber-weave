using KyberWeave.Cli.Commands;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Tests.Configuration;
using Xunit;

namespace KyberWeave.Cli.Tests.Commands;

/// <summary>An invalid host configuration reaches the operator as a <c>KW-CONFIG-001</c> diagnostic.</summary>
public class ConfigLoadDiagnosticTests
{
    private readonly InMemoryConfigFileReader _config = new();

    [Fact]
    public void CombinedConfigTryLoadSurfacesInvalidYamlAsKWCONFIG001Payload()
    {
        _ = _config.WriteRepositoryConfig("ontology: [unclosed");
        string root = _config.Root;

        KyberWeaveConfigLoadResult result = _config.Repository.TryLoad(root);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.NotNull(result.ConfigPath);
        Assert.Null(result.Config);

        // A failed result names no rule code; the code belongs to the diagnostic the CLI's load helper emits, so
        // that is what proves the KW-CONFIG-001 payload reaches the operator.
        DiagnosticReport report = new();
        bool loaded = CommandHelpers.TryLoadConfig(
            _config.Repository, root, null, report, out KyberWeaveConfig config);

        Assert.False(loaded);
        Assert.Same(KyberWeaveConfigDefaults.Product, config);
        Diagnostic diagnostic = Assert.Single(report.Items);
        Assert.Equal("KW-CONFIG-001", diagnostic.Code);
        Assert.Equal(Severity.Error, diagnostic.Severity);
        Assert.Equal(result.Error, diagnostic.Message);
        Assert.Equal(result.ConfigPath, diagnostic.FilePath);
    }
}
