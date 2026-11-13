using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using KyberWeave.Core.Configuration;

namespace KyberWeave.Core.Review;

/// <summary>One finished review cycle, as its folder records it.</summary>
/// <param name="Number">The cycle number, from the folder name <c>cycle-&lt;n&gt;</c>.</param>
/// <param name="Folder">Absolute path of the cycle folder.</param>
/// <param name="Head">The commit the cycle reviewed, from its <c>head</c> file; null when the file is absent or not a commit id.</param>
/// <param name="Gates">The cycle's gate report, or null when it wrote none or it cannot be read.</param>
/// <param name="Branch">The branch the cycle reviewed, from <c>cycle.json</c>; null when none was recorded.</param>
/// <param name="Base">The merge-base commit of the change the cycle reviewed, from <c>cycle.json</c>; null when none was recorded.</param>
public sealed record ReviewCycleRecord(
    int Number, string Folder, string? Head, GateReport? Gates, string? Branch = null, string? Base = null)
{
    /// <summary>The findings document the verdict command wrote for this cycle.</summary>
    public string FindingsPath => Path.Combine(Folder, ReviewCycles.FindingsFile);
}

/// <summary>The facts that tie a review cycle to one change: the branch, the commit the change forked from, and the commit reviewed.</summary>
/// <param name="Branch">The checked-out branch.</param>
/// <param name="Base">The merge-base commit of the base ref and <c>HEAD</c>.</param>
/// <param name="Head">The commit <c>HEAD</c> named.</param>
public sealed record ReviewCycleIdentity(string Branch, string Base, string Head);

/// <summary>A recorded cycle that does not belong to the current change, and the fact that says so.</summary>
/// <param name="Record">The cycle that was set aside.</param>
/// <param name="Reason">Why it does not belong, in one phrase.</param>
public sealed record IgnoredCycle(ReviewCycleRecord Record, string Reason);

/// <summary>The finished cycles that belong to the current change, and those that do not.</summary>
/// <param name="Valid">The cycles that count as prior, oldest first.</param>
/// <param name="Ignored">The cycles set aside, oldest first.</param>
public sealed record PriorCycles(IReadOnlyList<ReviewCycleRecord> Valid, IReadOnlyList<IgnoredCycle> Ignored);

/// <summary>What a cycle does about one declared gate.</summary>
public enum GateCycleAction
{
    /// <summary>The gate runs again: it failed, did not finish, or its inputs changed.</summary>
    Rerun,

    /// <summary>An earlier cycle's passing result still stands because no changed file is one of the gate's inputs.</summary>
    Carry,

    /// <summary>The gate does not apply: another operating system, or it has never applied to this change.</summary>
    NotApplicable
}

/// <summary>The decision for one gate, with the machine fact behind it.</summary>
/// <param name="GateId">The declared gate id.</param>
/// <param name="Action">What the cycle does with the gate.</param>
/// <param name="Reason">The fact the decision rests on, in one phrase.</param>
/// <param name="FromCycle">The cycle whose result is carried or was last seen, when there is one.</param>
/// <param name="Carried">The earlier result, when <see cref="Action"/> is <see cref="GateCycleAction.Carry"/>.</param>
public sealed record GateCycleDecision(
    string GateId,
    GateCycleAction Action,
    string Reason,
    int? FromCycle = null,
    GateResult? Carried = null);

/// <summary>Which cycle a review is in, and what the fix since the last cycle leaves to do.</summary>
/// <param name="Number">The cycle about to run: one more than the last finished cycle.</param>
/// <param name="Prior">The last finished cycle, or null when this is the first.</param>
/// <param name="Gates">What to do with each declared gate; empty in the first cycle.</param>
public sealed record ReviewCyclePlan(int Number, ReviewCycleRecord? Prior, IReadOnlyList<GateCycleDecision> Gates)
{
    /// <summary>Whether this cycle re-checks the prior findings instead of reviewing the change in full.</summary>
    public bool IsVerifier => Prior is not null;
}

/// <summary>
/// Reads a run's finished review cycles from the folders the council wrote, and decides from them which cycle is next
/// and which gates it must run again.
/// </summary>
/// <remarks>
/// A cycle is finished when the verdict command wrote its <c>findings.json</c>; a folder without one is a cycle that
/// stopped part way and does not count. A gate is carried only when an earlier cycle passed it and no file changed
/// since that cycle's head matches the gate's <c>include</c> and <c>applies-when</c> paths: the same two filters that
/// decide whether the gate runs at all.
/// </remarks>
public static partial class ReviewCycles
{
    /// <summary>The file the verdict command writes into a cycle folder.</summary>
    public const string FindingsFile = "findings.json";

