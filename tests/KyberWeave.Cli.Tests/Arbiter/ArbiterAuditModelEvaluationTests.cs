using KyberWeave.Cli.Commands.Arbiter;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Diagnostics;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// <c>arbiter audit</c> reports how often the decision model was asked, and flags a run whose writer dispatches never
/// reached it while a model is configured (ADR 0082). With provider <c>none</c> it stays silent.
/// </summary>
public sealed class ArbiterAuditModelEvaluationTests : IDisposable
{
    private const string LedgerId = "0000000000000001-aaaaaaaa";

    private readonly InMemoryArbiterHost _host = InMemoryArbiterHost.Create();

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task AConfiguredModelThatWasNeverAsked_IsReported()
    {
        string output = await AuditAsync(provider: "systemone", step: 0);

        Assert.Contains(ArbiterAuditCommand.NoModelEvaluations, output, StringComparison.Ordinal);
        Assert.Contains("0 decision-model evaluation(s) over 1 dispatch(es)", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AModelThatWasAsked_IsCountedAndNotReported()
    {
        string output = await AuditAsync(provider: "systemone", step: 1);

        Assert.DoesNotContain(ArbiterAuditCommand.NoModelEvaluations, output, StringComparison.Ordinal);
        Assert.Contains("1 decision-model evaluation(s) over 1 dispatch(es)", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithProviderNone_TheGuardStaysSilent()
    {
        string output = await AuditAsync(provider: "none", step: 0);

        Assert.DoesNotContain(ArbiterAuditCommand.NoModelEvaluations, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ModelTargetingAnswersAlone_DoNotHideAJudgementGap()
    {
        string output = await AuditAsync(provider: "systemone", step: 1, trigger: "delegate.model");

        Assert.Contains(ArbiterAuditCommand.NoModelEvaluations, output, StringComparison.Ordinal);
    }

    private async Task<string> AuditAsync(string provider, int step, string trigger = "delegate")
    {
        string root = _host.InWork("host");
        _ = _host.Write(
            Path.Combine(root, ".kyber-weave", "kyber-weave.yml"),
            $"ontology:\n  docs-root: docs\narbiter:\n  provider:\n    kind: {provider}\n");
        string arbiter = Path.Combine(root, "artifacts", "arbiter");
        await _host.State.Ledger(arbiter).AppendAsync(
            new ArbiterLedgerEvent(LedgerId, DateTimeOffset.UtcNow, "hook", "copilot-cli", "s-1", ArbiterLedgerPhases.Pre)
            {
                Trigger = "delegate",
                Target = "dev-medium",
            });
        string answer = step == 1 ? "within-task" : "no-task";
        await _host.State.Log.AppendLineAsync(
            arbiter,
            "decisions.jsonl",
            "{\"id\":\"0000000000000009-bbbbbbbb\",\"at\":\"2026-11-14T00:00:00Z\",\"source\":\"hook\",\"harness\":\"copilot-cli\"," +
            $"\"ledger-id\":\"{LedgerId}\",\"trigger\":\"{trigger}\",\"caller-source\":\"none\",\"target\":\"dev-medium\"," +
            "\"rule-set\":\"x\",\"rules\":[{\"id\":\"KW-ARB-SCOPE-002\",\"step\":" + step + ",\"answer\":\"" + answer +
            "\",\"effect\":\"allow\",\"facts\":[]}],\"provider\":{\"kind\":\"systemone\",\"latency-ms\":1,\"status\":\"answered\"}," +
            "\"outcome\":\"allow\",\"repeat\":1,\"duration-ms\":1,\"schema\":\"kyber-arbiter.decision/v1\"}",
            CancellationToken.None);

        ArbiterCommandRun run = await _host.RunAsync(token => _host.Command<ArbiterAuditCommand>().ExecuteAsync(
            null!, new ArbiterSettings { Path = root }, token));
        return run.Output;
    }
}
