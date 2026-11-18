using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using KyberWeave.Core.Configuration;

namespace KyberWeave.Core.Review;

/// <summary>
/// Decides from a run's finished review cycles which cycle is next
/// and which gates it must run again.
/// </summary>
/// <remarks>
/// A cycle is finished when the verdict command wrote its <c>findings.json</c>; a folder without one is a cycle that
/// stopped part way and does not count. A gate is carried only when an earlier cycle passed it and no file changed
/// since that cycle's head matches the gate's <c>include</c> and <c>applies-when</c> paths: the same two filters that
/// decide whether the gate runs at all.
/// </remarks>
public static partial class ReviewCyclePlanning
{
    /// <summary>The file the verdict command writes into a cycle folder.</summary>
    public const string FindingsFile = ReviewCycleRecord.FindingsFileName;

    /// <summary>The file that records which change a cycle reviewed.</summary>
    public const string IdentityFile = "cycle.json";

    /// <summary>The file the gate run writes the gate report to.</summary>
    public const string GatesFile = "gates.json";

    /// <summary>The file that records the commit a cycle reviewed.</summary>
    public const string HeadFile = "head";

    private const string FolderPrefix = "cycle-";
    private const string ScratchpadPrefix = ".agent_scratchpad/";

    private static readonly JsonSerializerOptions IdentityJson = new() { WriteIndented = true };

    /// <summary>
    /// Splits the finished cycles into those that belong to the current change and those that do not. A cycle belongs to
    /// it only when it recorded a branch, a base and a head, its branch equals the current branch, its base equals the
    /// current base, and its head is an ancestor of the current <c>HEAD</c>. When the current change cannot be read, no
    /// cycle belongs to it.
    /// </summary>
    /// <param name="finished">The finished cycles, oldest first.</param>
    /// <param name="current">The current change, or null when git could not name it.</param>
    /// <param name="isAncestorOfHead">Whether a commit is an ancestor of (or is) the current <c>HEAD</c>.</param>
    public static PriorCycles Belonging(
        IReadOnlyList<ReviewCycleRecord> finished,
        ReviewCycleIdentity? current,
        Func<string, bool> isAncestorOfHead)
    {
        ArgumentNullException.ThrowIfNull(finished);
        ArgumentNullException.ThrowIfNull(isAncestorOfHead);

        List<ReviewCycleRecord> valid = [];
        List<IgnoredCycle> ignored = [];
        foreach (ReviewCycleRecord cycle in finished)
        {
            string? reason = NotThisChange(cycle, current, isAncestorOfHead);
            if (reason is null)
                valid.Add(cycle);
            else
                ignored.Add(new IgnoredCycle(cycle, reason));
        }

        return new PriorCycles(valid, ignored);
    }

    private static string? NotThisChange(ReviewCycleRecord cycle, ReviewCycleIdentity? current, Func<string, bool> isAncestorOfHead)
    {
        if (current is null)
            return "the current branch, base or HEAD could not be read, so it cannot be tied to this change";

        if (cycle.Branch is null || cycle.Base is null || cycle.Head is null)
            return $"it recorded no branch, base and head ({IdentityFile}), so it cannot be tied to this change";

        if (!string.Equals(cycle.Branch, current.Branch, StringComparison.Ordinal))
            return $"it reviewed branch {cycle.Branch}, this is {current.Branch}";

        if (!string.Equals(cycle.Base, current.Base, StringComparison.Ordinal))
            return $"its base {Short(cycle.Base)} is not this change's base {Short(current.Base)}";

        return isAncestorOfHead(cycle.Head)
            ? null
            : $"its head {Short(cycle.Head)} is not an ancestor of HEAD {Short(current.Head)}";
    }

    private static string Short(string commit) => commit.Length > 12 ? commit[..12] : commit;

    /// <summary>The text <c>cycle.json</c> holds for <paramref name="identity"/>.</summary>
    public static string SerializeIdentity(ReviewCycleIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return JsonSerializer.Serialize(
            new Dictionary<string, string>
            {
                ["branch"] = identity.Branch,
                ["base"] = identity.Base,
                ["head"] = identity.Head
            },
            IdentityJson);
    }

