using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract for task 1.6 (classification): the header grammar and strip rule,
/// target classes from the embedded catalog, caller inference, every
/// classification row and planner marker, the unmarked pass-through without
/// host configuration, and multi-dispatch events.
/// RED: <see cref="HeaderBlock"/>, <see cref="TriggerClassifier"/> and
/// <see cref="ArbiterEvent"/> do not exist yet.
/// </summary>
public sealed class ArbiterClassificationTests
{
    private static ArbiterEvent Dispatch(
        string prompt,
        string? target = "csharp-dev",
        string harness = "antigravity",
        string phase = "pre",
        string? harnessCaller = null,
        string? renderedCaller = null,
        string? assertedCaller = null,
        bool isDispatch = true) =>
        new()
        {
            Harness = harness,
            Phase = phase,
            Target = target,
            Prompt = prompt,
            HarnessCaller = harnessCaller,
            RenderedCaller = renderedCaller,
            AssertedCaller = assertedCaller,
            IsDispatch = isDispatch,
        };

    private static string Marked(string body, string extraHeaders = "") =>
        string.IsNullOrEmpty(extraHeaders)
            ? $"KYBER-ARBITER: true\n\n{body}"
            : $"KYBER-ARBITER: true\n{extraHeaders}\n\n{body}";

    // ---- Header grammar (design section 5) ----

    [Fact]
    public void HeaderGrammar_ClosedSetHasNoFilesHeader()
    {
        HeaderBlock block = HeaderBlock.Parse("FILES: src/a.cs\nKYBER-ARBITER: true\n\nbody");

        // FILES: is not a header, so the block ends before it starts: empty.
        Assert.Empty(block.Headers);
        Assert.Equal(0, block.LineCount);
        Assert.DoesNotContain("FILES", HeaderBlock.ClosedNames);
    }

    [Fact]
    public void HeaderGrammar_ParsesKnownHeadersAtStartOnly()
    {
        HeaderBlock block = HeaderBlock.Parse(
            "KYBER-ARBITER: true\nPLAN_FILE: docs/plans/plan.md\nTASK: T3\n\nDo work.");

        Assert.Equal("true", block.Headers["KYBER-ARBITER"]);
        Assert.Equal("docs/plans/plan.md", block.Headers["PLAN_FILE"]);
        Assert.Equal("T3", block.Headers["TASK"]);
        Assert.Equal(3, block.LineCount);
        Assert.True(block.IsMarked);
    }

    [Fact]
    public void HeaderGrammar_BlockEndsAtFirstNonMatchingLine()
    {
        HeaderBlock block = HeaderBlock.Parse(
            "KYBER-ARBITER: true\nSTATUS: ARBITER_ESCALATION\n\nbody");

        // STATUS: is not a header name, so the block is one line.
        Assert.Single(block.Headers);
        Assert.Equal(1, block.LineCount);
    }

    [Fact]
    public void HeaderGrammar_NamesAreCaseSensitive()
    {
        HeaderBlock block = HeaderBlock.Parse("kyber-arbiter: true\n\nbody");

        Assert.Empty(block.Headers);
        Assert.Equal(0, block.LineCount);
    }

    [Fact]
    public void HeaderGrammar_DuplicateNameLeavesFactAbsent()
    {
        HeaderBlock block = HeaderBlock.Parse("TASK: T1\nTASK: T2\n\nbody");

        Assert.DoesNotContain("TASK", block.Headers.Keys);
        Assert.Equal(2, block.LineCount);
    }

    [Fact]
    public void HeaderGrammar_MalformedValuesLeaveFactAbsent()
    {
        HeaderBlock emptyMarker = HeaderBlock.Parse("KYBER-ARBITER: \n\nbody");
        Assert.DoesNotContain("KYBER-ARBITER", emptyMarker.Headers.Keys);

        HeaderBlock badLens = HeaderBlock.Parse("LENS: not-a-lens\n\nbody");
        Assert.DoesNotContain("LENS", badLens.Headers.Keys);

        HeaderBlock badRefute = HeaderBlock.Parse("REFUTE: security/BAD_SLUG!\n\nbody");
        Assert.DoesNotContain("REFUTE", badRefute.Headers.Keys);

        HeaderBlock badTask = HeaderBlock.Parse("TASK: not a task id!\n\nbody");
        Assert.DoesNotContain("TASK", badTask.Headers.Keys);

        HeaderBlock goodRefute = HeaderBlock.Parse("REFUTE: security/key-in-argv\n\nbody");
        Assert.Equal("security/key-in-argv", goodRefute.Headers["REFUTE"]);
    }

