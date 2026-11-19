using System.Text.Json;
using KyberWeave.Arbiter;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Arbiter.Hooks.Adapters;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Configuration;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// One pairing mechanism for every return path. The payloads are the live ones a Copilot CLI conductor run recorded
/// on 2026-11-19: a turn that started T1 and T2 in one <c>toolCalls</c> batch, two background launch results, and an
/// architect's escalation return whose attestation named tasks and no plan.
/// </summary>
public sealed class ArbiterReturnPairingTests
{
    private const string Plan = "6-Docs/plans/2026-11-18-job-status-dashboard.md";
    private const string T1Call = "call_082a8f1c81d84a759d069b95e63f0e1a";
    private const string T2Call = "call_7dc161d793a14c7092d3f7957e42ed07";

    private static ArbiterLedgerEvent Pre(
        string id, string task, string callId, string plan = Plan, string harness = "copilot-cli") =>
        new(id, new DateTimeOffset(2026, 11, 19, 2, 32, 6, TimeSpan.Zero), "hook", harness, "session", ArbiterLedgerPhases.Pre)
        {
            CallId = callId,
            Target = "dev-hard",
            Trigger = "delegate",
            Headers = new ArbiterLedgerHeaders(plan, task, null, null, null),
            PairDigest = "digest-of-the-dispatch-as-written",
        };

    private static ArbiterLedgerEvent Post(
        string id,
        string? callId,
        string? digest,
        ArbiterLedgerHeaders? headers = null,
        ArbiterReturnMarkers? returns = null,
        string preId = "") =>
        new(id, new DateTimeOffset(2026, 11, 19, 2, 47, 36, TimeSpan.Zero), "hook", "copilot-cli", "session", ArbiterLedgerPhases.Post)
        {
            CallId = callId,
            Target = "dev-hard",
            Trigger = "delegate.returned",
            Headers = headers,
            PairDigest = digest,
            PreId = string.IsNullOrEmpty(preId) ? null : preId,
            Returns = returns,
        };

    /// <summary>
    /// Run 10's case: a batched pre event per worker, two background launches whose post carries the packet the hook
    /// inserted (so its digest differs from the dispatch's), and the workers' passing digests. Both tasks complete.
    /// </summary>
    [Fact]
    public void ParallelBackgroundDispatchesPairByCallId_SoTheirReturnsCompleteBothTasks()
    {
        // Each launch post pairs with its own dispatch: the same rewritten digest on both sides, distinct call ids.
        ArbiterLedgerEvent[] launches =
        [
            Pre("pre-t1", "T1", T1Call),
            Pre("pre-t2", "T2", T2Call),
            Post("launch-t1", T1Call, "digest-of-the-inserted-packet"),
            Post("launch-t2", T2Call, "digest-of-the-inserted-packet"),
        ];
        Assert.Equal("pre-t1", ArbiterLedgerQueries.MatchPre(launches, launches[2]));
        Assert.Equal("pre-t2", ArbiterLedgerQueries.MatchPre(launches, launches[3]));

        // The ledger stores the pair it resolved, so the two workers' passing digests complete T1 and T2.
        ArbiterLedgerEvent[] events =
        [
            Pre("pre-t1", "T1", T1Call),
            Pre("pre-t2", "T2", T2Call),
            Post("return-t1", T1Call, "digest-of-the-inserted-packet", returns: Passing(), preId: "pre-t1"),
            Post("return-t2", T2Call, "digest-of-the-inserted-packet", returns: Passing(), preId: "pre-t2"),
        ];

        Assert.Equal(["T1", "T2"], ArbiterLedgerQueries.CompletedTasks(events, Plan));
    }

    /// <summary>The call id is exact, so a sibling that shares a digest never claims the other's pre event.</summary>
    [Fact]
    public void TheCallIdOutranksTheDigest_SoSiblingsCannotClaimEachOthersDispatch()
    {
        ArbiterLedgerEvent[] events =
        [
            Pre("pre-t1", "T1", T1Call),
            Pre("pre-t2", "T2", T2Call),
            Post("return-t2", T2Call, "digest-of-the-dispatch-as-written", returns: Passing()),
        ];

        Assert.Equal("pre-t2", ArbiterLedgerQueries.MatchPre(events, events[2]));
    }

    /// <summary>
    /// The architect resolved the escalation and attested T1 and T2. Its dispatch opened with
    /// <c>STATUS: ARBITER_ESCALATION</c>, so it has no header block; the plan is the envelope's <c>PLAN_FILE</c> line.
    /// </summary>
    [Fact]
    public void AnAttestationFromAnEscalationReturnCountsForThePlanTheEnvelopeNames()
    {
        const string envelope =
            "STATUS: ARBITER_ESCALATION\nTRIGGER: delegate\nCALLER: conductor (copilot-cli)\nTARGET: architect\n" +
            $"PLAN_FILE: {Plan}\nTASK: T3\nRULES: KW-ARB-READY-001\nQUESTION: Has every task this one depends on finished?";

        Assert.Equal(Plan, ArbiterLedgerQueries.EnvelopePlan(envelope));
        Assert.Null(ArbiterLedgerQueries.EnvelopePlan("KYBER-ARBITER: true\nPLAN_FILE: docs/plans/p.md\n\nDo it."));

        ArbiterLedgerEvent[] events =
        [
            new("pre-escalation", DateTimeOffset.UnixEpoch, "hook", "copilot-cli", "session", ArbiterLedgerPhases.Pre)
            {
                Target = "architect",
                Trigger = "investigate",
                Headers = new ArbiterLedgerHeaders(ArbiterLedgerQueries.EnvelopePlan(envelope), null, null, null, null),
            },
            Post(
                "return-attestation",
                callId: null,
                digest: "digest-of-the-escalation-packet",
                returns: new ArbiterReturnMarkers(PlannerStatus: "ESCALATION_RESOLVED", Attested: ["T1=complete", "T2=complete"]),
                preId: "pre-escalation"),
        ];

        Assert.Equal(["T1", "T2"], ArbiterLedgerQueries.CompletedTasks(events, Plan));
        Assert.Empty(ArbiterLedgerQueries.CompletedTasks(events, "6-Docs/plans/another.md"));
    }

    /// <summary>A return that names no plan is not attributed to a plan; the run filter, not the plan filter, holds it.</summary>
    [Fact]
    public void AnAttestationWithNoPlanAtAllCountsForNoPlan()
    {
        ArbiterLedgerEvent[] events =
        [
            Post(
                "return-attestation",
                callId: null,
                digest: "digest",
                returns: new ArbiterReturnMarkers(Attested: ["T1=complete"])),
        ];

        Assert.Empty(ArbiterLedgerQueries.CompletedTasks(events, Plan));
        Assert.Equal(["T1"], ArbiterLedgerQueries.CompletedTasks(events));
    }

    private static ArbiterReturnMarkers Passing() =>
        new(PlannerStatus: "PASS", TaskGate: "PASS");

}