    /// <summary>The change a cycle folder recorded, or null when it recorded none or the record is incomplete.</summary>
    public static ReviewCycleIdentity? ReadRecordedIdentity(string? identityText)
    {
        (string? branch, string? baseCommit, string? head) = ReadIdentity(identityText);
        return branch is null || baseCommit is null || head is null ? null : new ReviewCycleIdentity(branch, baseCommit, head);
    }

    /// <summary>Decides which cycle is next for a run, and for a later cycle what each gate must do.</summary>
    /// <param name="finished">The finished cycles, oldest first.</param>
    /// <param name="declared">The gates the host declares under <c>review.gates</c>.</param>
    /// <param name="currentOs"><c>windows</c> or <c>unix</c>.</param>
    /// <param name="changedSince">The paths the working tree changed since a commit, or null when they cannot be listed.</param>
    public static ReviewCyclePlan Plan(
        IReadOnlyList<ReviewCycleRecord> finished,
        IReadOnlyList<ReviewGate> declared,
        string currentOs,
        Func<string, IReadOnlyList<string>?> changedSince)
    {
        ArgumentNullException.ThrowIfNull(finished);

        ReviewCycleRecord? prior = finished.Count > 0 ? finished[^1] : null;
        return new ReviewCyclePlan(
            prior is null ? 1 : prior.Number + 1,
            prior,
            prior is null ? [] : DecideGates(finished, declared, currentOs, changedSince));
    }

    /// <summary>Decides, for every declared gate, whether the next cycle runs it again or carries an earlier result.</summary>
    /// <param name="finished">The finished cycles, oldest first; the newest result for a gate is the one considered.</param>
    /// <param name="declared">The gates the host declares under <c>review.gates</c>.</param>
    /// <param name="currentOs"><c>windows</c> or <c>unix</c>.</param>
    /// <param name="changedSince">The paths the working tree changed since a commit, or null when they cannot be listed.</param>
    public static IReadOnlyList<GateCycleDecision> DecideGates(
        IReadOnlyList<ReviewCycleRecord> finished,
        IReadOnlyList<ReviewGate> declared,
        string currentOs,
        Func<string, IReadOnlyList<string>?> changedSince)
    {
        ArgumentNullException.ThrowIfNull(finished);
        ArgumentNullException.ThrowIfNull(declared);
        ArgumentNullException.ThrowIfNull(changedSince);

        if (finished.Count == 0)
            return [];

        Dictionary<string, IReadOnlyList<string>?> deltas = new(StringComparer.Ordinal);
        IReadOnlyList<string>? Delta(string head)
        {
            if (!deltas.TryGetValue(head, out IReadOnlyList<string>? delta))
                deltas[head] = delta = changedSince(head)?.Where(path => !IsScratchpad(path)).ToArray();

            return delta;
        }

        List<GateCycleDecision> decisions = [];
        foreach (ReviewGate gate in declared)
        {
            if (gate.Os is not null && gate.Os != currentOs)
            {
                decisions.Add(new GateCycleDecision(gate.Id, GateCycleAction.NotApplicable, $"it runs only on {gate.Os}"));
                continue;
            }

            (ReviewCycleRecord cycle, GateResult result)? last = LastResult(finished, gate.Id);
            ReviewCycleRecord reference = last?.cycle ?? finished[0];
            int? from = last?.cycle.Number;

            if (last is { } seen && !Settled(seen.result))
            {
                decisions.Add(new GateCycleDecision(
                    gate.Id, GateCycleAction.Rerun, $"it did not pass in cycle {seen.cycle.Number}", from));
                continue;
            }

            if (gate.Include.Count == 0 && gate.AppliesWhen is not { Paths.Count: > 0 })
            {
                decisions.Add(new GateCycleDecision(
                    gate.Id, GateCycleAction.Rerun, "it declares no include, so any change is its input", from));
                continue;
            }

            IReadOnlyList<string>? delta = reference.Head is { } head ? Delta(head) : null;
            if (delta is null)
            {
                decisions.Add(new GateCycleDecision(
                    gate.Id,
                    GateCycleAction.Rerun,
                    reference.Head is null
                        ? $"cycle {reference.Number} recorded no usable head to compare with"
                        : $"the changes since cycle {reference.Number}'s head could not be listed",
                    from));
                continue;
            }

            string? touched = delta.FirstOrDefault(path => IsInput(gate, path));
            if (touched is not null)
            {
                decisions.Add(new GateCycleDecision(
                    gate.Id, GateCycleAction.Rerun, $"{touched} changed since cycle {reference.Number}", from));
                continue;
            }

            decisions.Add(last is { } carried
                ? new GateCycleDecision(
                    gate.Id,
                    GateCycleAction.Carry,
                    $"no input changed since cycle {carried.cycle.Number}",
                    carried.cycle.Number,
                    carried.result)
                : new GateCycleDecision(
                    gate.Id, GateCycleAction.NotApplicable, "no earlier cycle ran it and no input has changed"));
        }

        return decisions;
    }

