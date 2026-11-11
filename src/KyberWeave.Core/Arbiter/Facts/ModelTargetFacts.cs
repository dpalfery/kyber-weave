using KyberWeave.Core.Review;

namespace KyberWeave.Core.Arbiter.Facts;

/// <summary>One file a task touches, tagged with its extension and whether the owner map counts it as a test.</summary>
/// <param name="Extension">The file extension, including its dot (<c>.cs</c>), or empty when the file has none.</param>
/// <param name="Kind"><c>test</c> when the test owner's patterns match the path, otherwise <c>source</c>.</param>
public sealed record ArbiterFileKind(string Extension, string Kind);

/// <summary>The most recent earlier attempt at the same plan task, as the ledger and decision log record it.</summary>
/// <param name="Result"><c>pass</c> or <c>fail</c>, from the attempt's task-review marker.</param>
/// <param name="Profile">The profile the attempt ran on, when one was recorded.</param>
/// <param name="Token">The model token the attempt ran on, when one was recorded.</param>
public sealed record ArbiterPriorAttempt(string Result, string? Profile, string? Token);

/// <summary>
/// Supplies the model-targeting facts the <c>delegate.model</c> rules read: <c>plan.task.file-kinds</c>,
/// <c>ledger.task.prior-attempt</c> and <c>model-targeting.ladder.next</c>. Each fact is filled only when the
/// trigger declared it, so the other triggers' bags keep their shape.
/// </summary>
/// <remarks>
/// The owner map is read from the shipped defaults rather than the host configuration: the test owner's
/// patterns are the one definition of "test file" the Arbiter uses, and a host may not redefine it per
/// trigger. A prior attempt is the latest earlier pass or fail for the same plan and task. Its profile is
/// the decision's delivered model block when there is one, because that is what the harness ran, and the
/// ledger's pre event otherwise.
/// </remarks>
internal static class ModelTargetFacts
{
    private const string FileKinds = "plan.task.file-kinds";
    private const string PriorAttempt = "ledger.task.prior-attempt";
    internal const string PriorResult = "model-targeting.prior-result";
    private const string PriorProfile = "model-targeting.prior-profile";
    private const string LadderNext = "model-targeting.ladder.next";
    private const string TestOwner = "test-dev";

    private static readonly IReadOnlyList<string> TestPatterns = ArbiterConfig.ProductDefaults.OwnerFileKindMap
        .Where(entry => string.Equals(entry.Owner, TestOwner, StringComparison.Ordinal))
        .SelectMany(entry => entry.Patterns)
        .ToList();

    /// <summary>Fills the model-targeting facts the bag declares.</summary>
    /// <param name="facts">The bag, carrying <c>plan.task.files</c> and the delegation facts.</param>
    /// <param name="ledger">The in-flight ledger the prior attempt is read from.</param>
    /// <param name="decisions">The decision log the delivered model block is read from.</param>
    /// <param name="ledgerId">The current event's ledger id, excluded from the prior attempt.</param>
    /// <param name="targeting">The model-targeting block, whose ladder the next rung comes from.</param>
    public static ArbiterFactSet Enrich(
        ArbiterFactSet facts,
        InFlightLedger ledger,
        DecisionLog decisions,
        string ledgerId,
        ArbiterModelTargetingConfig targeting)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(decisions);
        ArgumentNullException.ThrowIfNull(ledgerId);
        ArgumentNullException.ThrowIfNull(targeting);

        if (facts.Contains(FileKinds))
        {
            facts = facts.With(FileKinds, FileKindsOf(facts), ArbiterFactLabel.Derived);
        }

        if (!facts.Contains(PriorAttempt) && !facts.Contains(PriorResult) && !facts.Contains(PriorProfile)
            && !facts.Contains(LadderNext))
        {
            return facts;
        }

        ArbiterPriorAttempt? prior = PriorAttemptOf(facts, ledger, decisions, ledgerId);
        if (facts.Contains(PriorAttempt))
        {
            facts = facts.With(PriorAttempt, prior, ArbiterFactLabel.Derived);
        }

        // A rule can test a fact's presence or equality but not reach into a record's fields, so the
        // result and the profile are also published as facts of their own for the ladder rule to read.
        if (facts.Contains(PriorResult))
        {
            facts = facts.With(PriorResult, prior?.Result, ArbiterFactLabel.Derived);
        }

