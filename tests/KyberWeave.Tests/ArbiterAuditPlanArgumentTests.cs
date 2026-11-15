using KyberWeave.Cli.Commands.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using Xunit;

namespace KyberWeave.Tests;

/// <summary><c>arbiter audit --plan</c> resolves its argument through the shared plan resolver, so any natural spelling selects the events carrying that plan.</summary>
[Collection(ArbiterCommandCompositionCollection.Name)]
public sealed class ArbiterAuditPlanArgumentTests : IDisposable
{
    private const string Plan = "handbook/plans/2026-11-13-job-status-cards.md";
    private const string OtherPlan = "handbook/plans/2026-11-12-other.md";
    private const string OnPlan = "0000000000000001-aaaaaaaa";
    private const string OnPlanBySlug = "0000000000000002-aaaaaaaa";
    private const string OffPlan = "0000000000000003-aaaaaaaa";

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Theory]
    [InlineData("job-status-cards", ".")]
    [InlineData("2026-11-13-job-status-cards.md", "handbook/plans")]
    [InlineData("handbook/plans/2026-11-13-job-status-cards.md", ".")]
    [InlineData("./handbook/plans/2026-11-13-job-status-cards.md", ".")]
    public void AnySpellingOfThePlanSelectsTheEventsThatCarryItWhateverSpellingTheDispatchUsed(string typed, string workingDirectory)
    {
        string root = Host();

        CapturedConsoleExecution<int> run = ProcessConsoleCapture.Run(() => new ArbiterAuditCommand().Execute(
            null!,
            new ArbiterSettings
            {
                Path = root,
                Plan = typed,
                WorkingDirectory = Path.Combine(root, workingDirectory.Replace('/', Path.DirectorySeparatorChar))
            }));

        Assert.Contains(OnPlan, run.Output, StringComparison.Ordinal);
        Assert.Contains(OnPlanBySlug, run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(OffPlan, run.Output, StringComparison.Ordinal);
    }

    private string Host()
    {
        string root = Path.Combine(_temp.Path, "host");
        Directory.CreateDirectory(Path.Combine(root, ".kyber-weave"));
        File.WriteAllText(Path.Combine(root, ".kyber-weave", "kyber-weave.yml"), "ontology:\n  docs-root: handbook\n");
        Directory.CreateDirectory(Path.Combine(root, "handbook", "plans"));
        foreach (string name in new[] { "2026-11-13-job-status-cards.md", "2026-11-12-other.md" })
        {
            File.WriteAllText(
                Path.Combine(root, "handbook", "plans", name),
                $"---\nid: plans/{name[..^3]}\ntitle: Plan\ndoc-type: plan\nstatus: current\n---\n\n# Plan\n\n**Status: Ready.**\n");
        }

        InFlightLedger ledger = new(Path.Combine(root, "artifacts", "arbiter"));
        Append(ledger, OnPlan, Plan);
        Append(ledger, OnPlanBySlug, "job-status-cards");
        Append(ledger, OffPlan, OtherPlan);
        return root;
    }

    private static void Append(InFlightLedger ledger, string id, string plan) =>
        ledger.AppendAsync(new ArbiterLedgerEvent(id, DateTimeOffset.UtcNow, "hook", "claude", "session-1", ArbiterLedgerPhases.Pre)
        {
            Trigger = "delegate",
            Target = "csharp-dev",
            Headers = new ArbiterLedgerHeaders(PlanFile: plan)
        }).GetAwaiter().GetResult();
}
