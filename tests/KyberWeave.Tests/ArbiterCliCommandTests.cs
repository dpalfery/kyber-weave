using System.Runtime.CompilerServices;
using System.Text.Json;
using KyberWeave.Cli.Commands.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Task 3.1 contract: the read-only Arbiter CLI surfaces (<c>validate</c>,
/// <c>rules</c>, <c>plan</c>, <c>eval</c>, <c>audit</c>) and their exit codes.
/// RED: the <c>arbiter</c> branch is not registered and the commands do not exist yet.
/// </summary>
public sealed class ArbiterCliCommandTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Validate_RepositoryRoot_ExitsZero()
    {
        ArbiterValidateCommand command = new();
        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new ArbiterSettings { Path = RepositoryRoot() }));

        Assert.Equal(0, execution.ExitCode);
    }

    [Fact]
    public void Validate_NoArbiterSection_ExitsZero()
    {
        string empty = NewDir("arbiter-validate-empty");
        ArbiterValidateCommand command = new();
        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new ArbiterSettings { Path = empty }));

        Assert.Equal(0, execution.ExitCode);
    }

    [Fact]
    public void Validate_InvalidSection_ExitsOneWithHintedDiagnostic()
    {
        string host = NewDir("arbiter-validate-invalid");
        Directory.CreateDirectory(Path.Combine(host, ".kyber-weave"));
        File.WriteAllText(
            Path.Combine(host, ".kyber-weave", "kyber-weave.yml"),
            """
            arbiter:
              rules:
                - id: KW-ARB-SCOPE-001
                  trigger: delegate
                  question: Hijacked question?
                  answers: [yes]
                  decide:
                    - when: { fact: delegation.target, exists: true }
                      answer: yes
                  effects: { yes: allow }
            """);

        ArbiterValidateCommand command = new();
        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new ArbiterSettings { Path = host, Format = "json" }));

        Assert.Equal(1, execution.ExitCode);
        Assert.Contains("KW-ARB-CONFIG-", execution.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Rules_ListsRuleFields()
    {
        ArbiterRulesCommand command = new();
        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new ArbiterSettings { Path = NewDir("arbiter-rules") }));

        Assert.Equal(0, execution.ExitCode);
        Assert.Contains("KW-ARB-", execution.Output, StringComparison.Ordinal);
        Assert.Contains("delegate", execution.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Rules_UnknownTrigger_ExitsNonZero()
    {
        ArbiterRulesCommand command = new();
        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new ArbiterSettings { Path = NewDir("arbiter-rules-bad-trigger"), Trigger = "no-such-trigger" }));

        Assert.NotEqual(0, execution.ExitCode);
        Assert.Contains("delegate", execution.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_PrintsParsedTasks()
    {
        string plan = WriteFile("arbiter-plan.md", """
            # Packet 3.1

            ## Tasks

            ### T1: First task

            **Files:** `src/KyberWeave.Cli/Commands/Arbiter/ArbiterSettings.cs`
            """);

        ArbiterPlanCommand command = new();
        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new ArbiterSettings { Path = plan }));

        Assert.Equal(0, execution.ExitCode);
        Assert.Contains("T1", execution.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_WithoutTasks_ReportsPlan001()
    {
        string plan = WriteFile("arbiter-plan-empty.md", """
            # Packet without tasks

            ## Notes

            Nothing actionable here.
            """);

        ArbiterPlanCommand command = new();
        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new ArbiterSettings { Path = plan }));

        Assert.Contains("KW-ARB-PLAN-001", execution.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_UnknownDependency_ReportsParse001()
    {
        string plan = WriteFile("arbiter-plan-dep.md", """
            # Packet 3.1

            ## Tasks

            ### T1: First task

            **Files:** `src/KyberWeave.Cli/Commands/Arbiter/ArbiterSettings.cs`

            **Depends on:** T9
            """);

        ArbiterPlanCommand command = new();
        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new ArbiterSettings { Path = plan }));

        Assert.Contains("KW-ARB-PARSE-001", execution.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Eval_DryRun_PrintsOutcomeAndWritesNothing()
    {
        string host = NewDir("arbiter-eval");
        string eventFile = Path.Combine(host, "event.json");
        File.WriteAllText(eventFile, JsonSerializer.Serialize(new ArbiterEvent
        {
            Harness = "claude",
            Phase = "pre",
            Target = "csharp-dev",
            Prompt = "Implement the arbiter settings type.",
            HarnessCaller = "conductor",
            IsDispatch = true,
        }));

        ArbiterEvalCommand command = new();
        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new ArbiterSettings
            {
                Path = host,
                Trigger = "delegate",
                Event = eventFile,
                Provider = "none",
            }));

        Assert.Equal(0, execution.ExitCode);
        Assert.True(
            execution.Output.Contains("allow", StringComparison.Ordinal) ||
            execution.Output.Contains("escalate", StringComparison.Ordinal),
            execution.Output);
        Assert.False(
            Directory.Exists(Path.Combine(host, "artifacts", "arbiter")),
            "eval is a dry run and must write nothing.");
    }

    [Fact]
    public void Audit_MissingDecision_ReportsAudit001()
    {
        string host = SeedLedger("arbiter-audit-001", ledger =>
        {
            ledger.AppendAsync(new ArbiterLedgerEvent(
                "0000000000000001-aaaaaaaa",
                DateTimeOffset.UtcNow,
                "hook",
                "claude",
                "session-1",
                ArbiterLedgerPhases.Pre)
            {
                Trigger = "delegate",
                Target = "csharp-dev",
            }).GetAwaiter().GetResult();
        });

        ArbiterAuditCommand command = new();
        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new ArbiterSettings { Path = host }));

        Assert.Contains("KW-ARB-AUDIT-001", execution.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Audit_UnmarkedDispatchWithNullTarget_ReportsAudit002()
    {
        // A Devin dispatch whose profile argument is missing has no target. The adapter
        // promises audit reports it, so a null target must be flagged too (review 20.1, b).
        string host = SeedLedger("arbiter-audit-002-null-target", ledger =>
        {
            ledger.AppendAsync(new ArbiterLedgerEvent(
                "0000000000000004-aaaaaaaa",
                DateTimeOffset.UtcNow,
                "hook",
                "devin",
                "session-4",
                ArbiterLedgerPhases.Unmarked)
            {
                Trigger = "delegate",
                Target = null,
            }).GetAwaiter().GetResult();
        });

        ArbiterAuditCommand command = new();
        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new ArbiterSettings { Path = host }));

        Assert.Contains("KW-ARB-AUDIT-002", execution.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Audit_ListsAttestationsAsInformation()
    {
        string host = SeedLedger("arbiter-audit-attested", ledger =>
        {
            ArbiterLedgerEvent pre = new(
                "0000000000000002-aaaaaaaa",
                DateTimeOffset.UtcNow,
                "hook",
                "claude",
                "session-2",
                ArbiterLedgerPhases.Pre)
            {
                Trigger = "delegate",
                Target = "csharp-dev",
            };
            ArbiterLedgerEvent post = new(
                "0000000000000003-aaaaaaaa",
                DateTimeOffset.UtcNow,
                "hook",
                "claude",
                "session-2",
                ArbiterLedgerPhases.Post)
            {
                Trigger = "delegate",
                Target = "task-reviewer",
                Returns = new ArbiterReturnMarkers(Attested: ["T1=complete"]),
            };
            ledger.AppendAsync(pre).GetAwaiter().GetResult();
            ledger.AppendAsync(post).GetAwaiter().GetResult();
        });

        ArbiterAuditCommand command = new();
        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new ArbiterSettings { Path = host }));

        Assert.Equal(0, execution.ExitCode);
        Assert.Contains("T1=complete", execution.Output, StringComparison.Ordinal);
    }

    private string SeedLedger(string name, Action<InFlightLedger> seed)
    {
        string host = NewDir(name);
        InFlightLedger ledger = new(Path.Combine(host, "artifacts", "arbiter"));
        seed(ledger);
        return host;
    }

    private string NewDir(string name)
    {
        string dir = Path.Combine(_temp.Path, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private string WriteFile(string name, string content)
    {
        string path = Path.Combine(_temp.Path, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static CommandExecution Capture(Func<int> execute)
    {
        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(execute);
        return new CommandExecution(execution.Result, execution.Output);
    }

    private static string RepositoryRoot([CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "..", ".."));

    private sealed record CommandExecution(int ExitCode, string Output);
}
