using System.Text.RegularExpressions;
using KyberWeave.Core.Configuration;

namespace KyberWeave.Core.Arbiter.Facts;

/// <summary>One dispatch classified to its trigger, or passing with no trigger.</summary>
/// <param name="Trigger">The classified trigger, or null when the dispatch passes.</param>
/// <param name="Caller">The resolved caller, or null when unidentified.</param>
/// <param name="CallerSource">How the caller was named (see <see cref="ArbiterCallerSources"/>).</param>
/// <param name="Target">The dispatch target.</param>
/// <param name="TargetClass">The target class from the embedded catalog.</param>
/// <param name="Headers">The valid dispatch headers.</param>
/// <param name="Marker">The planner marker, when the prompt carries one.</param>
/// <param name="PassedWithoutConfig">True: the result required no host configuration.</param>
public sealed record TriggerClassification(
    string? Trigger,
    string? Caller,
    string CallerSource,
    string? Target,
    string TargetClass,
    IReadOnlyDictionary<string, string> Headers,
    string? Marker,
    bool PassedWithoutConfig)
{
    /// <summary>
    /// The raw header values the prompt writes, including the ones <see cref="Headers"/> rejected
    /// (see <see cref="HeaderBlock.RawValues"/>). Read by the plan lookup and the model questions only.
    /// </summary>
    public IReadOnlyDictionary<string, string> RawHeaders { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// Classifies harness-neutral events into triggers (design section 4): target
/// classes, caller resolution, the classification table and planner markers, all
/// from the embedded Squad catalog. The classifier never reads host
/// configuration: an event that is not a dispatch, and an unmarked unidentified
/// dispatch, classify through the optional provider without invoking it.
/// </summary>
/// <remarks>
/// Harness tokens <c>claude</c> and <c>copilot-vscode</c> render frontmatter hooks,
/// so a Squad dispatch there already names its caller; every other token is a
/// project-wide-hook harness where an unmarked dispatch with an unidentified caller
/// passes and is recorded as <c>unmarked</c> (Req 6.4).
/// </remarks>
public static partial class TriggerClassifier
{
    private static readonly IReadOnlySet<string> TrustedHarnesses = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase)
    {
        "claude",
        "copilot-vscode",
    };

    private static readonly IReadOnlySet<string> SpecPhases = new HashSet<string>(StringComparer.Ordinal)
    {
        "requirements",
        "design",
        "tasks",
        "phase-approval",
        "finalization",
    };

    /// <summary>
    /// The marker the conductor writes when it hands the run-start step to a worker, because it has no
    /// shell of its own to run the tool with.
    /// </summary>
    private const string RunStartMarker = "RUN-START";

    /// <summary>
    /// The marker a conductor writes first on a rework dispatch, naming the failure cluster the worker is
    /// reworking. Its <c>FAILURE:</c> section carries the failing output the same-failure rule compares.
    /// </summary>
    internal const string FailureClusterMarker = "FAILURE-CLUSTER";

    /// <summary>Whether <paramref name="harness"/> hooks project-wide rather than per agent.</summary>
    public static bool IsProjectWideHarness(string harness)
    {
        ArgumentNullException.ThrowIfNull(harness);
        return !TrustedHarnesses.Contains(harness);
    }

    /// <summary>Derives the target class from the embedded catalog's capability profile.</summary>
    public static string TargetClassFor(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
            return "not-squad";

        ArbiterSquadCatalog catalog = ArbiterSquadCatalog.LoadEmbedded();
        if (catalog.Agents.TryGetValue(target, out ArbiterSquadAgent? agent))
            return agent.TargetClass;

        return "not-squad";
    }

    /// <summary>
    /// Recognises a planner dispatch by its marker (design section 4.4). Except for
    /// <c>PLAN_FILE</c>, which is itself a header, a marker is the first non-blank
    /// line after the header block. Returns the marker label, or null.
    /// </summary>
    /// <remarks>
    /// <c>RUN-START</c> is the one marker that is not a planner dispatch: it is the conductor handing the
    /// run-start step to a worker, and the classifier reads it so that dispatch is not mistaken for a
    /// delegation.
    /// The plans-directory check on a <c>PLAN_FILE</c> marker is a rule-level path
    /// check against <c>config.planning-dirs</c>, not a classification check: the
    /// classifier reports the header marker and never loads configuration for it.
    /// </remarks>
    public static string? PlannerMarkerFor(string prompt, IReadOnlyDictionary<string, string> headers)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(headers);

        List<string> body = [.. HeaderBlock.Strip(prompt)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)];
        bool hasPlanFile = headers.ContainsKey("PLAN_FILE");
        string? first = body.Count > 0 ? body[0] : null;
        string? second = body.Count > 1 ? body[1] : null;

        if (first is not null)
        {
            if (first.StartsWith("INTAKE:", StringComparison.Ordinal))
                return "INTAKE";
            if (IsRunStartMarker(first))
                return RunStartMarker;
            if (first.StartsWith("FINDINGS:", StringComparison.Ordinal))
                return "FINDINGS";
            if (first.StartsWith(FailureClusterMarker + ":", StringComparison.Ordinal))
                return FailureClusterMarker;
            if (first.StartsWith("STATUS: ARBITER_ESCALATION", StringComparison.Ordinal))
                return "STATUS: ARBITER_ESCALATION";
            if (string.Equals(first, "FINALIZE", StringComparison.Ordinal) && hasPlanFile)
                return "FINALIZE";
            if (IsSpecPhasePair(first, second))
                return "FEATURE/PHASE";
        }

        if (hasPlanFile)
            return "PLAN_FILE";

        return null;
    }

    /// <summary>
    /// Whether <paramref name="line"/> is the <c>RUN-START: true</c> marker. Only the true
    /// value marks a dispatch: a line that carries the name and any other value is not the
    /// conductor asking for the step, and reading it as one would route it as an investigate.
    /// </summary>
    private static bool IsRunStartMarker(string line)
    {
        string prefix = RunStartMarker + ":";
        return line.StartsWith(prefix, StringComparison.Ordinal)
               && string.Equals(line[prefix.Length..].Trim(), "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Classifies every dispatch of <paramref name="ev"/>. The config provider is
    /// accepted so callers can prove the pass-through rows load nothing: it is never
    /// invoked.
    /// </summary>
    public static IReadOnlyList<TriggerClassification> Classify(
        ArbiterEvent ev,
        Func<KyberWeaveConfig>? configProvider = null)
    {
        ArgumentNullException.ThrowIfNull(ev);
        _ = configProvider;

        if (ev.Dispatches.Count > 0)
        {
            List<TriggerClassification> rows = new(ev.Dispatches.Count);
            foreach (ArbiterDispatch dispatch in ev.Dispatches)
                rows.Add(ClassifyOne(ev, dispatch.Target, dispatch.Prompt));
            return rows;
        }

        return [ClassifyOne(ev, ev.Target, ev.Prompt)];
    }

    /// <summary>Classifies one target and prompt with the event's harness and caller.</summary>
    public static TriggerClassification ClassifySingle(
        ArbiterEvent ev,
        string? target,
        string prompt,
        Func<KyberWeaveConfig>? configProvider = null)
    {
        ArgumentNullException.ThrowIfNull(ev);
        ArgumentNullException.ThrowIfNull(prompt);
        _ = configProvider;

        return ClassifyOne(ev, target, prompt);
    }

    private static TriggerClassification ClassifyOne(ArbiterEvent ev, string? target, string prompt) =>
        ClassifyCore(ev, target, prompt) with { RawHeaders = HeaderBlock.Parse(prompt).RawValues };

    private static TriggerClassification ClassifyCore(ArbiterEvent ev, string? target, string prompt)
    {
        HeaderBlock block = HeaderBlock.Parse(prompt);
        IReadOnlyDictionary<string, string> headers = block.Headers;
        string targetClass = TargetClassFor(target);
        string? marker = PlannerMarkerFor(prompt, headers);

        if (IsGateTarget(target))
        {
            (string? caller, string source) = ResolveCaller(ev, target, block, targetClass);
            return new TriggerClassification(
                "gate.select", caller, source, target, targetClass, headers, marker, true);
        }

        if (!ev.IsDispatch)
            return new TriggerClassification(null, null, ArbiterCallerSources.None, target, targetClass, headers, marker, true);

        (string? resolved, string resolvedSource) = ResolveCaller(ev, target, block, targetClass);
        bool marked = headers.ContainsKey("KYBER-ARBITER");
        bool unidentified = resolved is null;

        // A project-wide hook fires for every agent, so an unmarked dispatch from a caller nobody names passes,
        // unless its target is a Squad agent: the catalog is a machine fact, and routing must not depend on the
        // orchestrator volunteering headers.
        if (!marked && unidentified && IsProjectWideHarness(ev.Harness)
            && string.Equals(targetClass, "not-squad", StringComparison.Ordinal))
            return new TriggerClassification(null, null, ArbiterCallerSources.None, target, targetClass, headers, marker, true);

        string? trigger = ClassifyTrigger(resolved, target, targetClass, headers, marker);
        if (trigger is not null && IsReturnPhase(ev.Phase))
            trigger = ToReturned(trigger);

        return new TriggerClassification(trigger, resolved, resolvedSource, target, targetClass, headers, marker, true);
    }

    private static (string? Caller, string Source) ResolveCaller(
        ArbiterEvent ev,
        string? target,
        HeaderBlock block,
        string targetClass)
    {
        IReadOnlyDictionary<string, string> headers = block.Headers;
        string? harnessCaller = ev.HarnessCaller
            ?? (string.Equals(ev.CallerSource, ArbiterCallerSources.Harness, StringComparison.Ordinal) ? ev.Caller : null);
        string? renderedCaller = ev.RenderedCaller
            ?? (string.Equals(ev.CallerSource, ArbiterCallerSources.Rendered, StringComparison.Ordinal) ? ev.Caller : null);
        string? assertedCaller = ev.AssertedCaller
            ?? (string.Equals(ev.CallerSource, ArbiterCallerSources.Asserted, StringComparison.Ordinal) ? ev.Caller : null);

        // An unmarked planner dispatch on a project-wide harness (factory, antigravity) is
        // not the conductor's: those hooks fire for every agent, so the target alone proves
        // nothing about the caller. Marked dispatches and trusted harnesses keep the target
        // inference, both the TARGET header below and the fallback at the end of this method.
        // So does a rejected header: the sender tried to route, and a header that failed
        // validation must not pass where a valid one is gated.
        bool targetInfers = block.IsMarked
            || block.RejectedNames.Count > 0
            || !IsProjectWideHarness(ev.Harness);

        Dictionary<string, string?> resolveHeaders = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> pair in headers)
            resolveHeaders[pair.Key] = pair.Value;
        if (targetInfers && !string.IsNullOrWhiteSpace(target))
            resolveHeaders["TARGET"] = target;

        CallerResolution resolution = CallerResolver.Resolve(harnessCaller, renderedCaller, resolveHeaders, assertedCaller);
        if (resolution.Caller is not null)
        {
            return (resolution.Caller, resolution.Source switch
            {
                CallerSource.Harness => ArbiterCallerSources.Harness,
                CallerSource.Rendered => ArbiterCallerSources.Rendered,
                CallerSource.Header => ArbiterCallerSources.Header,
                CallerSource.Asserted => ArbiterCallerSources.Asserted,
                _ => ArbiterCallerSources.None,
            });
        }

        // Header inference keys on the catalog class, not on a name substring: only
        // the conductor's roster holds planners.
        if (targetInfers && string.Equals(targetClass, "planner", StringComparison.Ordinal))
            return ("conductor", ArbiterCallerSources.Header);

        return (null, ArbiterCallerSources.None);
    }

    private static string? ClassifyTrigger(
        string? caller,
        string? target,
        string targetClass,
        IReadOnlyDictionary<string, string> headers,
        string? marker)
    {
        // RUN-START: true is the conductor handing the run-start step to a worker because it has no shell
        // of its own, so the dispatch carries no PLAN_FILE. KW-ARB-PLAN-001 asks whether a delegate names a
        // plan, and a confident no would refuse the very route the marker exists for; investigate keeps the roster check
        // and KW-ARB-READONLY-001 doing their work over the dispatch instead.
        if (string.Equals(marker, RunStartMarker, StringComparison.Ordinal))
            return "investigate";

        // A rework dispatch (FAILURE-CLUSTER marker) is classified as the ordinary delegation: its failure rule
        // rides as a secondary trigger, derived by SecondaryTriggers.For.
        if (string.Equals(caller, "conductor", StringComparison.Ordinal))
        {
            if (string.Equals(targetClass, "implementation", StringComparison.Ordinal))
                return "delegate";
            if (string.Equals(targetClass, "planner", StringComparison.Ordinal))
                return "delegate.planner";
            if (string.Equals(targetClass, "read-only", StringComparison.Ordinal))
                return "investigate";
            return "investigate";
        }

        if (string.Equals(caller, "code-reviewer", StringComparison.Ordinal))
        {
            bool lensTarget = string.Equals(target, "review-lens", StringComparison.Ordinal)
                || string.Equals(target, "review-triage", StringComparison.Ordinal);
            if (lensTarget && headers.ContainsKey("LENS"))
                return "lens.spawn";
            if (string.Equals(target, "review-lens", StringComparison.Ordinal) && headers.ContainsKey("REFUTE"))
                return "refute.spawn";
            return "investigate";
        }

        if (string.Equals(caller, "architect", StringComparison.Ordinal)
            || string.Equals(caller, "product-owner", StringComparison.Ordinal))
            return "investigate";

        if (caller is null
            && headers.ContainsKey("KYBER-ARBITER")
            && string.Equals(targetClass, "read-only", StringComparison.Ordinal)
            && !headers.ContainsKey("PLAN_FILE")
            && !headers.ContainsKey("TASK"))
            return "investigate";

        // An identified caller outside its roster still classifies as investigate;
        // ROSTER-001 escalates it.
        if (caller is not null)
            return "investigate";

        if (headers.ContainsKey("KYBER-ARBITER"))
            return "investigate";

        // A Squad target with no caller and no headers: plan and task identity are unknown, so only the
        // machine-fact rules have anything to judge, and each of them allows a caller it cannot name.
        if (!string.Equals(targetClass, "not-squad", StringComparison.Ordinal))
            return "investigate";

        return null;
    }

    private static bool IsGateTarget(string? target) =>
        !string.IsNullOrWhiteSpace(target) &&
        (string.Equals(target, "gate", StringComparison.OrdinalIgnoreCase)
            || target.StartsWith("gate:", StringComparison.OrdinalIgnoreCase)
            || target.StartsWith("gate.", StringComparison.OrdinalIgnoreCase));

    private static bool IsReturnPhase(string phase) =>
        string.Equals(phase, "post", StringComparison.OrdinalIgnoreCase)
            || string.Equals(phase, "return", StringComparison.OrdinalIgnoreCase);

    private static string ToReturned(string trigger) => trigger switch
    {
        "delegate" => "delegate.returned",
        "delegate.planner" => "delegate.planner.returned",
        "investigate" => "investigate.returned",
        "lens.spawn" => "lens.returned",
        "refute.spawn" => "refute.returned",
        _ => trigger,
    };

    private static bool IsSpecPhasePair(string first, string? second)
    {
        if (second is null)
            return false;

        bool firstFeature = first.StartsWith("FEATURE:", StringComparison.Ordinal);
        bool firstPhase = first.StartsWith("PHASE:", StringComparison.Ordinal)
            && SpecPhases.Contains(PhaseValue(first));
        bool secondFeature = second.StartsWith("FEATURE:", StringComparison.Ordinal);
        bool secondPhase = second.StartsWith("PHASE:", StringComparison.Ordinal)
            && SpecPhases.Contains(PhaseValue(second));

        return (firstFeature && secondPhase) || (firstPhase && secondFeature);
    }

    private static string PhaseValue(string line)
    {
        Match match = PhaseLineRegex().Match(line);
        return match.Success ? match.Groups["phase"].Value.Trim() : string.Empty;
    }

    [GeneratedRegex(@"^PHASE:[ \t]*(?<phase>\S.*?)[ \t]*$", RegexOptions.CultureInvariant)]
    private static partial Regex PhaseLineRegex();
}
