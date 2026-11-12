using System.Runtime.CompilerServices;
using System.Text.Json;
using KyberWeave.Cli.Commands.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Plans;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Task 3.1 contract: the read-only Arbiter CLI surfaces (<c>validate</c>,
/// <c>rules</c>, <c>plan</c>, <c>eval</c>, <c>audit</c>) and their exit codes.
/// RED: the <c>arbiter</c> branch is not registered and the commands do not exist yet.
/// </summary>
[Collection(ArbiterCommandCompositionCollection.Name)]
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

    /// <summary>
    /// The shipped rules are listed with each rule's shadow default and its <c>for-each</c> list,
    /// and the lens rule shipped for no-code-change reviews is among them.
    /// </summary>
    [Fact]
    public void Rules_ListsShadowDefaultAndForEachForEveryShippedRule()
    {
        ArbiterRulesCommand command = new();
        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new ArbiterSettings { Path = NewDir("arbiter-rules-columns") }));

        Assert.Equal(0, execution.ExitCode);
        Assert.Contains("shadow", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("for-each", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("KW-ARB-LENS-002", execution.Output, StringComparison.Ordinal);
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

    /// <summary>
    /// The execution digest is the value a task file records. The plan command has to report it
    /// where a script can read it, or a reader can only reproduce it by reaching into the parser,
    /// and cannot confirm that a task file was written from this plan.
    /// </summary>
    [Fact]
    public void Plan_JsonReportsTheExecutionDigest()
    {
        string text = DigestPlan(frontmatterStatus: "current", bodyStatus: "Ready");
        string plan = WriteFile("arbiter-plan-digest-json.md", text);

        ArbiterPlanCommand command = new();
        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new ArbiterSettings { Path = plan, Format = "json" }));

        string expected = PlanDocumentParser.ExecutionDigest(PlanDocumentParser.Parse(text));
        string reported = ReportedExecutionDigest(execution.Output);
        Assert.Equal(expected, reported);
        Assert.Matches("^[0-9a-f]{64}$", reported);
    }

    /// <summary>
    /// The digest is also printed in the default table, on one line: a folded table cell would
    /// split the 64 characters across rows, and a reader could no longer copy it whole.
    /// </summary>
    [Fact]
    public void Plan_TableShowsTheExecutionDigest()
    {
        string text = DigestPlan(frontmatterStatus: "current", bodyStatus: "Ready");
        string plan = WriteFile("arbiter-plan-digest-table.md", text);

        ArbiterPlanCommand command = new();
        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new ArbiterSettings { Path = plan }));

        string expected = PlanDocumentParser.ExecutionDigest(PlanDocumentParser.Parse(text));
        Assert.Matches("(?m)^[^\\r\\n]*" + expected + "[^\\r\\n]*\\r?$", execution.Output);
    }

    /// <summary>
    /// The table shows the digest once. The unfolded line is the copyable one; a second copy in the
    /// metrics table can fold across rows on a narrow console, so it is not rendered there.
    /// </summary>
    [Fact]
    public void Plan_TableShowsTheDigestOnce()
    {
        string text = DigestPlan(frontmatterStatus: "current", bodyStatus: "Ready");
        string plan = WriteFile("arbiter-plan-digest-once.md", text);

        ArbiterPlanCommand command = new();
        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new ArbiterSettings { Path = plan }));

        string expected = PlanDocumentParser.ExecutionDigest(PlanDocumentParser.Parse(text));
        Assert.Equal(1, execution.Output.Split(expected).Length - 1);

        // The metrics cell truncates the digest, so the full value never repeats there; a prefix
        // is what a second copy would leave behind, and counting it is what catches the duplicate.
        const int prefixLength = 16;
        Assert.True(expected.Length >= prefixLength, "The digest is shorter than the prefix this test counts.");
        Assert.Equal(1, execution.Output.Split(expected[..prefixLength]).Length - 1);
    }

    /// <summary>
    /// A plan with no tasks has no execution to digest. It reports KW-ARB-PLAN-001 and no digest in
    /// either format, so a digest for an empty execution cannot be copied into a task file.
    /// </summary>
    [Fact]
    public void Plan_WithoutTasks_ReportsNoDigest()
    {
        string plan = WriteFile("arbiter-plan-digest-empty.md", """
            # Packet without tasks

            ## Notes

            Nothing actionable here.
            """);

        ArbiterPlanCommand command = new();
        CommandExecution json = Capture(() => command.Execute(
            null!,
            new ArbiterSettings { Path = plan, Format = "json" }));
        CommandExecution table = Capture(() => command.Execute(
            null!,
            new ArbiterSettings { Path = plan }));

        Assert.Contains("KW-ARB-PLAN-001", json.Output, StringComparison.Ordinal);
        Assert.False(ReportsMetric(json.Output, "execution-digest"));
        Assert.DoesNotContain("Execution digest:", table.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A task with no Files line is a completeness finding, not dropped content. Its execution is
    /// whole, so its digest is offered, and the finding is still reported.
    /// </summary>
    [Fact]
    public void Plan_TaskWithoutFiles_StillReportsTheDigest()
    {
        string plan = WriteFile("arbiter-plan-digest-no-files.md", """
            # Packet without files

            ## Tasks

            ### T1: First task

            Body text with no file scope.
            """);

        ArbiterPlanCommand command = new();
        CommandExecution json = Capture(() => command.Execute(
            null!,
            new ArbiterSettings { Path = plan, Format = "json" }));

        Assert.Equal(0, json.ExitCode);
        Assert.Contains("KW-ARB-PARSE-002", json.Output, StringComparison.Ordinal);
        Assert.True(ReportsMetric(json.Output, "execution-digest"));
    }

    /// <summary>
    /// The digest metric is a json field. The table prints its own line, and sarif and markdown carry
    /// no digest, so a reader of either format cannot take a digest for part of its report.
    /// </summary>
    [Fact]
    public void Plan_DigestMetricIsJsonOnly()
    {
        string plan = WriteFile(
            "arbiter-plan-digest-formats.md",
            DigestPlan(frontmatterStatus: "current", bodyStatus: "Ready"));

        ArbiterPlanCommand command = new();
        CommandExecution json = Capture(() => command.Execute(
            null!,
            new ArbiterSettings { Path = plan, Format = "json" }));
        CommandExecution sarif = Capture(() => command.Execute(
            null!,
            new ArbiterSettings { Path = plan, Format = "sarif" }));
        CommandExecution markdown = Capture(() => command.Execute(
            null!,
            new ArbiterSettings { Path = plan, Format = "markdown" }));

        Assert.True(ReportsMetric(json.Output, "execution-digest"));
        Assert.False(JsonCarriesProperty(sarif.Output, "execution-digest"));
        Assert.DoesNotContain("execution-digest", markdown.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Execution digest:", markdown.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A <c>## Tasks</c> section with no task heading parses to zero tasks. Its digest would describe
    /// an empty execution, so no digest is reported in either format.
    /// </summary>
    [Fact]
    public void Plan_EmptyTasksSection_ReportsNoDigest()
    {
        string plan = WriteFile("arbiter-plan-digest-empty-tasks.md", """
            # Packet with an empty Tasks section

            ## Tasks

            Nothing is listed yet.
            """);

        ArbiterPlanCommand command = new();
        CommandExecution json = Capture(() => command.Execute(
            null!,
            new ArbiterSettings { Path = plan, Format = "json" }));
        CommandExecution table = Capture(() => command.Execute(
            null!,
            new ArbiterSettings { Path = plan }));

        Assert.Equal(0, json.ExitCode);
        Assert.Equal(0, table.ExitCode);
        Assert.Contains("KW-ARB-PLAN-001", json.Output, StringComparison.Ordinal);
        Assert.Contains("KW-ARB-PLAN-001", table.Output, StringComparison.Ordinal);
        Assert.False(ReportsMetric(json.Output, "execution-digest"));
        Assert.DoesNotContain("Execution digest:", table.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A plan with a parse finding does not parse cleanly, so the digest of what was parsed is not
    /// offered: a task file copied from it would record a view that dropped the broken line. The
    /// finding itself is still reported.
    /// </summary>
    [Fact]
    public void Plan_WithParseFinding_ReportsNoDigestButStillReportsTheFinding()
    {
        string plan = WriteFile("arbiter-plan-digest-parse-finding.md", """
            # Packet 3.1

            ## Tasks

            ### T1: First task

            **Files:** `src/KyberWeave.Cli/Commands/Arbiter/ArbiterSettings.cs`

            **Depends on:** T9
            """);

        ArbiterPlanCommand command = new();
        CommandExecution json = Capture(() => command.Execute(
            null!,
            new ArbiterSettings { Path = plan, Format = "json" }));
        CommandExecution table = Capture(() => command.Execute(
            null!,
            new ArbiterSettings { Path = plan }));

        Assert.Equal(0, json.ExitCode);
        Assert.Equal(0, table.ExitCode);
        Assert.Contains("KW-ARB-PARSE-001", json.Output, StringComparison.Ordinal);
        Assert.Contains("KW-ARB-PARSE-001", table.Output, StringComparison.Ordinal);
        Assert.False(ReportsMetric(json.Output, "execution-digest"));
        Assert.DoesNotContain("Execution digest:", table.Output, StringComparison.Ordinal);
    }

    /// <summary>Whether any object in the json output carries a property named <paramref name="name"/>.</summary>
    private static bool JsonCarriesProperty(string output, string name)
    {
        using JsonDocument document = ParseWholeOutput(output);
        return CarriesProperty(document.RootElement, name);
    }

    private static bool CarriesProperty(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (property.Name == name || CarriesProperty(property.Value, name))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                if (CarriesProperty(item, name))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Whether the json report's <c>metrics</c> object carries <paramref name="key"/>.</summary>
    private static bool ReportsMetric(string output, string key)
    {
        using JsonDocument document = ParseWholeOutput(output);
        return document.RootElement.TryGetProperty("metrics", out JsonElement metrics)
            && metrics.TryGetProperty(key, out _);
    }

    /// <summary>
    /// A status change is bookkeeping, not a plan edit, so it must not stale a task file. The
    /// digest covers the tasks, the contract and the out-of-scope list, and not the status
    /// fields, which the plan's owner moves without touching what a worker is asked to do.
    /// </summary>
    [Fact]
    public void Plan_DigestIgnoresAStatusFlip()
    {
        string draft = WriteFile("arbiter-plan-digest-draft.md", DigestPlan(frontmatterStatus: "draft", bodyStatus: "Draft"));
        string ready = WriteFile("arbiter-plan-digest-ready.md", DigestPlan(frontmatterStatus: "current", bodyStatus: "Ready"));

        string draftDigest = ReportedDigestOf(draft);
        string readyDigest = ReportedDigestOf(ready);

        Assert.Equal(draftDigest, readyDigest);
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
    public void Audit_MalformedLedgerLine_ReportsRatherThanEscaping()
    {
        // A truncated or hand-edited line is malformed JSON, which throws JsonException --
        // not an IOException, so the read's catch never saw it and the exception escaped
        // the command as an unhandled failure instead of a diagnostic.
        string host = SeedLedger("arbiter-audit-malformed", ledger =>
        {
            ledger.AppendAsync(new ArbiterLedgerEvent(
                "0000000000000003-aaaaaaaa",
                DateTimeOffset.UtcNow,
                "hook",
                "claude",
                "session-3",
                ArbiterLedgerPhases.Pre)
            {
                Trigger = "delegate",
                Target = "csharp-dev",
            }).GetAwaiter().GetResult();
        });
        File.AppendAllText(
            Path.Combine(host, "artifacts", "arbiter", "ledger.jsonl"),
            "{\"id\": \"broken\", \"phase\": \n");

        ArbiterAuditCommand command = new();
        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new ArbiterSettings { Path = host }));

        Assert.Equal(1, execution.ExitCode);
        Assert.Contains("1 error", execution.Output, StringComparison.Ordinal);
        Assert.Contains("complete record", execution.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Audit_MalformedDecisionLine_ReportsRatherThanEscaping()
    {
        string host = SeedLedger("arbiter-audit-malformed-decision", ledger =>
        {
            ledger.AppendAsync(new ArbiterLedgerEvent(
                "0000000000000004-aaaaaaaa",
                DateTimeOffset.UtcNow,
                "hook",
                "claude",
                "session-4",
                ArbiterLedgerPhases.Pre)
            {
                Trigger = "delegate",
            }).GetAwaiter().GetResult();
        });
        File.AppendAllText(
            Path.Combine(host, "artifacts", "arbiter", "decisions.jsonl"),
            "not json at all\n");

        ArbiterAuditCommand command = new();
        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new ArbiterSettings { Path = host }));

        Assert.Equal(1, execution.ExitCode);
        Assert.Contains("1 error", execution.Output, StringComparison.Ordinal);
        Assert.Contains("complete record", execution.Output, StringComparison.Ordinal);
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

    /// <summary>
    /// A dispatch that classified as no trigger is the pass-through shape: the evaluator
    /// has no rule to run, returns allow, and records no decision. Reporting it as a
    /// missing decision makes every benign pass on a host with no hooks look like a
    /// crashed hook, which is the one thing this id is supposed to mean.
    /// </summary>
    [Fact]
    public void Audit_APassThroughEventWithNoTrigger_ReportsNoMissingDecision()
    {
        string host = SeedLedger("arbiter-audit-pass-through", ledger =>
        {
            ledger.AppendAsync(new ArbiterLedgerEvent(
                "0000000000000005-bbbbbbbb",
                DateTimeOffset.UtcNow,
                "hook",
                "claude",
                "session-5",
                ArbiterLedgerPhases.Pre)
            {
                // No trigger and no caller: nothing classified, nothing to decide.
                Target = "Read",
            }).GetAwaiter().GetResult();
        });

        ArbiterAuditCommand command = new();
        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new ArbiterSettings { Path = host }));

        Assert.Equal(0, execution.ExitCode);
        Assert.DoesNotContain("KW-ARB-AUDIT-001", execution.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The exemption is the pass-through shape only: an event that named a trigger
    /// still has to produce a decision, or the hook did die mid-evaluation.
    /// </summary>
    [Fact]
    public void Audit_ATriggeredEventWithNoDecision_StillReportsTheMissingDecision()
    {
        string host = SeedLedger("arbiter-audit-triggered-no-decision", ledger =>
        {
            ledger.AppendAsync(new ArbiterLedgerEvent(
                "0000000000000006-cccccccc",
                DateTimeOffset.UtcNow,
                "hook",
                "claude",
                "session-6",
                ArbiterLedgerPhases.Pre)
            {
                Trigger = "refute.spawn",
                Target = "review-lens",
                Caller = "code-reviewer",
            }).GetAwaiter().GetResult();
        });

        ArbiterAuditCommand command = new();
        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new ArbiterSettings { Path = host }));

        Assert.Contains("KW-ARB-AUDIT-001", execution.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The plan the digest tests write: a file scope on each task, one contract row and an
    /// out-of-scope path, so every field the digest covers has a value an edit could move.
    /// </summary>
    private static string DigestPlan(string frontmatterStatus, string bodyStatus) =>
        $$"""
        ---
        id: plans/2026-11-12-digest-plan
        title: Digest plan
        doc-type: plan
        status: {{frontmatterStatus}}
        component: KyberWeave
        owner: Maintainers
        last-reviewed: 2026-11-12
        development-mode: test-first
        ---

        # Digest plan

        **Status: {{bodyStatus}}.** This plan is approved for execution.

        ## Test contract

        | Task | Test project or file | Runner filter | Observable behaviour | RED evidence required | GREEN acceptance |
        |---|---|---|---|---|---|
        | T1 | `tests/KyberWeave.Tests/ArbiterCliCommandTests.cs` | `dotnet test --filter FullyQualifiedName~ArbiterCliCommandTests` | The digest is reported. | Fails: the digest is absent. | Passes. |

        ## Tasks

        ### T1: First task

        - **Files:** `src/KyberWeave.Cli/Commands/Arbiter/ArbiterPlanCommand.cs`
        - **Depends on:** none
        - **Required skills:** `csharp-dev`

        ### T2: Second task

        - **Files:** `src/KyberWeave.Core/Arbiter/Plans/PlanDocumentParser.cs`
        - **Depends on:** T1
        - **Required skills:** `csharp-dev`

        ## Out of scope

        - `docs/archive/**`
        """;

    /// <summary>
    /// The execution digest the plan command reports for a plan file. Shared with the task-file
    /// tests, which record the same digest in a task file they then validate.
    /// </summary>
    internal static string ReportedDigestOf(string planPath)
    {
        ArbiterPlanCommand command = new();
        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new ArbiterSettings { Path = planPath, Format = "json" }));

        return ReportedExecutionDigest(execution.Output);
    }

    /// <summary>
    /// Reads the <c>execution-digest</c> metric from the output, which must be one JSON document
    /// with a <c>metrics</c> object. Each shape check fails the test with the output named rather
    /// than throwing from the reader.
    /// </summary>
    private static string ReportedExecutionDigest(string output)
    {
        using JsonDocument document = ParseWholeOutput(output);
        JsonElement root = document.RootElement;

        JsonElement metrics = default;
        bool hasMetrics = root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("metrics", out metrics)
            && metrics.ValueKind == JsonValueKind.Object;
        Assert.True(hasMetrics, $"expected a 'metrics' object at the root of the plan command's JSON output. Output:{Environment.NewLine}{output}");

        JsonElement digest = default;
        bool hasDigest = metrics.TryGetProperty("execution-digest", out digest)
            && digest.ValueKind == JsonValueKind.String;
        Assert.True(hasDigest, $"expected a string 'execution-digest' metric in the plan command's JSON output. Output:{Environment.NewLine}{output}");

        return digest.GetString()!;
    }

    private static JsonDocument ParseWholeOutput(string output)
    {
        try
        {
            return JsonDocument.Parse(output);
        }
        catch (JsonException exception)
        {
            Assert.Fail(
                $"expected the plan command's whole output to be one JSON document, but it did not parse " +
                $"({exception.Message}). Output:{Environment.NewLine}{output}");
            throw;
        }
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