    [Fact]
    public void HeaderStrip_RemovesBlockAndOneBlankLine()
    {
        string prompt = "KYBER-ARBITER: true\nPLAN_FILE: docs/plans/plan.md\n\nDo the work.";
        Assert.Equal("Do the work.", HeaderBlock.Strip(prompt));
    }

    [Fact]
    public void HeaderStrip_WithoutBlockReturnsPromptUnchanged()
    {
        const string Prompt = "Do the work with `src/a.cs`.";
        Assert.Equal(Prompt, HeaderBlock.Strip(Prompt));
    }

    // ---- Target classes (design section 4.1, from the embedded catalog) ----

    [Fact]
    public void TargetClasses_FollowTheCatalogProfiles()
    {
        Assert.Equal("implementation", TriggerClassifier.TargetClassFor("csharp-dev"));
        Assert.Equal("implementation", TriggerClassifier.TargetClassFor("test-dev"));
        Assert.Equal("planner", TriggerClassifier.TargetClassFor("architect"));
        Assert.Equal("planner", TriggerClassifier.TargetClassFor("product-owner"));
        Assert.Equal("read-only", TriggerClassifier.TargetClassFor("task-reviewer"));
        Assert.Equal("read-only", TriggerClassifier.TargetClassFor("code-reviewer"));
        Assert.Equal("read-only", TriggerClassifier.TargetClassFor("review-lens"));
        Assert.Equal("not-squad", TriggerClassifier.TargetClassFor("some-fallback-target"));
        Assert.Equal("not-squad", TriggerClassifier.TargetClassFor(null));
    }

    // ---- Caller resolution (design section 4.2) ----

    [Fact]
    public void CallerInference_PlanFileOrTaskMeansConductor()
    {
        var plan = TriggerClassifier.ClassifySingle(
            Dispatch(Marked("work", "PLAN_FILE: docs/plans/plan.md"), target: "csharp-dev"),
            "csharp-dev",
            Marked("work", "PLAN_FILE: docs/plans/plan.md"));

        Assert.Equal("conductor", plan.Caller);

        var task = TriggerClassifier.ClassifySingle(
            Dispatch(Marked("work", "TASK: T3"), target: "csharp-dev"),
            "csharp-dev",
            Marked("work", "TASK: T3"));

        Assert.Equal("conductor", task.Caller);
    }

    [Fact]
    public void CallerInference_LensOrRefuteMeansCodeReviewer()
    {
        var lens = TriggerClassifier.ClassifySingle(
            Dispatch(Marked("review", "LENS: security"), target: "review-lens"),
            "review-lens",
            Marked("review", "LENS: security"));

        Assert.Equal("code-reviewer", lens.Caller);

        var @refute = TriggerClassifier.ClassifySingle(
            Dispatch(Marked("claim", "REFUTE: security/key-in-argv"), target: "review-lens"),
            "review-lens",
            Marked("claim", "REFUTE: security/key-in-argv"));

        Assert.Equal("code-reviewer", @refute.Caller);
    }

    [Fact]
    public void CallerInference_PlannerTargetMeansConductor()
    {
        TriggerClassification plan = TriggerClassifier.ClassifySingle(
            Dispatch("INTAKE: docs/todo/item.md", target: "architect"),
            "architect",
            "INTAKE: docs/todo/item.md");

        Assert.Equal("conductor", plan.Caller);
    }

    [Fact]
    public void CallerInference_MarkerOnlyStaysUnidentified()
    {
        TriggerClassification plan = TriggerClassifier.ClassifySingle(
            Dispatch(Marked("work"), target: "csharp-dev", harness: "antigravity"),
            "csharp-dev",
            Marked("work"));

        Assert.Null(plan.Caller);
    }

    [Fact]
    public void CallerResolution_HarnessPayloadOutranksRenderedCaller()
    {
        TriggerClassification plan = TriggerClassifier.ClassifySingle(
            Dispatch(
                Marked("work", "PLAN_FILE: docs/plans/plan.md\nTASK: T3"),
                target: "csharp-dev",
                harness: "claude",
                harnessCaller: "conductor",
                renderedCaller: "architect"),
            "csharp-dev",
            Marked("work", "PLAN_FILE: docs/plans/plan.md\nTASK: T3"));

        Assert.Equal("conductor", plan.Caller);
    }

    // ---- Classification rows (design section 4.3) ----

