using KyberWeave.Cli.Commands;
using KyberWeave.Cli.Commands.Agents;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Agents.Model;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Tests.Fakes;
using Spectre.Console.Testing;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The agent commands write to the console they were constructed with, so their output is read here without a
/// terminal and without the process-wide console.
/// </summary>
public sealed class AgentCommandsConsoleTests : IDisposable
{
    private const string Root = "/kw/agents";

    private readonly TestConsole _console = new() { EmitAnsiSequences = false };
    private readonly StubAgentGovernance _agents = new();

    public AgentCommandsConsoleTests() => _console.Profile.Width = 200;

    public void Dispose() => _console.Dispose();

    [Fact]
    public void Validate_WithAnError_WritesTheFindingToTheConsoleAndReturnsOne()
    {
        _agents.Report.Add(new Diagnostic("KW-AGENT-001", Severity.Error, "bad agent", "agent"));
        AgentValidateCommand command = new(_agents, _console);

        int exit = command.Execute(null!, new AgentCommandSettings { Path = Root, Format = "lines" }, CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Contains("KW-AGENT-001", _console.Output, StringComparison.Ordinal);
        Assert.Equal((Root, (string?)null), _agents.Validated);
    }

    [Fact]
    public void Validate_FilterRefused_ReturnsOneEvenWithoutFindings()
    {
        _agents.FilterRefused = true;
        AgentValidateCommand command = new(_agents, _console);

        int exit = command.Execute(null!, new AgentCommandSettings { Path = Root, Format = "json" }, CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.NotEmpty(_console.Output);
    }

    [Fact]
    public void Scan_WithAWarning_WritesTheFindingAndTheSummaryToTheConsole()
    {
        _agents.Report.Add(new Diagnostic("KW-AGENT-002", Severity.Warning, "suspicious agent", "agent"));
        AgentScanCommand command = new(_agents, _console);

        int exit = command.Execute(null!, new AgentScanSettings { Path = Root, Format = "lines" }, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("KW-AGENT-002", _console.Output, StringComparison.Ordinal);
        Assert.Contains("1 warning", _console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void SyncCheck_WithAnError_WritesItToTheConsoleAndReturnsOne()
    {
        _agents.SyncReport.Add(new Diagnostic("KW-AGENT-003", Severity.Error, "role missing", "agent"));
        AgentSyncCheckCommand command = new(_agents, new FakeKyberWeaveConfigRepository(), _console);

        int exit = command.Execute(null!, new AnalysisSettings { Path = Root, Format = "lines" }, CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Contains("KW-AGENT-003", _console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Catalog_ListsEachRoleInATableOnTheConsole()
    {
        _agents.Roles["reviewer"] = new Dictionary<HarnessKind, AgentModel>();
        AgentCatalogCommand command = new(_agents, new FakeKyberWeaveConfigRepository(), _console);

        int exit = command.Execute(null!, new AgentCatalogSettings { Path = Root }, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("Agent Harness Parity Matrix", _console.Output, StringComparison.Ordinal);
        Assert.Contains("reviewer", _console.Output, StringComparison.Ordinal);
        Assert.Contains("Total agent roles: 1", _console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Catalog_AConfigurationThatDoesNotLoad_WritesItsFindingToTheConsoleAndReturnsOne()
    {
        AgentCatalogCommand command = new(_agents, new FakeKyberWeaveConfigRepository(), _console);

        int exit = command.Execute(
            null!,
            new AgentCatalogSettings { Path = Root, Config = "/kw/agents/missing.yml" },
            CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Contains("KW-", _console.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Total agent roles", _console.Output, StringComparison.Ordinal);
    }

    private sealed class StubAgentGovernance : IAgentGovernanceService
    {
        public DiagnosticReport Report { get; } = new();

        public DiagnosticReport SyncReport { get; } = new();

        public bool FilterRefused { get; set; }

        public Dictionary<string, Dictionary<HarnessKind, AgentModel>> Roles { get; } = new(StringComparer.Ordinal);

        public (string Path, string? Harness)? Validated { get; private set; }

        public AgentAnalysis Validate(string path, string? harness)
        {
            Validated = (path, harness);
            return new AgentAnalysis(Report, FilterRefused);
        }

        public AgentAnalysis Scan(string path, string? harness) => new(Report, FilterRefused);

        public DiagnosticReport SyncCheck(string path, HarnessProfileConfig profiles) => SyncReport;

        public IReadOnlyDictionary<string, Dictionary<HarnessKind, AgentModel>> RoleMatrix(string path) => Roles;
    }
}