        if (facts.Contains(PriorProfile))
        {
            facts = facts.With(PriorProfile, prior?.Profile, ArbiterFactLabel.Derived);
        }

        if (facts.Contains(LadderNext))
        {
            facts = facts.With(LadderNext, NextRung(prior?.Profile, targeting.Ladder), ArbiterFactLabel.Derived);
        }

        return facts;
    }

    private static List<ArbiterFileKind>? FileKindsOf(ArbiterFactSet facts)
    {
        if (!facts.TryGet("plan.task.files", out ArbiterFact? files) || files.Value is not IEnumerable<string> paths)
        {
            return null;
        }

        HashSet<ArbiterFileKind> distinct = [];
        List<ArbiterFileKind> kinds = [];
        foreach (string path in paths)
        {
            ArbiterFileKind kind = new(Path.GetExtension(path), IsTest(path) ? "test" : "source");
            if (distinct.Add(kind))
            {
                kinds.Add(kind);
            }
        }

        return kinds;
    }

    private static bool IsTest(string path) =>
        TestPatterns.Any(pattern => PathGlob.IsMatch(pattern, path));

    private static ArbiterPriorAttempt? PriorAttemptOf(
        ArbiterFactSet facts,
        InFlightLedger ledger,
        DecisionLog decisions,
        string ledgerId)
    {
        string? task = Text(facts, "plan.task") ?? Text(facts, "delegation.task");
        if (task is null)
        {
            return null;
        }

        string? planFile = Text(facts, "delegation.plan-file");
        IReadOnlyList<ArbiterLedgerEvent> events = ledger.ReadAll();
        Dictionary<string, ArbiterLedgerEvent> pres = new(StringComparer.Ordinal);
        foreach (ArbiterLedgerEvent entry in events)
        {
            if (entry.Phase == ArbiterLedgerPhases.Pre)
            {
                pres[entry.Id] = entry;
            }
        }

        IReadOnlyList<ArbiterDecisionRecord> records = decisions.ReadAll();
        ArbiterPriorAttempt? latest = null;
        foreach (ArbiterLedgerEvent post in events)
        {
            string? result = post.Returns?.TaskReview;
            if (post.Phase != ArbiterLedgerPhases.Post || post.PreId is null || post.PreId == ledgerId
                || result is not ("PASS" or "FAIL")
                || !pres.TryGetValue(post.PreId, out ArbiterLedgerEvent? pre)
                || !string.Equals(pre.Headers?.Task, task, StringComparison.Ordinal)
                || (planFile is not null && !string.Equals(pre.Headers?.PlanFile, planFile, StringComparison.Ordinal)))
            {
                continue;
            }

            (string? profile, string? token) = RanOn(pre, records);
            latest = new ArbiterPriorAttempt(
                string.Equals(result, "PASS", StringComparison.Ordinal) ? "pass" : "fail",
                profile,
                token);
        }

        return latest;
    }

    /// <summary>The profile and token the attempt ran on: the delivered model block when there is one, else the pre event.</summary>
    private static (string? Profile, string? Token) RanOn(ArbiterLedgerEvent pre, IReadOnlyList<ArbiterDecisionRecord> records)
    {
        foreach (ArbiterDecisionRecord record in records)
        {
            if (string.Equals(record.LedgerId, pre.Id, StringComparison.Ordinal)
                && record.Model is { Delivered: true } block)
            {
                return (block.Profile, block.Token);
            }
        }

        return (pre.ModelProfile, pre.ModelToken);
    }

    /// <summary>The ladder entry one rung above <paramref name="profile"/>, or null at the top or when the profile is off the ladder.</summary>
    private static string? NextRung(string? profile, IReadOnlyList<string> ladder)
    {
        if (profile is null)
        {
            return null;
        }

        for (int index = 0; index + 1 < ladder.Count; index++)
        {
            if (string.Equals(ladder[index], profile, StringComparison.Ordinal))
            {
                return ladder[index + 1];
            }
        }

        return null;
    }

    private static string? Text(ArbiterFactSet facts, string name) =>
        facts.TryGet(name, out ArbiterFact? fact) && fact.Value is string { Length: > 0 } value ? value : null;
}
