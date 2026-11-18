using KyberWeave.Contracts.Repositories;
using KyberWeave.Tests.Arbiter;
using KyberWeave.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using KyberWeave.Cli.Commands.Arbiter;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The Arbiter logs are read through a window at their end. The hook stays quiet about what the window leaves out;
/// <c>arbiter audit</c> and <c>arbiter tune</c> must say so, and must not present a log that exists but cannot be read
/// as an empty one.
/// </summary>
public sealed class ArbiterLogWindowReportTests : IDisposable
{
    private const int PaddingChars = 100_000;
    private static readonly DateTimeOffset At = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _temp = new();
    private readonly ArbiterTestHost _host;

    public ArbiterLogWindowReportTests() =>
        _host = ArbiterTestHost.Create(services =>
            services.AddSingleton<IProcessEnvironment>(new FakeProcessEnvironment(() => Path.Combine(_temp.Path, "home"))));

    public void Dispose()
    {
        _host.Dispose();
        _temp.Dispose();
    }

    private string Root => Path.Combine(_temp.Path, "host");

    private string ArbiterDirectory => Path.Combine(Root, "artifacts", "arbiter");

    [Fact]
    public async Task ALedgerLongerThanTheWindowReadsAsTruncated()
    {
        await AppendLedgerPastTheWindowAsync();

        ArbiterLogRead<ArbiterLedgerEvent> read = new InFlightLedger(ArbiterDirectory).ReadAllWithDiagnostics();

        Assert.True(read.Truncated);
        Assert.Null(read.Unreadable);
        Assert.Equal(0, read.MalformedLines);
        Assert.NotEmpty(read.Records);
        Assert.DoesNotContain(read.Records, record => record.Id == "event-000");
        Assert.Contains(read.Records, record => record.Id == "event-099");
    }

    [Fact]
    public async Task ADecisionLogLongerThanTheWindowReadsAsTruncated()
    {
        await AppendDecisionsPastTheWindowAsync();

        ArbiterLogRead<ArbiterDecisionRecord> read = new DecisionLog(ArbiterDirectory).ReadAllWithDiagnostics();

        Assert.True(read.Truncated);
        Assert.DoesNotContain(read.Records, record => record.Id == "decision-000");
        Assert.Contains(read.Records, record => record.Id == "decision-099");
    }

    [Fact]
    public async Task ALogThatFitsTheWindowIsNotTruncated()
    {
        Directory.CreateDirectory(ArbiterDirectory);
        await new InFlightLedger(ArbiterDirectory).AppendAsync(Event("event-small", 10));

        ArbiterLogRead<ArbiterLedgerEvent> read = new InFlightLedger(ArbiterDirectory).ReadAllWithDiagnostics();

        Assert.False(read.Truncated);
        Assert.Null(read.Unreadable);
        Assert.Single(read.Records);
    }

    [Fact]
    public void AnAbsentLogIsEmptyWithNoProblem()
    {
        ArbiterLogRead<ArbiterLedgerEvent> read = new InFlightLedger(ArbiterDirectory).ReadAllWithDiagnostics();

        Assert.Empty(read.Records);
        Assert.False(read.Truncated);
        Assert.Null(read.Unreadable);
    }

    [Fact]
    public void ALogThatExistsButCannotBeOpenedIsReportedUnreadableNotEmpty()
    {
        // A folder where the log belongs fails to open with an access error, as a file the user may not read does.
        Directory.CreateDirectory(Path.Combine(ArbiterDirectory, InFlightLedger.FileName));
        Directory.CreateDirectory(Path.Combine(ArbiterDirectory, DecisionLog.FileName));

        ArbiterLogRead<ArbiterLedgerEvent> ledger = new InFlightLedger(ArbiterDirectory).ReadAllWithDiagnostics();
        ArbiterLogRead<ArbiterDecisionRecord> decisions = new DecisionLog(ArbiterDirectory).ReadAllWithDiagnostics();

        Assert.Empty(ledger.Records);
        Assert.NotNull(ledger.Unreadable);
        Assert.NotNull(decisions.Unreadable);
    }

