using KyberWeave.Cli.Commands.Arbiter;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary><c>arbiter plan</c> resolves its argument through the shared plan resolver, in a host whose docs root is not <c>docs</c>.</summary>
public sealed class ArbiterPlanArgumentTests : IDisposable
{
    private const string Plan = "handbook/plans/2026-11-13-job-status-cards.md";
    private readonly InMemoryArbiterHost _host = InMemoryArbiterHost.Create();

    public void Dispose() => _host.Dispose();

    private string Host()
    {
        string root = _host.InWork("host");
        _host.Files.CreateDirectory(Path.Combine(root, ".git"));
        _ = _host.Write(Path.Combine(root, ".kyber-weave", "kyber-weave.yml"), "ontology:\n  docs-root: handbook\n");
        _ = _host.Write(
            Path.Combine(root, "handbook", "plans", "2026-11-13-job-status-cards.md"),
            "---\nid: plans/2026-11-13-job-status-cards\ntitle: Cards\ndoc-type: plan\nstatus: current\n---\n\n# Cards\n\n**Status: Ready.**\n");
        return root;
    }

    [Theory]
    [InlineData("job-status-cards", ".")]
    [InlineData("2026-11-13-job-status-cards", "handbook/plans")]
    [InlineData("2026-11-13-job-status-cards.md", "handbook/plans")]
    [InlineData("handbook/plans/2026-11-13-job-status-cards.md", "handbook/plans")]
    public void ThePlanIsFoundFromAnySpellingAndItsPathIsPrinted(string typed, string workingDirectory)
    {
        string root = Host();
        ArbiterSettings settings = new()
        {
            Path = typed,
            WorkingDirectory = Path.Combine(root, workingDirectory.Replace('/', Path.DirectorySeparatorChar))
        };

        ArbiterCommandRun run = _host.Run(() => _host.Command<ArbiterPlanCommand>().Execute(null!, settings));

        Assert.DoesNotContain("KW-ARB-PLAN-002", run.Output, StringComparison.Ordinal);
        Assert.Contains(Plan, run.Output.Replace('\\', '/'), StringComparison.Ordinal);
    }
}
