using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Rules;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract for task 1.5: the ledger (<c>kyber-arbiter.ledger/v1</c>) and the
/// decision log (<c>kyber-arbiter.decision/v1</c>). RED:
/// <see cref="InFlightLedger"/> and <see cref="DecisionLog"/> do not exist yet.
/// Covers the schemas (fields exactly as designed), locked appends that never
/// interleave and fail closed on an unavailable lock, partial-line tolerance on
/// reads, pre/post pairing by call-id then pair-digest, the queries the rules
/// read (in-flight, concurrent, completed, red evidence, <c>REPEAT</c>) and the
/// no-secrets rule (prompts and code appear only as digests).
/// </summary>
public sealed class ArbiterLedgerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kw-arbiter-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string ArbiterDirectory => Path.Combine(_root, "artifacts", "arbiter");

    private static DateTimeOffset At(int seconds) =>
        new DateTimeOffset(2025, 10, 1, 12, 0, 0, TimeSpan.Zero).AddSeconds(seconds);

    private static ArbiterLedgerEvent NewEvent(
        DateTimeOffset at,
        string phase,
        string? callId = null,
        string? pairDigest = null,
        string? trigger = null,
        string? target = null,
        string? caller = null,
        string? callerSource = null,
        IReadOnlyList<string>? taskFiles = null,
        ArbiterLedgerHeaders? headers = null,
        ArbiterReturnMarkers? returnsMarkers = null,
        string harness = "test-harness",
        string? session = "session-1")
        => new(
            ArbiterRecordId.New(at), at, ArbiterSources.Hook, harness, session, phase)
        {
            CallId = callId,
            PairDigest = pairDigest,
            Trigger = trigger,
            Target = target,
            Caller = caller,
            CallerSource = callerSource ?? (caller is null ? null : ArbiterCallerSources.Harness),
            TaskFiles = taskFiles,
            Headers = headers,
            Returns = returnsMarkers,
        };

    private static ArbiterDecisionRecord Decision(
        DateTimeOffset at,
        string outcome,
        string[] ruleIds,
        string planFile = "docs/plans/plan.md",
        string planDigest = "plan-digest-1",
        string? task = "T3",
        string? ledgerId = null)
        => new(
            ArbiterRecordId.New(at),
            at,
            ArbiterSources.Hook,
            "test-harness",
            "session-1",
            ledgerId ?? "ledger-1",
            "delegate",
            "conductor",
            ArbiterCallerSources.Harness,
            "csharp-dev",
            planFile,
            planDigest,
            task,
            "ruleset-sha",
            [.. ruleIds.Select(id => new ArbiterDecisionRule(
                id, 0, "beyond-files", null, null, null, outcome, [], "one line of evidence"))],
            new ArbiterProviderRecord(
                "rules", null, null, null, null, ArbiterProviderStatuses.ShortCircuited),
            outcome,
            1,
            42);

    private static string Sha256Hex(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static void AssertKeys(JsonElement element, params string[] expected)
    {
        string[] actual = [.. element.EnumerateObject().Select(property => property.Name)];
        Assert.Equal(
            expected.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
            actual.OrderBy(name => name, StringComparer.Ordinal).ToArray());
    }

    // ---- Schemas (acceptance 1) ----

    [Fact]
    public async Task LedgerSchema_FieldsExactlyAsDesigned()
    {
        var ledger = new InFlightLedger(ArbiterDirectory);
        DateTimeOffset at = At(0);
        var ev = new ArbiterLedgerEvent(
            ArbiterRecordId.New(at), at, ArbiterSources.Hook, "pi", "session-1", ArbiterLedgerPhases.Pre)
        {
            CallId = "call-1",
            PairDigest = "pair-digest",
            PreId = "pre-1",
            Caller = "conductor",
            CallerSource = ArbiterCallerSources.Harness,
            Target = "csharp-dev",
            Trigger = "delegate",
            Marker = "delegate",
            Headers = new ArbiterLedgerHeaders("docs/plans/plan.md", "T3", "infra-workflow", "finding-1"),
            TaskFiles = ["docs/plans/plan.md"],
            Snapshot = new ArbiterSnapshot(
                "head-sha", new Dictionary<string, string> { ["src/a.cs"] = "blob-1" }),
            ChangedPaths = ["src/a.cs"],
            Returns = new ArbiterReturnMarkers("PASS", "3 failing tests", "DONE", ["T3=complete"]),
            BodyNamesPlanningPath = true,
            DecisionId = "decision-1",
        };

        await ledger.AppendAsync(ev);

        string[] expectedLedgerLocation = ["arbiter", "ledger.jsonl"];
        string[] actualLedgerLocation =
            [Path.GetFileName(Path.GetDirectoryName(ledger.FilePath))!, Path.GetFileName(ledger.FilePath)];
        Assert.Equal(expectedLedgerLocation, actualLedgerLocation);
        Assert.Equal(Path.Combine(ArbiterDirectory, ".lock"), ledger.LockFilePath);

        string[] lines = await File.ReadAllLinesAsync(ledger.FilePath);
        Assert.Single(lines);
        using JsonDocument doc = JsonDocument.Parse(lines[0]);
        Assert.Equal("kyber-arbiter.ledger/v1", doc.RootElement.GetProperty("schema").GetString());
        string? atText = doc.RootElement.GetProperty("at").GetString();
        Assert.NotNull(atText);
        Assert.EndsWith("Z", atText, StringComparison.Ordinal);
        Assert.StartsWith("2025-10-01T12:00:00", atText, StringComparison.Ordinal);
        AssertKeys(
            doc.RootElement,
            "schema", "id", "at", "source", "harness", "session", "phase",
            "call-id", "pair-digest", "pre-id",
            "caller", "caller-source", "target", "trigger", "marker",
            "headers", "task-files", "snapshot", "changed-paths", "returns",
            "body-names-planning-path", "decision-id");
        AssertKeys(doc.RootElement.GetProperty("headers"), "plan-file", "task", "lens", "refute");
        AssertKeys(
            doc.RootElement.GetProperty("returns"),
            "task-review", "red-evidence", "planner-status", "attested");
        AssertKeys(doc.RootElement.GetProperty("snapshot"), "head", "blobs");
    }

    [Fact]
    public async Task DecisionSchema_FieldsExactlyAsDesigned()
    {
        var decisions = new DecisionLog(ArbiterDirectory);
        DateTimeOffset at = At(0);
        var rule = new ArbiterDecisionRule(
            "KW-ARB-SCOPE-002", 1, "beyond-files", [0.86], 0.91, 0.6, RuleEffects.Escalate,
            [new ArbiterRuleFactRef("plan.task.files", "derived")],
            "delegation asks to refactor GateRunner");
        var provider = new ArbiterProviderRecord(
            "model", "https://api.example.com", "jev-1.13.0",
            new ArbiterProviderUsage(512, 64), 812, ArbiterProviderStatuses.Answered);
        var decision = new ArbiterDecisionRecord(
            ArbiterRecordId.New(at), at, ArbiterSources.Hook, "pi", "session-1",
            "ledger-1", "delegate", "conductor", ArbiterCallerSources.Harness,
            "csharp-dev", "docs/plans/plan.md", "plan-digest", "T3",
            "ruleset-sha", [rule], provider, RuleEffects.Escalate, 1, 42);

        await decisions.AppendAsync(decision);

        Assert.Equal(Path.Combine(ArbiterDirectory, "decisions.jsonl"), decisions.FilePath);
        string[] lines = await File.ReadAllLinesAsync(decisions.FilePath);
        Assert.Single(lines);
        using JsonDocument doc = JsonDocument.Parse(lines[0]);
        Assert.Equal("kyber-arbiter.decision/v1", doc.RootElement.GetProperty("schema").GetString());
        AssertKeys(
            doc.RootElement,
            "schema", "id", "at", "source", "harness", "session",
            "ledger-id", "trigger", "caller", "caller-source",
            "target", "plan-file", "plan-digest", "task", "rule-set",
            "rules", "provider", "outcome", "repeat", "duration-ms");
        Assert.Single(doc.RootElement.GetProperty("rules").EnumerateArray());
        AssertKeys(
            doc.RootElement.GetProperty("rules")[0],
            "id", "step", "answer", "probabilities", "confidence", "threshold",
            "effect", "facts", "evidence");
        AssertKeys(doc.RootElement.GetProperty("rules")[0].GetProperty("facts")[0], "name", "label");
        AssertKeys(
            doc.RootElement.GetProperty("provider"),
            "kind", "endpoint-origin", "model", "usage", "latency-ms", "status");
        Assert.Equal(1, doc.RootElement.GetProperty("repeat").GetInt32());
        Assert.Equal(42, doc.RootElement.GetProperty("duration-ms").GetInt32());
    }

    [Fact]
    public async Task Append_WritesCompleteLines_WithSortableUniqueIds()
    {
        var ledger = new InFlightLedger(ArbiterDirectory);
        var first = NewEvent(At(0), ArbiterLedgerPhases.Pre);
        var second = NewEvent(At(1), ArbiterLedgerPhases.Post);

        await ledger.AppendAsync(first);
        await ledger.AppendAsync(second);

        Assert.Equal(2, (await File.ReadAllLinesAsync(ledger.FilePath)).Length);
        var events = ledger.ReadAll();
        Assert.Equal(2, events.Count);
        Assert.Equal(first.Id, events[0].Id);
        Assert.Equal(second.Id, events[1].Id);
        Assert.True(
            string.CompareOrdinal(events[0].Id, events[1].Id) < 0,
            $"expected sortable ids but '{events[0].Id}' >= '{events[1].Id}'");

        // Same timestamp: the random suffix keeps ids unique.
        Assert.NotEqual(ArbiterRecordId.New(At(2)), ArbiterRecordId.New(At(2)));
    }

    [Fact]
    public async Task Append_WhenLockUnavailable_FailsClosedAndWritesNothing()
    {
        var ledger = new InFlightLedger(ArbiterDirectory);
        Directory.CreateDirectory(ArbiterDirectory);

        // Another process (or handle) holding .lock with FileShare.None is the
        // contention the design names; the append must fail closed, not wait.
        long started;
        IOException failure;
        using (File.Open(ledger.LockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            started = Stopwatch.GetTimestamp();
            failure = await Assert.ThrowsAnyAsync<IOException>(() =>
                ledger.AppendAsync(NewEvent(At(0), ArbiterLedgerPhases.Pre)));
        }

        TimeSpan waited = Stopwatch.GetElapsedTime(started);

        Assert.Contains(".lock", failure.Message, StringComparison.Ordinal);
        Assert.InRange(waited.TotalMilliseconds, 450, 10_000);
        Assert.False(File.Exists(ledger.FilePath), "a failed append must not have written a line");
    }

    // ---- Concurrency (acceptance 2 and 6) ----

    [Fact]
    public async Task ConcurrentAppenders_NeverInterleaveLines()
    {
        // Four concurrent hook-like writers, eight appends each. The sizing is
        // deliberate: every append serializes on .lock, and a writer that waits
        // past the 500 ms hook budget fails closed by design, so the test keeps
        // the serialized work far below that budget even under a loaded runner.
        const int Writers = 4;
        const int AppendsPerWriter = 8;

        DateTimeOffset baseAt = At(0);
        List<Task> writers = [];
        for (int writer = 0; writer < Writers; writer++)
        {
            // One InFlightLedger instance per writer: the mutual exclusion under
            // test is the OS-level lock on .lock (FileShare.None), not any shared
            // in-process state, exactly as it would be between hook processes.
            var ledger = new InFlightLedger(ArbiterDirectory);
            string harness = $"harness-{writer}";
            writers.Add(Task.Run(async () =>
            {
                for (int append = 0; append < AppendsPerWriter; append++)
                {
                    await ledger.AppendAsync(NewEvent(
                        baseAt.AddSeconds((writer * AppendsPerWriter) + append),
                        ArbiterLedgerPhases.Pre,
                        harness: harness,
                        session: null));
                }
            }));
        }

        await Task.WhenAll(writers);

        var events = new InFlightLedger(ArbiterDirectory).ReadAll();
        Assert.Equal(Writers * AppendsPerWriter, events.Count);
        Assert.Equal(Writers * AppendsPerWriter, events.Select(e => e.Id).Distinct().Count());
        Assert.All(events, e =>
        {
            Assert.Equal("kyber-arbiter.ledger/v1", e.Schema);
            Assert.Matches(@"^harness-\d+$", e.Harness);
        });
    }

    // ---- Reads (acceptance 3) ----

    [Fact]
    public async Task Read_IgnoresFinalLineWithoutNewline()
    {
        var ledger = new InFlightLedger(ArbiterDirectory);
        await ledger.AppendAsync(NewEvent(At(0), ArbiterLedgerPhases.Pre));
        await ledger.AppendAsync(NewEvent(At(1), ArbiterLedgerPhases.Post));

        // A crashed writer leaves a torn final line; readers must ignore it.
        await File.AppendAllTextAsync(ledger.FilePath, "{\"schema\":\"kyber-arbiter.ledger/v1");

        Assert.Equal(2, ledger.ReadAll().Count);
    }

    // ---- Pairing (acceptance 4) ----

    [Fact]
    public async Task Pairing_ByCallId()
    {
        var ledger = new InFlightLedger(ArbiterDirectory);
        var pre = NewEvent(At(0), ArbiterLedgerPhases.Pre, callId: "call-1", pairDigest: "digest-a");
        await ledger.AppendAsync(pre);
        var post = NewEvent(At(1), ArbiterLedgerPhases.Post, callId: "call-1", pairDigest: "digest-b");
        await ledger.AppendAsync(post);

        var events = ledger.ReadAll();
        Assert.Equal(pre.Id, events[1].PreId);
        Assert.Equal(pre.Id, ledger.MatchPre(post));
    }

    [Fact]
    public async Task Pairing_ByDigest_OldestUnpairedFirst_CallIdWinsWhenPresent()
    {
        var ledger = new InFlightLedger(ArbiterDirectory);
        var pre1 = NewEvent(At(0), ArbiterLedgerPhases.Pre, pairDigest: "digest-same");
        var pre2 = NewEvent(At(1), ArbiterLedgerPhases.Pre, pairDigest: "digest-same");
        var pre3 = NewEvent(At(2), ArbiterLedgerPhases.Pre, callId: "call-9", pairDigest: "digest-x");
        await ledger.AppendAsync(pre1);
        await ledger.AppendAsync(pre2);
        await ledger.AppendAsync(pre3);

        var post1 = NewEvent(At(3), ArbiterLedgerPhases.Post, pairDigest: "digest-same");
        await ledger.AppendAsync(post1);
        var post2 = NewEvent(At(4), ArbiterLedgerPhases.Post, pairDigest: "digest-same");
        await ledger.AppendAsync(post2);
        var post3 = NewEvent(At(5), ArbiterLedgerPhases.Post, callId: "call-9", pairDigest: "digest-other");
        await ledger.AppendAsync(post3);

        var events = ledger.ReadAll();
        Assert.Equal(pre1.Id, events[3].PreId);
        Assert.Equal(pre2.Id, events[4].PreId);
        Assert.Equal(pre3.Id, events[5].PreId);

        Assert.Null(ledger.MatchPre(NewEvent(At(6), ArbiterLedgerPhases.Post, pairDigest: "digest-unknown")));
    }

    // ---- Queries (acceptance 5) ----

    [Fact]
    public async Task InFlightPaths_ReturnsTaskFilesOfOtherUnpairedDelegatePreEvents()
    {
        var ledger = new InFlightLedger(ArbiterDirectory);
        var a = NewEvent(At(0), ArbiterLedgerPhases.Pre, trigger: "delegate", taskFiles: ["docs/plans/a.md"]);
        var b = NewEvent(At(1), ArbiterLedgerPhases.Pre, trigger: "delegate", taskFiles: ["docs/plans/b.md"]);
        var c = NewEvent(At(2), ArbiterLedgerPhases.Pre, callId: "call-c", trigger: "delegate", taskFiles: ["docs/plans/c.md"]);
        var lens = NewEvent(At(3), ArbiterLedgerPhases.Pre, trigger: "lens.spawn", taskFiles: ["docs/plans/lens.md"]);
        await ledger.AppendAsync(a);
        await ledger.AppendAsync(b);
        await ledger.AppendAsync(c);
        await ledger.AppendAsync(lens);
        await ledger.AppendAsync(NewEvent(At(4), ArbiterLedgerPhases.Post, callId: "call-c"));

        string[] allUnpaired = ["docs/plans/a.md", "docs/plans/b.md"];
        string[] excludingA = ["docs/plans/b.md"];
        Assert.Equal(allUnpaired, ledger.InFlightPaths());
        Assert.Equal(excludingA, ledger.InFlightPaths(excludeEventId: a.Id));
    }

    [Fact]
    public async Task ConcurrentPaths_ReturnsTaskFilesOfOverlappingDispatches()
    {
        var ledger = new InFlightLedger(ArbiterDirectory);
        var pre1 = NewEvent(At(0), ArbiterLedgerPhases.Pre, callId: "c1", taskFiles: ["src/f1.cs"]);
        var pre2 = NewEvent(At(1), ArbiterLedgerPhases.Pre, callId: "c2", taskFiles: ["src/f2.cs"]);
        var pre3 = NewEvent(At(5), ArbiterLedgerPhases.Pre, callId: "c3", taskFiles: ["src/f3.cs"]);
        var pre4 = NewEvent(At(1), ArbiterLedgerPhases.Pre, callId: "c4", taskFiles: ["src/f4.cs"]);
        await ledger.AppendAsync(pre1);
        await ledger.AppendAsync(pre2);
        await ledger.AppendAsync(pre3);
        await ledger.AppendAsync(pre4);
        await ledger.AppendAsync(NewEvent(At(2), ArbiterLedgerPhases.Post, callId: "c1"));
        await ledger.AppendAsync(NewEvent(At(3), ArbiterLedgerPhases.Post, callId: "c2"));
        await ledger.AppendAsync(NewEvent(At(6), ArbiterLedgerPhases.Post, callId: "c3"));

        // [t0,t2] overlaps pre2's [t1,t3] and unpaired pre4 (in flight since t1),
        // not pre3's [t5,t6].
        string[] concurrentWithPre1 = ["src/f2.cs", "src/f4.cs"];
        Assert.Equal(
            concurrentWithPre1,
            ledger.ConcurrentPaths(pre1.Id, ledger.ReadAll().First(e => e.PreId == pre1.Id).Id));

        // An unpaired pre event stays in flight with an open-ended interval —
        // the ledger cannot know a dispatch ended without its return — so pre4
        // also overlaps pre3's later window, while the completed pre1 and pre2
        // dispatches (back at t2 and t3) do not.
        string[] stillInFlightAtPre3 = ["src/f4.cs"];
        Assert.Equal(
            stillInFlightAtPre3,
            ledger.ConcurrentPaths(pre3.Id, ledger.ReadAll().First(e => e.PreId == pre3.Id).Id));
    }

    [Fact]
    public async Task CompletedTasks_CountsPassResultsAndCompleteAttestations()
    {
        var ledger = new InFlightLedger(ArbiterDirectory);
        await ledger.AppendAsync(NewEvent(
            At(0), ArbiterLedgerPhases.Post,
            headers: new ArbiterLedgerHeaders(Task: "T3"),
            returnsMarkers: new ArbiterReturnMarkers(TaskReview: "PASS")));
        await ledger.AppendAsync(NewEvent(
            At(1), ArbiterLedgerPhases.Post,
            headers: new ArbiterLedgerHeaders(Task: "T7"),
            returnsMarkers: new ArbiterReturnMarkers(TaskReview: "FAIL")));
        await ledger.AppendAsync(NewEvent(
            At(2), ArbiterLedgerPhases.Post,
            returnsMarkers: new ArbiterReturnMarkers(Attested: ["T4=complete", "T1=red"])));
        await ledger.AppendAsync(NewEvent(
            At(3), ArbiterLedgerPhases.Post,
            headers: new ArbiterLedgerHeaders(Task: "T9"),
            returnsMarkers: new ArbiterReturnMarkers(Attested: ["T9=red"])));

        string[] completed = ["T3", "T4"];
        Assert.Equal(completed, ledger.CompletedTasks());
    }

    [Fact]
    public async Task RedEvidenceTasks_CountsNonNoneEvidenceAndRedAttestations()
    {
        var ledger = new InFlightLedger(ArbiterDirectory);
        await ledger.AppendAsync(NewEvent(
            At(0), ArbiterLedgerPhases.Post,
            headers: new ArbiterLedgerHeaders(Task: "T2"),
            returnsMarkers: new ArbiterReturnMarkers(RedEvidence: "3 failing tests in GateRunner")));
        await ledger.AppendAsync(NewEvent(
            At(1), ArbiterLedgerPhases.Post,
            headers: new ArbiterLedgerHeaders(Task: "T8"),
            returnsMarkers: new ArbiterReturnMarkers(RedEvidence: "none")));
        await ledger.AppendAsync(NewEvent(
            At(2), ArbiterLedgerPhases.Post,
            returnsMarkers: new ArbiterReturnMarkers(Attested: ["T1=red", "T4=complete"])));

        string[] redEvidence = ["T1", "T2"];
        Assert.Equal(redEvidence, ledger.RedEvidenceTasks());
    }

    [Fact]
    public async Task DecisionLog_Repeat_CountsPriorEscalationsMatchingRuleAndPlanKey()
    {
        var decisions = new DecisionLog(ArbiterDirectory);
        const string rule = "KW-ARB-SCOPE-002";

        Assert.Equal(1, decisions.Repeat(rule, "docs/plans/plan.md", "plan-digest-1", "T3"));

        await decisions.AppendAsync(Decision(
            At(0), RuleEffects.Escalate, [rule]));
        Assert.Equal(2, decisions.Repeat(rule, "docs/plans/plan.md", "plan-digest-1", "T3"));

        await decisions.AppendAsync(Decision(
            At(1), RuleEffects.Escalate, [rule, "KW-ARB-SCOPE-001"]));
        Assert.Equal(3, decisions.Repeat(rule, "docs/plans/plan.md", "plan-digest-1", "T3"));
        Assert.Equal(2, decisions.Repeat("KW-ARB-SCOPE-001", "docs/plans/plan.md", "plan-digest-1", "T3"));

        // A Draft amendment changes the digest and resets the count (R8).
        await decisions.AppendAsync(Decision(
            At(2), RuleEffects.Escalate, [rule], planDigest: "plan-digest-2"));
        Assert.Equal(2, decisions.Repeat(rule, "docs/plans/plan.md", "plan-digest-2", "T3"));
        Assert.Equal(3, decisions.Repeat(rule, "docs/plans/plan.md", "plan-digest-1", "T3"));

        // Only escalations count, and the key is the full (plan, digest, task).
        await decisions.AppendAsync(Decision(
            At(3), RuleEffects.Allow, [rule]));
        await decisions.AppendAsync(Decision(
            At(4), RuleEffects.Escalate, [rule], task: "T4"));
        Assert.Equal(3, decisions.Repeat(rule, "docs/plans/plan.md", "plan-digest-1", "T3"));
        Assert.Equal(2, decisions.Repeat(rule, "docs/plans/plan.md", "plan-digest-1", "T4"));
    }

    // ---- No secrets (acceptance 7) ----

    [Fact]
    public async Task NoSecrets_SentinelSecretNeverAppearsInEitherFile()
    {
        const string secret = "KW-TEST-SENTINEL-SECRET-do-not-log-9f2c";
        string promptBody = $"PLAN_FILE header\nTASK body mentioning {secret}\ncode: var x = \"{secret}\";";
        string digest = Sha256Hex(promptBody);

        var ledger = new InFlightLedger(ArbiterDirectory);
        var decisions = new DecisionLog(ArbiterDirectory);
        var pre = NewEvent(At(0), ArbiterLedgerPhases.Pre, trigger: "delegate", target: "csharp-dev", pairDigest: digest);
        await ledger.AppendAsync(pre);

        ArbiterDecisionRecord decision = Decision(
            At(1), RuleEffects.Escalate, ["KW-ARB-SCOPE-002"], ledgerId: pre.Id) with
        {
            PlanDigest = digest,
        };
        await decisions.AppendAsync(decision);

        string ledgerText = await File.ReadAllTextAsync(ledger.FilePath);
        string decisionsText = await File.ReadAllTextAsync(decisions.FilePath);
        Assert.DoesNotContain(secret, ledgerText, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, decisionsText, StringComparison.Ordinal);
        Assert.DoesNotContain("var x =", ledgerText, StringComparison.Ordinal);
        Assert.Contains(digest, ledgerText, StringComparison.Ordinal);
        Assert.Contains(digest, decisionsText, StringComparison.Ordinal);
    }
}