    [Fact]
    public void Classification_ConductorImplementationIsDelegate()
    {
        string prompt = Marked("Implement.", "PLAN_FILE: docs/plans/plan.md\nTASK: T3");
        TriggerClassification pre = TriggerClassifier.ClassifySingle(
            Dispatch(prompt, target: "csharp-dev", harness: "claude", harnessCaller: "conductor"),
            "csharp-dev",
            prompt);

        Assert.Equal("delegate", pre.Trigger);

        TriggerClassification post = TriggerClassifier.ClassifySingle(
            Dispatch(prompt, target: "csharp-dev", harness: "claude", harnessCaller: "conductor", phase: "post"),
            "csharp-dev",
            prompt);

        Assert.Equal("delegate.returned", post.Trigger);
    }

    [Fact]
    public void Classification_ConductorPlannerIsDelegatePlanner()
    {
        string prompt = Marked("INTAKE: docs/todo/item.md", "PLAN_FILE: docs/plans/plan.md");
        TriggerClassification plan = TriggerClassifier.ClassifySingle(
            Dispatch(prompt, target: "architect", harness: "claude", harnessCaller: "conductor"),
            "architect",
            prompt);

        Assert.Equal("delegate.planner", plan.Trigger);
        Assert.NotNull(plan.Marker);
    }

    [Fact]
    public void Classification_ConductorReadOnlyIsInvestigate()
    {
        string prompt = Marked("Review.", "PLAN_FILE: docs/plans/plan.md\nTASK: T3");
        TriggerClassification plan = TriggerClassifier.ClassifySingle(
            Dispatch(prompt, target: "task-reviewer", harness: "claude", harnessCaller: "conductor"),
            "task-reviewer",
            prompt);

        Assert.Equal("investigate", plan.Trigger);
    }

    [Fact]
    public void Classification_CodeReviewerLensIsLensSpawn()
    {
        string prompt = Marked("Apply lens.", "LENS: security");
        TriggerClassification pre = TriggerClassifier.ClassifySingle(
            Dispatch(prompt, target: "review-lens", harness: "claude", harnessCaller: "code-reviewer"),
            "review-lens",
            prompt);

        Assert.Equal("lens.spawn", pre.Trigger);

        TriggerClassification post = TriggerClassifier.ClassifySingle(
            Dispatch(prompt, target: "review-lens", harness: "claude", harnessCaller: "code-reviewer", phase: "return"),
            "review-lens",
            prompt);

        Assert.Equal("lens.returned", post.Trigger);
    }

    [Fact]
    public void Classification_CodeReviewerRefuteIsRefuteSpawn()
    {
        string prompt = Marked("finding yaml", "REFUTE: security/key-in-argv");
        TriggerClassification plan = TriggerClassifier.ClassifySingle(
            Dispatch(prompt, target: "review-lens", harness: "claude", harnessCaller: "code-reviewer"),
            "review-lens",
            prompt);

        Assert.Equal("refute.spawn", plan.Trigger);
    }

    [Fact]
    public void Classification_ArchitectOtherTargetIsInvestigate()
    {
        string prompt = Marked("Look around.");
        TriggerClassification plan = TriggerClassifier.ClassifySingle(
            Dispatch(prompt, target: "csharp-dev", harness: "claude", harnessCaller: "architect"),
            "csharp-dev",
            prompt);

        // An identified caller outside its roster still classifies; ROSTER-001 escalates it.
        Assert.Equal("investigate", plan.Trigger);
    }

    [Fact]
    public void Classification_UnidentifiedMarkedReadOnlyWithoutPlanOrTaskIsInvestigate()
    {
        string prompt = Marked("Look around.");
        TriggerClassification plan = TriggerClassifier.ClassifySingle(
            Dispatch(prompt, target: "research-agent", harness: "antigravity"),
            "research-agent",
            prompt);

        Assert.Equal("investigate", plan.Trigger);
    }

    [Fact]
    public void Classification_PlannerIgnoresTaskHeader()
    {
        string prompt = Marked("INTAKE: docs/todo/item.md", "PLAN_FILE: docs/plans/plan.md\nTASK: T3");
        TriggerClassification plan = TriggerClassifier.ClassifySingle(
            Dispatch(prompt, target: "architect", harness: "claude", harnessCaller: "conductor"),
            "architect",
            prompt);

        Assert.Equal("delegate.planner", plan.Trigger);
    }

    // ---- Planner markers (design section 4.4) ----

