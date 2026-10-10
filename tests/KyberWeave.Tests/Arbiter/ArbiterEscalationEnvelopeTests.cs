using KyberWeave.Core.Arbiter;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract for task 1.1: byte-exact escalation envelope and review notes
/// (field order, id-ordered multi-rule repetition, all four ANSWER forms,
/// the NEXT table and REPEAT). RED: <see cref="ArbiterEscalationEnvelope"/>
/// does not exist yet.
/// </summary>
public sealed class ArbiterEscalationEnvelopeTests
{
    private static ArbiterEnvelopeEntry Step0Entry(
        string ruleId = "KW-ARB-SCOPE-002",
        string question = "Does the delegation ask for work the plan task does not describe?",
        string answer = "within-task",
        string evidence = "delegation matches T3 exactly") =>
        new(ruleId, question, ArbiterAnswerText.Step0(answer), evidence);

    private static ArbiterEscalationEnvelope ConductorEnvelope(
        IReadOnlyList<ArbiterEnvelopeEntry>? entries = null,
        int repeat = 1,
        string decisionId = "dec-1") =>
        new(
            "delegate",
            "conductor (harness)",
            "csharp-dev",
            "docs/plans/plan.md",
            "T3",
            entries ?? [Step0Entry()],
            decisionId,
            repeat);

    // ---- exact text: single-rule envelope ----

    [Fact]
    public void Render_SingleRuleEnvelope_MatchesByteExactText()
    {
        ArbiterEscalationEnvelope envelope = new(
            "delegate",
            "conductor (harness)",
            "csharp-dev",
            "docs/plans/plan.md",
            "T3",
            [new ArbiterEnvelopeEntry(
                "KW-ARB-SCOPE-002",
                "Does the delegation ask for work the plan task does not describe?",
                ArbiterAnswerText.Step1("adds-work", 0.86, "jev-1.13.0"),
                "delegation asks to \"also refactor GateRunner\"; T3 describes only the plan parser")],
            "dec-9",
            1);
        string expected =
            "STATUS: ARBITER_ESCALATION\n" +
            "TRIGGER: delegate\n" +
            "CALLER: conductor (harness)\n" +
            "TARGET: csharp-dev\n" +
            "PLAN_FILE: docs/plans/plan.md\n" +
            "TASK: T3\n" +
            "RULES: KW-ARB-SCOPE-002\n" +
            "QUESTION: Does the delegation ask for work the plan task does not describe?\n" +
            "ANSWER: adds-work (p=0.86, step 1, jev-1.13.0)\n" +
            "EVIDENCE: delegation asks to \"also refactor GateRunner\"; T3 describes only the plan parser\n" +
            "DECISION_ID: dec-9\n" +
            "REPEAT: 1\n" +
            "NEXT: dispatch architect with this envelope; do not retry this delegation unchanged.";
        Assert.Equal(expected, envelope.Render());
    }

    // ---- multi-rule: id order + repeated triples ----

    [Fact]
    public void Render_MultipleRules_ListsIdsInOrderAndRepeatsTriples()
    {
        ArbiterEscalationEnvelope envelope = ConductorEnvelope(
            [
                Step0Entry("KW-ARB-SCOPE-002", "Second question?", "adds-work", "second evidence"),
                Step0Entry("KW-ARB-SCOPE-001", "First question?", "beyond-files", "first evidence"),
            ]);
        string rendered = envelope.Render();
        string expected =
            "STATUS: ARBITER_ESCALATION\n" +
            "TRIGGER: delegate\n" +
            "CALLER: conductor (harness)\n" +
            "TARGET: csharp-dev\n" +
            "PLAN_FILE: docs/plans/plan.md\n" +
            "TASK: T3\n" +
            "RULES: KW-ARB-SCOPE-001, KW-ARB-SCOPE-002\n" +
            "QUESTION: First question?\n" +
            "ANSWER: beyond-files (step 0)\n" +
            "EVIDENCE: first evidence\n" +
            "QUESTION: Second question?\n" +
            "ANSWER: adds-work (step 0)\n" +
            "EVIDENCE: second evidence\n" +
            "DECISION_ID: dec-1\n" +
            "REPEAT: 1\n" +
            "NEXT: dispatch architect with this envelope; do not retry this delegation unchanged.";
        Assert.Equal(expected, rendered);
    }

    // ---- all four ANSWER forms ----

    [Fact]
    public void AnswerText_Step0_RendersBareAnswerWithStep()
    {
        Assert.Equal("within-task (step 0)", ArbiterAnswerText.Step0("within-task").Render());
    }

    [Fact]
    public void AnswerText_Step1_RendersProbabilityModelAndStep()
    {
        Assert.Equal(
            "adds-work (p=0.86, step 1, jev-1.13.0)",
            ArbiterAnswerText.Step1("adds-work", 0.86, "jev-1.13.0").Render());
    }

    [Fact]
    public void AnswerText_Undecidable_RendersReason()
    {
        Assert.Equal(
            "undecidable (confidence 0.5 below floor 0.8)",
            ArbiterAnswerText.Undecidable("confidence 0.5 below floor 0.8").Render());
    }

    [Fact]
    public void AnswerText_Error_RendersHookCodeAndMessage()
    {
        Assert.Equal(
            "error (KW-ARB-HOOK-001: decision log unavailable)",
            ArbiterAnswerText.Error("KW-ARB-HOOK-001", "decision log unavailable").Render());
    }

