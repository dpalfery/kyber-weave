using KyberWeave.Application.Arbiter;
using KyberWeave.Cli.Commands.Arbiter;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// <c>arbiter audit</c> decides whether a model is configured from the repository's provider and the user's override.
/// A corrupt override is reported and ignored; a readable one is not reported.
/// </summary>
public sealed class ArbiterAuditModelConfiguredTests : IDisposable
{
    private readonly InMemoryArbiterHost _host = InMemoryArbiterHost.Create();

    public void Dispose() => _host.Dispose();

    [Fact]
    public void ACorruptUserSettingsFile_IsReportedAndTheRepositoryProviderDecides()
    {
        WriteUserSettings("provider: [unclosed\n");
        DiagnosticReport report = new();
        int reported = 0;

        bool configured = _host.Command<ArbiterAuditService>().ModelConfigured(KyberWeaveConfigDefaults.Product, report, ref reported);

        Assert.False(configured);
        Assert.Equal(1, reported);
        Diagnostic warning = Assert.Single(report.Items);
        Assert.Equal(Severity.Warning, warning.Severity);
        Assert.Equal(ArbiterAuditCommand.NoModelEvaluations, warning.Code);
        Assert.Contains("arbiter.yml", warning.Message, StringComparison.Ordinal);
        Assert.Contains("YamlException", warning.Message, StringComparison.Ordinal);
        Assert.Contains("provider override was ignored", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AReadableUserSettingsFile_IsNotReported()
    {
        WriteUserSettings("provider:\n  kind: systemone\n");
        DiagnosticReport report = new();
        int reported = 0;

        bool configured = _host.Command<ArbiterAuditService>().ModelConfigured(KyberWeaveConfigDefaults.Product, report, ref reported);

        Assert.True(configured);
        Assert.Equal(0, reported);
        Assert.Empty(report.Items);
    }

    private void WriteUserSettings(string yaml)
    {
        _ = _host.Write(ArbiterUserSettings.GetPath(_host.HomeDirectory), yaml);
    }
}
