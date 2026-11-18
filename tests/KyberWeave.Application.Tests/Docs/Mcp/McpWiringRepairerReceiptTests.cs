using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Mcp;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace KyberWeave.Application.Tests.Docs.Mcp;

/// <summary>
/// The repairer over in-memory seams: the receipt it persists carries the time the clock gave, and an
/// unwire reads the same receipt back, so the undo record needs neither a disk nor the system clock.
/// </summary>
public sealed class McpWiringRepairerReceiptTests
{
    private const string Root = "/work/repo";
    private const string ServerName = "kw-docs";

    private static readonly DateTimeOffset Now = new(2026, 11, 18, 10, 15, 30, TimeSpan.Zero);

    private readonly InMemoryFileSystem _files = new();
    private readonly FakeTimeProvider _clock = new(Now);

    private McpWiringReceiptStore Receipts() => new(_files);

    private McpWiringRepairer Repairer() => new(
        new InMemoryMcpConfigFiles(_files),
        Receipts(),
        _clock,
        new McpRepositoryPath(new FakePathResolver(_files), new FakeProcessEnvironment()));

    private static McpConfig Declaration() => new()
    {
        RequiredServers = new Dictionary<string, RequiredMcpServer>(StringComparer.Ordinal)
        {
            [ServerName] = new(ServerName, ["kyber-weave-mcp", "--repo-root", "."], McpProvenance.FirstParty)
        }
    };

    [Fact]
    public void ACreatedFileIsRecordedAtTheTimeTheClockGave()
    {
        _files.CreateDirectory(Root + "/.factory");

        McpWiringRepairReport report = Repairer().Repair(Root, Declaration(), new FoundEverywhere(), ["factory"], dryRun: false);

        Assert.Equal(McpWiringChangeOutcome.Created, Assert.Single(report.Files).Outcome);
        McpWiringReceiptRecord? record = Receipts().Load(Root).Find(".factory/mcp.json");
        Assert.NotNull(record);
        Assert.Equal(Now, record.WrittenAtUtc);
        Assert.Equal(McpWiringWriteMode.Created, record.Mode);
    }

    [Fact]
    public void AnInsertedFileIsRecordedAtTheTimeTheClockGaveAndKeepsItsPreWriteBytes()
    {
        _files.CreateDirectory(Root + "/.vscode");
        _files.WriteAllText(Root + "/.vscode/mcp.json", "{\"servers\": {}}");
        _clock.Advance(TimeSpan.FromMinutes(5));

        Repairer().Repair(Root, Declaration(), new FoundEverywhere(), ["vscode"], dryRun: false);

        McpWiringReceiptRecord? record = Receipts().Load(Root).Find(".vscode/mcp.json");
        Assert.NotNull(record);
        Assert.Equal(Now.AddMinutes(5), record.WrittenAtUtc);
        Assert.Equal(Convert.ToBase64String("{\"servers\": {}}"u8.ToArray()), record.PreContent);
    }

    [Fact]
    public void ADryRunWritesNeitherTheFileNorTheReceipt()
    {
        _files.CreateDirectory(Root + "/.factory");

        Repairer().Repair(Root, Declaration(), new FoundEverywhere(), ["factory"], dryRun: true);

        Assert.False(_files.FileExists(Root + "/.factory/mcp.json"));
        Assert.Empty(Receipts().Load(Root).Files);
    }

    [Fact]
    public void UnwireDeletesWhatTheRepairCreatedAndDropsItsRecord()
    {
        _files.CreateDirectory(Root + "/.factory");
        Repairer().Repair(Root, Declaration(), new FoundEverywhere(), ["factory"], dryRun: false);

        McpWiringRepairReport removal = Repairer().Unwire(Root, ["factory"]);

        Assert.Equal(McpWiringChangeOutcome.Deleted, Assert.Single(removal.Files).Outcome);
        Assert.False(_files.FileExists(Root + "/.factory/mcp.json"));
        Assert.Empty(Receipts().Load(Root).Files);
    }

    [Fact]
    public void ARepairWhoseReceiptCannotBeWrittenIsRefusedAndWritesNoConfiguration()
    {
        _files.CreateDirectory(Root + "/.factory");
        _files.FailWhen(
            operation => operation.Name == "WriteAllText" && operation.Path!.EndsWith("mcp-wiring.json", StringComparison.Ordinal),
            new IOException("receipt volume full"));

        McpWiringRepairReport report = Repairer().Repair(Root, Declaration(), new FoundEverywhere(), ["factory"], dryRun: false);

        Assert.Equal(McpWiringChangeOutcome.Refused, Assert.Single(report.Files).Outcome);
        Assert.False(_files.FileExists(Root + "/.factory/mcp.json"));
    }

    private sealed class FoundEverywhere : ILaunchCommandResolver
    {
        public string? Resolve(string command) => "/usr/local/bin/" + command;
    }
}