    [Theory]
    [InlineData("INTAKE: docs/todo/item.md")]
    [InlineData("FINDINGS:")]
    [InlineData("STATUS: ARBITER_ESCALATION")]
    [InlineData("FINALIZE")]
    public void PlannerMarkers_Recognised(string markerLine)
    {
        string extra = markerLine == "FINALIZE" ? "PLAN_FILE: docs/plans/plan.md" : string.Empty;
        string prompt = string.IsNullOrEmpty(extra)
            ? $"KYBER-ARBITER: true\n\n{markerLine}\n"
            : $"KYBER-ARBITER: true\n{extra}\n\n{markerLine}\n";

        Assert.NotNull(TriggerClassifier.PlannerMarkerFor(prompt, HeaderBlock.Parse(prompt).Headers));
    }

    [Fact]
    public void PlannerMarkers_SpecPhaseNeedsFeatureAndPhase()
    {
        string prompt = "KYBER-ARBITER: true\n\nFEATURE: search\nPHASE: design\n";
        Assert.NotNull(TriggerClassifier.PlannerMarkerFor(prompt, HeaderBlock.Parse(prompt).Headers));

        string reversed = "KYBER-ARBITER: true\n\nPHASE: tasks\nFEATURE: search\n";
        Assert.NotNull(TriggerClassifier.PlannerMarkerFor(reversed, HeaderBlock.Parse(reversed).Headers));

        string oneLine = "KYBER-ARBITER: true\n\nFEATURE: search\n";
        Assert.Null(TriggerClassifier.PlannerMarkerFor(oneLine, HeaderBlock.Parse(oneLine).Headers));
    }

    // ---- Pass-through without host configuration (Req 6.4) ----

    [Fact]
    public void Classification_UnmarkedUnidentifiedPassesWithoutConfig()
    {
        static KyberWeave.Core.Configuration.KyberWeaveConfig Fail() =>
            throw new InvalidOperationException("must not load .kyber-weave/kyber-weave.yml");

        var ev = Dispatch("Do anything with `src/a.cs`.", target: "csharp-dev", harness: "antigravity");
        IReadOnlyList<TriggerClassification> rows = TriggerClassifier.Classify(ev, Fail);

        TriggerClassification single = Assert.Single(rows);
        Assert.Null(single.Trigger);
        Assert.True(single.PassedWithoutConfig);
    }

    [Fact]
    public void Classification_NonDispatchClassifiedWithoutConfig()
    {
        static KyberWeave.Core.Configuration.KyberWeaveConfig Fail() =>
            throw new InvalidOperationException("must not load .kyber-weave/kyber-weave.yml");

        var ev = Dispatch("output text", target: null, isDispatch: false);
        IReadOnlyList<TriggerClassification> rows = TriggerClassifier.Classify(ev, Fail);

        TriggerClassification single = Assert.Single(rows);
        Assert.Null(single.Trigger);
        Assert.True(single.PassedWithoutConfig);
    }

    [Fact]
    public void Classification_MultiDispatchEventClassifiesEach()
    {
        var ev = new ArbiterEvent
        {
            Harness = "antigravity",
            Phase = "pre",
            HarnessCaller = "conductor",
            IsDispatch = true,
            Dispatches =
            [
                new ArbiterDispatch("csharp-dev", Marked("One.", "PLAN_FILE: docs/plans/plan.md\nTASK: T1")),
                new ArbiterDispatch("test-dev", Marked("Two.", "PLAN_FILE: docs/plans/plan.md\nTASK: T2")),
            ],
        };

        IReadOnlyList<TriggerClassification> rows = TriggerClassifier.Classify(ev);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal("delegate", row.Trigger));
        Assert.Equal("csharp-dev", rows[0].Target);
        Assert.Equal("test-dev", rows[1].Target);
    }

    [Fact]
    public void Classification_GateTargetIsGateSelect()
    {
        TriggerClassification plan = TriggerClassifier.ClassifySingle(
            Dispatch("select", target: "gate", harness: "antigravity"),
            "gate",
            "select");

        Assert.Equal("gate.select", plan.Trigger);
    }

    [Fact]
    public void Classification_NoIndexInferenceForPlanIdentity()
    {
        // The body names a plan path, but without headers nothing is inferred.
        TriggerClassification plan = TriggerClassifier.ClassifySingle(
            Dispatch("Read docs/plans/other.md and implement `src/a.cs`.", target: "csharp-dev", harness: "antigravity"),
            "csharp-dev",
            "Read docs/plans/other.md and implement `src/a.cs`.");

        Assert.Null(plan.Caller);
        Assert.DoesNotContain("PLAN_FILE", plan.Headers.Keys);
    }
}
