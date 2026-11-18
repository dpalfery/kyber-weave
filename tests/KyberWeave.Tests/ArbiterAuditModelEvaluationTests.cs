using KyberWeave.Contracts.Repositories;
using KyberWeave.Tests.Arbiter;
using KyberWeave.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using KyberWeave.Application.Arbiter;
using KyberWeave.Cli.Commands.Arbiter;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// <c>arbiter audit</c> reports how often the decision model was asked, and flags a run whose writer dispatches never
/// reached it while a model is configured (ADR 0082). With provider <c>none</c> it stays silent.
/// </summary>
public sealed class ArbiterAuditModelEvaluationTests : IDisposable
{
    private const string LedgerId = "0000000000000001-aaaaaaaa";

    private readonly TempDirectory _temp = new();
    private readonly ArbiterTestHost _host;

    public ArbiterAuditModelEvaluationTests() =>
        _host = ArbiterTestHost.Create(services =>
            services.AddSingleton<IProcessEnvironment>(new FakeProcessEnvironment(() => Path.Combine(_temp.Path, "home"))));

    public void Dispose()
    {
        _host.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public void AConfiguredModelThatWasNeverAsked_IsReported()
    {
        string output = Audit(provider: "systemone", step: 0);

        Assert.Contains(ArbiterAuditCommand.NoModelEvaluations, output, StringComparison.Ordinal);
        Assert.Contains("0 decision-model evaluation(s) over 1 dispatch(es)", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AModelThatWasAsked_IsCountedAndNotReported()
    {
        string output = Audit(provider: "systemone", step: 1);

        Assert.DoesNotContain(ArbiterAuditCommand.NoModelEvaluations, output, StringComparison.Ordinal);
        Assert.Contains("1 decision-model evaluation(s) over 1 dispatch(es)", output, StringComparison.Ordinal);
    }

    [Fact]
    public void WithProviderNone_TheGuardStaysSilent()
    {
        string output = Audit(provider: "none", step: 0);

        Assert.DoesNotContain(ArbiterAuditCommand.NoModelEvaluations, output, StringComparison.Ordinal);
    }

    [Fact]
    public void ModelTargetingAnswersAlone_DoNotHideAJudgementGap()
    {
        string output = Audit(provider: "systemone", step: 1, trigger: "delegate.model");

        Assert.Contains(ArbiterAuditCommand.NoModelEvaluations, output, StringComparison.Ordinal);
    }

    [Fact]
    public void ACorruptUserSettingsFile_IsReportedAndTheRepositoryProviderDecides()
    {
        WriteUserSettings("provider: [unclosed\n");
        DiagnosticReport report = new();
        int reported = 0;

        bool configured = _host.Command<ArbiterAuditService>().ModelConfigured(KyberWeaveConfig.ProductDefaults, report, ref reported);

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

        bool configured = _host.Command<ArbiterAuditService>().ModelConfigured(KyberWeaveConfig.ProductDefaults, report, ref reported);

        Assert.True(configured);
        Assert.Equal(0, reported);
        Assert.Empty(report.Items);
    }

    private void WriteUserSettings(string yaml)
    {
        string settingsPath = ArbiterUserSettings.GetPath(Path.Combine(_temp.Path, "home"));
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath, yaml);
    }

    private string Audit(string provider, int step, string trigger = "delegate")
    {
        string root = Path.Combine(_temp.Path, "host");
        Directory.CreateDirectory(Path.Combine(root, ".kyber-weave"));
        File.WriteAllText(
            Path.Combine(root, ".kyber-weave", "kyber-weave.yml"),
            $"ontology:\n  docs-root: docs\narbiter:\n  provider:\n    kind: {provider}\n");
        string arbiter = Path.Combine(root, "artifacts", "arbiter");
        new InFlightLedger(arbiter).AppendAsync(
            new ArbiterLedgerEvent(LedgerId, DateTimeOffset.UtcNow, "hook", "copilot-cli", "s-1", ArbiterLedgerPhases.Pre)
            {
                Trigger = "delegate",
                Target = "dev-medium",
            }).GetAwaiter().GetResult();
        string answer = step == 1 ? "within-task" : "no-task";
        File.WriteAllText(
            Path.Combine(arbiter, "decisions.jsonl"),
            "{\"id\":\"0000000000000009-bbbbbbbb\",\"at\":\"2026-11-14T00:00:00Z\",\"source\":\"hook\",\"harness\":\"copilot-cli\"," +
            $"\"ledger-id\":\"{LedgerId}\",\"trigger\":\"{trigger}\",\"caller-source\":\"none\",\"target\":\"dev-medium\"," +
            "\"rule-set\":\"x\",\"rules\":[{\"id\":\"KW-ARB-SCOPE-002\",\"step\":" + step + ",\"answer\":\"" + answer +
            "\",\"effect\":\"allow\",\"facts\":[]}],\"provider\":{\"kind\":\"systemone\",\"latency-ms\":1,\"status\":\"answered\"}," +
            "\"outcome\":\"allow\",\"repeat\":1,\"duration-ms\":1,\"schema\":\"kyber-arbiter.decision/v1\"}\n");

        CapturedConsoleExecution<int> run = ProcessConsoleCapture.Run(() => _host.Command<ArbiterAuditCommand>().Execute(
            null!, new ArbiterSettings { Path = root }));
        return run.Output;
    }
}