    [Fact]
    public async Task AuditReportsATruncatedLedgerAsAWarning()
    {
        WriteHostConfig();
        await AppendLedgerPastTheWindowAsync();

        CapturedConsoleExecution<int> run = ProcessConsoleCapture.Run(() => _host.Command<ArbiterAuditCommand>().Execute(
            null!, new ArbiterSettings { Path = Root }));

        Assert.Contains(ArbiterAuditCommand.LogWindowTruncated, run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuditReportsATruncatedDecisionLogAsAWarning()
    {
        WriteHostConfig();
        await AppendDecisionsPastTheWindowAsync();

        CapturedConsoleExecution<int> run = ProcessConsoleCapture.Run(() => _host.Command<ArbiterAuditCommand>().Execute(
            null!, new ArbiterSettings { Path = Root }));

        Assert.Contains(ArbiterAuditCommand.LogWindowTruncated, run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuditSaysNothingAboutTheWindowWhenTheLogFitsIt()
    {
        WriteHostConfig();
        Directory.CreateDirectory(ArbiterDirectory);
        await new InFlightLedger(ArbiterDirectory).AppendAsync(Event("event-small", 10));

        CapturedConsoleExecution<int> run = ProcessConsoleCapture.Run(() => _host.Command<ArbiterAuditCommand>().Execute(
            null!, new ArbiterSettings { Path = Root }));

        // The audit ran to its summary, so the absence below is "the log fit the window", not "the command printed nothing".
        Assert.Contains("1 ledger event(s)", run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(ArbiterAuditCommand.LogWindowTruncated, run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void AuditFailsClosedOnALogItCannotRead()
    {
        WriteHostConfig();
        Directory.CreateDirectory(Path.Combine(ArbiterDirectory, InFlightLedger.FileName));

        CapturedConsoleExecution<int> run = ProcessConsoleCapture.Run(() => _host.Command<ArbiterAuditCommand>().Execute(
            null!, new ArbiterSettings { Path = Root }));

        Assert.Equal(1, run.Result);
        Assert.Contains("could not be read", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TuneTextReportNamesATruncatedLog()
    {
        await AppendDecisionsPastTheWindowAsync();

        CapturedConsoleExecution<int> run = ProcessConsoleCapture.Run(() => CommandAppFixture.ArbiterTune().Run(["arbiter", "tune", Root]));

        Assert.Equal(0, run.Result);
        Assert.Contains($"warning {ArbiterAuditCommand.LogWindowTruncated}", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TuneJsonReportCarriesATruncatedMarkerOnlyWhenTheLogWasCut()
    {
        await AppendDecisionsPastTheWindowAsync();

        CapturedConsoleExecution<int> cut = ProcessConsoleCapture.Run(() => CommandAppFixture.ArbiterTune().Run(["arbiter", "tune", Root, "--json"]));
        using JsonDocument cutDocument = JsonDocument.Parse(cut.Output);

        Assert.True(cutDocument.RootElement.TryGetProperty("truncated", out JsonElement note));
        Assert.Contains("decisions.jsonl", note.GetString(), StringComparison.Ordinal);

        string whole = Path.Combine(_temp.Path, "whole");
        Directory.CreateDirectory(Path.Combine(whole, "artifacts", "arbiter"));
        CapturedConsoleExecution<int> fits = ProcessConsoleCapture.Run(() => CommandAppFixture.ArbiterTune().Run(["arbiter", "tune", whole, "--json"]));
        using JsonDocument fitsDocument = JsonDocument.Parse(fits.Output);

        Assert.False(fitsDocument.RootElement.TryGetProperty("truncated", out _));
    }

    [Fact]
    public void TuneFailsClosedOnALogItCannotRead()
    {
        Directory.CreateDirectory(Path.Combine(ArbiterDirectory, DecisionLog.FileName));

        CapturedConsoleExecution<int> run = ProcessConsoleCapture.Run(() => CommandAppFixture.ArbiterTune().Run(["arbiter", "tune", Root]));

        Assert.Equal(1, run.Result);
        Assert.Contains("could not be read", run.Output, StringComparison.Ordinal);
    }

    private void WriteHostConfig()
    {
        Directory.CreateDirectory(Path.Combine(Root, ".kyber-weave"));
        File.WriteAllText(
            Path.Combine(Root, ".kyber-weave", "kyber-weave.yml"),
            "ontology:\n  docs-root: docs\narbiter:\n  provider:\n    kind: none\n");
    }

    private async Task AppendLedgerPastTheWindowAsync()
    {
        Directory.CreateDirectory(ArbiterDirectory);
        InFlightLedger ledger = new(ArbiterDirectory);
        for (int i = 0; i < 100; i++)
        {
            await ledger.AppendAsync(Event($"event-{i:D3}", PaddingChars));
        }
    }

    private async Task AppendDecisionsPastTheWindowAsync()
    {
        Directory.CreateDirectory(ArbiterDirectory);
        DecisionLog decisions = new(ArbiterDirectory);
        for (int i = 0; i < 100; i++)
        {
            await decisions.AppendAsync(Decision($"decision-{i:D3}", PaddingChars));
        }
    }

    private static ArbiterLedgerEvent Event(string id, int padding) =>
        new(id, At, "hook", "claude", "s-1", ArbiterLedgerPhases.Pre)
        {
            Trigger = "delegate",
            Target = new string('x', padding),
        };

    private static ArbiterDecisionRecord Decision(string id, int padding) =>
        new(
            id,
            At,
            "hook",
            "claude",
            null,
            "ledger-1",
            "lens.spawn",
            new string('x', padding),
            "header",
            "review-lens",
            null,
            null,
            null,
            "shipped",
            [],
            new ArbiterProviderRecord("systemone", null, "jev-1.13.0", null, 12, ArbiterProviderStatuses.Answered),
            "allow",
            0,
            5);
}