    /// <summary>Whether a gate that passed, or did not apply, in an earlier cycle can be carried forward.</summary>
    private static bool Settled(GateResult result) =>
        result.NotApplicableReason is not null
        || (result.ExitCode == 0 && result.Outcome == GateOutcome.Completed);

    private static bool IsInput(ReviewGate gate, string path) =>
        (gate.Include.Count == 0 || gate.Include.Any(pattern => PathGlob.IsMatch(pattern, path)))
        && (gate.AppliesWhen is not { Paths.Count: > 0 } applies || applies.Paths.Any(pattern => PathGlob.IsMatch(pattern, path)));

    private static bool IsScratchpad(string path) =>
        path.Replace('\\', '/').StartsWith(ScratchpadPrefix, StringComparison.Ordinal);

    private static (ReviewCycleRecord cycle, GateResult result)? LastResult(
        IReadOnlyList<ReviewCycleRecord> finished,
        string gateId)
    {
        for (int index = finished.Count - 1; index >= 0; index--)
        {
            GateResult? result = finished[index].Gates?.Gates
                .FirstOrDefault(gate => string.Equals(gate.Id, gateId, StringComparison.Ordinal));
            if (result is not null)
                return (finished[index], result);
        }

        return null;
    }

    /// <summary>Builds a cycle's record from the texts of its files; a text that is absent or unreadable is null.</summary>
    /// <param name="number">The cycle number.</param>
    /// <param name="folder">The cycle folder.</param>
    /// <param name="headText">The <c>head</c> file's text.</param>
    /// <param name="identityText">The <c>cycle.json</c> text.</param>
    /// <param name="gatesText">The <c>gates.json</c> text.</param>
    public static ReviewCycleRecord Record(int number, string folder, string? headText, string? identityText, string? gatesText)
    {
        // The id reaches git argv, so only a hexadecimal commit id is accepted.
        string? head = headText is null ? null : CommitId().IsMatch(headText.Trim()) ? headText.Trim() : null;

        (string? branch, string? baseCommit, string? recordedHead) = ReadIdentity(identityText);
        head = CommitId().IsMatch(recordedHead ?? string.Empty) ? recordedHead : head;

        GateReport? gates = null;
        if (gatesText is not null)
        {
            try
            {
                gates = ReviewJson.ReadGates(gatesText);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                gates = null;
            }
        }

        return new ReviewCycleRecord(number, folder, head, gates, branch, baseCommit);
    }

    private static (string? Branch, string? Base, string? Head) ReadIdentity(string? text)
    {
        if (text is null)
            return (null, null, null);

        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            JsonElement root = document.RootElement;
            string? Text(string name) =>
                root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty(name, out JsonElement value)
                && value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(value.GetString())
                    ? value.GetString()
                    : null;

            string? commitBase = Text("base");
            return (Text("branch"), CommitId().IsMatch(commitBase ?? string.Empty) ? commitBase : null, Text("head"));
        }
        catch (JsonException)
        {
            return (null, null, null);
        }
    }

    public static int? TryNumber(string name) =>
        name.StartsWith(FolderPrefix, StringComparison.Ordinal)
        && int.TryParse(name.AsSpan(FolderPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int number)
        && number > 0
            ? number
            : null;

    [GeneratedRegex("^[0-9a-f]{7,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex CommitId();
}