    /// <summary>The file that records which change a cycle reviewed.</summary>
    public const string IdentityFile = "cycle.json";

    private const string GatesFile = "gates.json";
    private const string HeadFile = "head";
    private const string FolderPrefix = "cycle-";
    private const string ScratchpadPrefix = ".agent_scratchpad/";

    private static readonly JsonSerializerOptions IdentityJson = new() { WriteIndented = true };

    /// <summary>Lists the finished cycles under a review folder, oldest first.</summary>
    /// <param name="reviewFolder"><c>.agent_scratchpad/&lt;task-id&gt;/review</c>.</param>
    public static IReadOnlyList<ReviewCycleRecord> ReadFinished(string reviewFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reviewFolder);

        if (!Directory.Exists(reviewFolder))
            return [];

        List<ReviewCycleRecord> cycles = [];
        foreach (string folder in Directory.EnumerateDirectories(reviewFolder))
        {
            if (TryNumber(Path.GetFileName(folder)) is not { } number || !File.Exists(Path.Combine(folder, FindingsFile)))
                continue;

            cycles.Add(Read(number, folder));
        }

        return [.. cycles.OrderBy(cycle => cycle.Number)];
    }

    /// <summary>
    /// Lists the cycles up to and including <paramref name="priorFolder"/>, oldest first: that folder and, when it is
    /// named <c>cycle-&lt;n&gt;</c>, the finished cycles before it in the same parent.
    /// </summary>
    public static IReadOnlyList<ReviewCycleRecord> ReadThrough(string priorFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(priorFolder);

        string full = Path.GetFullPath(priorFolder);
        if (TryNumber(Path.GetFileName(full)) is not { } number)
            return Directory.Exists(full) ? [Read(0, full)] : [];

        string parent = Path.GetDirectoryName(full) ?? full;
        return [.. ReadFinished(parent).Where(cycle => cycle.Number < number), Read(number, full)];
    }

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

    /// <summary>Records which change a cycle reviewed in <c>cycle.json</c>, replacing an earlier record.</summary>
    /// <param name="cycleFolder">The <c>cycle-&lt;n&gt;</c> folder; created when missing.</param>
    /// <param name="identity">The change being reviewed.</param>
    public static void WriteIdentity(string cycleFolder, ReviewCycleIdentity identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cycleFolder);
        ArgumentNullException.ThrowIfNull(identity);

        Directory.CreateDirectory(cycleFolder);
        File.WriteAllText(
            Path.Combine(cycleFolder, IdentityFile),
            JsonSerializer.Serialize(
                new Dictionary<string, string>
                {
                    ["branch"] = identity.Branch,
                    ["base"] = identity.Base,
                    ["head"] = identity.Head
                },
                IdentityJson));
    }

    /// <summary>Whether a cycle folder already records which change it reviewed.</summary>
    public static bool HasIdentity(string cycleFolder) => File.Exists(Path.Combine(cycleFolder, IdentityFile));

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

    private static ReviewCycleRecord Read(int number, string folder)
    {
        string? head = null;
        string headPath = Path.Combine(folder, HeadFile);
        if (File.Exists(headPath))
        {
            // The id reaches git argv, so only a hexadecimal commit id is accepted.
            string text = File.ReadAllText(headPath).Trim();
            head = CommitId().IsMatch(text) ? text : null;
        }

        (string? branch, string? baseCommit, string? recordedHead) = ReadIdentity(folder);
        head = CommitId().IsMatch(recordedHead ?? string.Empty) ? recordedHead : head;

        GateReport? gates = null;
        string gatesPath = Path.Combine(folder, GatesFile);
        if (File.Exists(gatesPath))
        {
            try
            {
                gates = ReviewJson.ReadGates(File.ReadAllText(gatesPath));
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException
                                           or InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                gates = null;
            }
        }

        return new ReviewCycleRecord(number, folder, head, gates, branch, baseCommit);
    }

    private static (string? Branch, string? Base, string? Head) ReadIdentity(string folder)
    {
        string path = Path.Combine(folder, IdentityFile);
        if (!File.Exists(path))
            return (null, null, null);

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
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
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return (null, null, null);
        }
    }

    private static int? TryNumber(string name) =>
        name.StartsWith(FolderPrefix, StringComparison.Ordinal)
        && int.TryParse(name.AsSpan(FolderPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int number)
        && number > 0
            ? number
            : null;

    [GeneratedRegex("^[0-9a-f]{7,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex CommitId();
}