    // ---- NEXT table ----

    [Fact]
    public void Next_ConductorRepeatOne_DispatchesArchitect()
    {
        Assert.Equal(
            "dispatch architect with this envelope; do not retry this delegation unchanged.",
            ConductorEnvelope(repeat: 1).RenderNext());
    }

    [Fact]
    public void Next_ConductorRepeatTwoOrMore_StopsArchitectRedispatch()
    {
        Assert.Equal(
            "stop: record this as a run finding; do not dispatch architect again for this task.",
            ConductorEnvelope(repeat: 2).RenderNext());
        Assert.Equal(
            "stop: record this as a run finding; do not dispatch architect again for this task.",
            ConductorEnvelope(repeat: 3).RenderNext());
    }

    [Fact]
    public void Next_PlannerMalformed_ReissuesPlannerDispatch()
    {
        ArbiterEscalationEnvelope envelope = new(
            "delegate.planner",
            "conductor (harness)",
            "product-owner",
            "docs/plans/plan.md",
            "T1",
            [new ArbiterEnvelopeEntry(
                "KW-ARB-PLANNER-001",
                "Does the dispatch carry a recognised marker?",
                ArbiterAnswerText.Step0("malformed"),
                "dispatch has no marker")],
            "dec-2",
            1);
        Assert.Equal(
            "re-issue this planner dispatch with a recognised marker; do not send it to architect.",
            envelope.RenderNext());
        Assert.Contains(
            "NEXT: re-issue this planner dispatch with a recognised marker; do not send it to architect.",
            envelope.Render(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Next_InvestigateKnownCaller_ReportsInOwnResult()
    {
        foreach (string caller in new[] { "architect", "product-owner", "code-reviewer" })
        {
            ArbiterEscalationEnvelope envelope = new(
                "investigate",
                caller,
                null,
                null,
                null,
                [Step0Entry("KW-ARB-INV-001", "Is the question answerable here?", "needs-context", "no sources")],
                "dec-3",
                1);
            Assert.Equal(
                "report this in your own result; do not retry this dispatch unchanged.",
                envelope.RenderNext());
        }
    }

    [Fact]
    public void Next_InvestigateUnidentifiedCaller_ConductorDispatchesArchitect()
    {
        ArbiterEscalationEnvelope envelope = new(
            "investigate",
            "unknown-tool",
            null,
            null,
            null,
            [Step0Entry("KW-ARB-INV-001", "Is the question answerable here?", "needs-context", "no sources")],
            "dec-4",
            1);
        Assert.Equal(
            "the conductor dispatches architect with this envelope; any other caller reports it in its own result.",
            envelope.RenderNext());
    }

    // ---- REPEAT ----

    [Fact]
    public void Render_Repeat_RendersGivenCount()
    {
        Assert.Contains("\nREPEAT: 2\n", ConductorEnvelope(repeat: 2).Render(), StringComparison.Ordinal);
    }

    // ---- review notes ----

    [Fact]
    public void Render_SkipNote_MatchesByteExactText()
    {
        ArbiterSkipNote note = new(
            "infra-workflow",
            ["KW-ARB-LENS-001"],
            ArbiterAnswerText.Step0("not-applicable"),
            "dec-5");
        string expected =
            "STATUS: ARBITER_SKIP\n" +
            "LENS: infra-workflow\n" +
            "RULES: KW-ARB-LENS-001\n" +
            "ANSWER: not-applicable (step 0)\n" +
            "DECISION_ID: dec-5";
        Assert.Equal(expected, note.Render());
    }

    [Fact]
    public void Render_VerifiedNote_CarriesRefuteAndScoreAnswer()
    {
        ArbiterVerifiedNote note = new(
            "correctness/off-by-one",
            ["KW-ARB-REFUTE-001"],
            ArbiterAnswerText.Step1Score("supports", 0.93, "jev-1.13.0"),
            "dec-6");
        string expected =
            "STATUS: ARBITER_VERIFIED\n" +
            "REFUTE: correctness/off-by-one\n" +
            "RULES: KW-ARB-REFUTE-001\n" +
            "ANSWER: supports (c=0.93, step 1, jev-1.13.0)\n" +
            "DECISION_ID: dec-6";
        Assert.Equal(expected, note.Render());
    }

    [Fact]
    public void Render_VerifiedNote_Step0Answer_RendersBareForm()
    {
        ArbiterVerifiedNote note = new(
            "correctness/off-by-one",
            ["KW-ARB-REFUTE-001"],
            ArbiterAnswerText.Step0("corroborated"),
            "dec-6");
        Assert.Contains("ANSWER: corroborated (step 0)", note.Render(), StringComparison.Ordinal);
    }

    [Fact]
    public void Render_AnnotationNote_CarriesFinding()
    {
        ArbiterAnnotationNote note = new(
            "style/naming",
            ["KW-ARB-RETURN-001"],
            ArbiterAnswerText.Step0("annotated"),
            "dec-7");
        string expected =
            "STATUS: ARBITER_ANNOTATION\n" +
            "FINDING: style/naming\n" +
            "RULES: KW-ARB-RETURN-001\n" +
            "ANSWER: annotated (step 0)\n" +
            "DECISION_ID: dec-7";
        Assert.Equal(expected, note.Render());
    }
}
