using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;

namespace KyberWeave.Core.Squad.Rendering;

/// <summary>
/// One Arbiter hook the wiring decided to produce: the target it lowers to, the agent whose
/// dispatches (or planning-path reads) it gates, the exact command line the hook runs, and
/// the timeout it enforces.
/// </summary>
/// <remarks>
/// The wiring decides <em>who</em> is gated and <em>with which command</em>; how a renderer
/// lowers the decision into harness-native form — per-agent frontmatter hooks on Claude and
/// Copilot, or a fold into the single project-level marker for harnesses with no per-agent
/// hooks (D24) — remains the renderer's decision, not this record's.
/// </remarks>
public sealed record SquadArbiterHook(
    string Target,
    string Caller,
    string CommandLine,
    int TimeoutSeconds);

/// <summary>
/// Decides in one place which agents get which Arbiter hooks, with which command line, on
/// which targets, for a loaded Squad source.
/// </summary>
/// <remarks>
/// Per-agent hooks make a caller trusted without a new agent field, which
/// <c>agent.schema.json</c> (<c>additionalProperties: false</c>) would refuse (D22).
/// Dispatchers are exactly the agents with a non-empty <c>delegates-to</c> roster; guarded
/// agents are exactly the implementation specialists carrying the <c>worker</c> or
/// <c>publishing-worker</c> capability profile. docs-dev carries the <c>documentation</c>
/// profile, so Req 25.3 (docs-dev keeps unguarded document access) holds structurally —
/// there is deliberately no name-based carve-out that could silently rot when the roster
/// changes. The target data (<see cref="HookedTargets"/>, <see cref="FallbackTargets"/>,
/// <see cref="TrustSteps"/>) is declared here rather than in the renderers so later phases
/// that extend it are a single edit in this file.
/// </remarks>
public static class ArbiterHookWiring
{
    private const string ArbiterIdentity = "arbiter";
    private const string DegradationCode = "arbiter-not-enforced";
    private const string NoHookSupportReason = "no-hook-support";
    private const string FallbackOnlyReason = "fallback-only";
    private const string GlobalScopeReason = "global-scope";
    private const string CommandName = "kyber-weave-arbiter";

    /// <summary>The shape every agent name and harness token written into a hook command takes.</summary>
    private const string CallerPatternText = "^[A-Za-z0-9][A-Za-z0-9_-]*$";

    private static readonly System.Text.RegularExpressions.Regex CallerPattern =
        new(CallerPatternText, System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>The hooked targets in stable derivation order for <see cref="BuildHooks"/>.</summary>
    private static readonly IReadOnlyList<SquadTarget> HookedSquadTargets =
        [SquadTarget.Claude, SquadTarget.Codex, SquadTarget.Copilot, SquadTarget.Cursor, SquadTarget.OpenCode, SquadTarget.Pi];

    /// <summary>The capability profiles whose agents are implementation specialists.</summary>
    private static readonly HashSet<string> GuardedCapabilityProfiles =
        new(StringComparer.Ordinal) { "worker", "publishing-worker" };

    /// <summary>The targets on which Arbiter hooks are produced today.</summary>
    /// <remarks>
    /// Claude and Copilot in VS Code take per-agent hooks; OpenCode is gated through the
    /// single project-level marker (D24) because it has no per-agent hook primitive. Pi,
    /// Codex and Cursor joined the hooked roster once their hook primitives landed. Which
    /// form each target takes is a renderer decision; membership here only means the target
    /// is enforced, so <see cref="TargetDegradations(IEnumerable{SquadTarget}, SquadDeploymentScope)"/> records nothing for it.
    /// </remarks>
    public static IReadOnlySet<string> HookedTargets { get; } = HookedSquadTargets
        .Select(SquadTargetCatalog.GetToken)
        .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The targets whose only enforcement is the advisory D4 marker fallback (R7).
    /// </summary>
    /// <remarks>
    /// Empty today: no approved target holds the fallback-only role. The set exists so a
    /// future advisory-only target is one edit here, with the degradation kind
    /// (<see cref="FallbackOnlyReason"/>) already derived and tested.
    /// </remarks>
    public static IReadOnlySet<string> FallbackTargets { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>The trust gate a user must grant before hooks run, keyed by target.</summary>
    /// <remarks>
    /// From design §10.7: Claude needs the workspace trust dialog, and a <c>claude -p</c>
    /// session never counts as trusted — headless runs execute no project sub-agent
    /// frontmatter hooks, which is why trust is surfaced at install time where the audit
    /// cannot observe the gap. Copilot in VS Code needs a trusted workspace with
    /// <c>chat.useHooks</c> on. Codex needs a trusted <c>.codex/</c> layer plus a per-hook
    /// review through <c>/hooks</c>, so every <c>squad update</c> that changes the hook
    /// needs trust again. Pi needs project trust so that <c>.pi/extensions/</c> loads
    /// (R17). Only hooked targets with a trust gate appear; OpenCode's marker and
    /// Cursor's hooks carry no trust gate today.
    /// </remarks>
    public static IReadOnlyDictionary<SquadTarget, string> TrustSteps { get; } =
        new Dictionary<SquadTarget, string>
        {
            [SquadTarget.Claude] =
                "Accept the workspace trust dialog for this project so hooks may run. " +
                "Note that `claude -p` sessions never count as trusted and run no project " +
                "sub-agent frontmatter hooks, so headless runs stay unenforced.",
            [SquadTarget.Copilot] =
                "Open this project as a trusted workspace in VS Code and keep " +
                "chat.useHooks enabled, or the hooks stay off.",
            [SquadTarget.Codex] =
                "Trust the project's `.codex/` layer, then review and trust the new hook " +
                "through `/hooks`. Every `squad update` that changes the hook needs that " +
                "review again, or the changed hook is skipped.",
            [SquadTarget.Pi] =
                "Trust the project so that `.pi/extensions/` loads. " +
                "Until trust is granted the extension does not load and dispatches stay ungated."
        };

    /// <summary>Names the dispatcher agents: those with a non-empty <c>delegates-to</c> roster.</summary>
    public static IReadOnlySet<string> Dispatchers(SquadSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        HashSet<string> dispatchers = new(StringComparer.Ordinal);
        foreach (SquadAgent agent in source.Agents)
        {
            if (agent.DelegatesTo.Count > 0)
            {
                dispatchers.Add(agent.Name);
            }
        }

        return dispatchers;
    }

    /// <summary>Names the guarded agents: the implementation specialists in the worker and publishing-worker profiles.</summary>
    public static IReadOnlySet<string> GuardedAgents(SquadSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        HashSet<string> guarded = new(StringComparer.Ordinal);
        foreach (SquadAgent agent in source.Agents)
        {
            if (GuardedCapabilityProfiles.Contains(agent.CapabilityProfile))
            {
                guarded.Add(agent.Name);
            }
        }

        return guarded;
    }

    /// <summary>Builds the hook command line that gates one agent's dispatches on one target.</summary>
    public static string HookCommandLine(SquadTarget target, string caller) =>
        HookCommandLine(SquadTargetCatalog.GetToken(target), caller);

    /// <summary>
    /// Builds the hook command line for one harness token and one caller.
    /// </summary>
    /// <remarks>
    /// The one place an Arbiter hook command is composed, so every renderer that emits a
    /// shell command line shares its validation. The command runs through the harness's
    /// shell, so the caller is checked against a plain agent name before it is written:
    /// a space or a metacharacter would otherwise end the argument and be the shell's to
    /// interpret. Agent names are not attacker-controlled in the normal path — they come
    /// from the canonical Squad data — but a name that cannot be written safely must fail
    /// the render rather than be lowered into something a shell executes.
    /// </remarks>
    /// <param name="harnessToken">The harness token passed to <c>--harness</c>.</param>
    /// <param name="caller">The agent name passed to <c>--caller</c>.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="caller"/> is not <c>^[A-Za-z0-9][A-Za-z0-9_-]*$</c>, or when
    /// <paramref name="harnessToken"/> is not a plain token.
    /// </exception>
    public static string HookCommandLine(string harnessToken, string caller)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(harnessToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(caller);
        if (!CallerPattern.IsMatch(caller))
        {
            throw new ArgumentException(
                $"Arbiter hook caller '{caller}' is not a plain agent name: it must match " +
                $"{CallerPatternText} so it cannot end the argument or reach the shell as a " +
                "metacharacter.",
                nameof(caller));
        }

        if (!CallerPattern.IsMatch(harnessToken))
        {
            throw new ArgumentException(
                $"Arbiter hook harness token '{harnessToken}' is not a plain token: it must " +
                $"match {CallerPatternText}.",
                nameof(harnessToken));
        }

        return $"{CommandName} hook --harness {harnessToken} --caller {caller}";
    }

    /// <summary>Produces the hook decisions for a render, gated on Enabled and Project scope.</summary>
    /// <remarks>
    /// Hooks are produced only when the wiring is enabled and the scope is
    /// <see cref="SquadDeploymentScope.Project"/>: a global install reads no project
    /// configuration, so there is nowhere to enforce from (Req 22.4). Every wired agent —
    /// dispatcher or guarded — gets one hook per hooked target, in stable ordinal order so
    /// renders are deterministic.
    /// </remarks>
    public static IReadOnlyList<SquadArbiterHook> BuildHooks(
        SquadSource source,
        SquadArbiterWiring wiring,
        SquadDeploymentScope scope)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(wiring);

        if (!wiring.Enabled || scope != SquadDeploymentScope.Project)
        {
            return [];
        }

        List<SquadArbiterHook> hooks = [];
        foreach (SquadTarget target in HookedSquadTargets)
        {
            foreach (string caller in Dispatchers(source).Concat(GuardedAgents(source)).Order(StringComparer.Ordinal))
            {
                hooks.Add(new SquadArbiterHook(
                    SquadTargetCatalog.GetToken(target),
                    caller,
                    HookCommandLine(target, caller),
                    wiring.HookTimeoutSeconds));
            }
        }

        return hooks;
    }

    /// <summary>Derives the arbiter-not-enforced degradation records for the requested targets.</summary>
    /// <remarks>
    /// From design §10.5: recorded at render time, per target, never as a runtime
    /// condition. The reason lands in <see cref="SquadDegradationRecord.Details"/> and the
    /// receipt keeps the <see cref="DegradationCode"/>. The records are target-scoped
    /// rather than agent-scoped, so the identity fields carry the arbiter wiring itself and
    /// the instruction digest is empty — no instruction body is involved. An unhooked
    /// target degrades with <c>no-hook-support</c>; a fallback-only target with
    /// <c>fallback-only</c>, where the marker is advisory and planner investigator
    /// dispatches stay ungated (R7); and under global scope every target degrades with
    /// <c>global-scope</c>, hooked or not (Req 22.4).
    /// </remarks>
    public static IReadOnlyList<SquadDegradationRecord> TargetDegradations(
        IEnumerable<SquadTarget> targets,
        SquadDeploymentScope scope) =>
        TargetDegradations(targets, scope, HookedTargets, FallbackTargets);

    /// <summary>The set-parameterized derivation <see cref="TargetDegradations(IEnumerable{SquadTarget}, SquadDeploymentScope)"/> delegates to.</summary>
    /// <remarks>
    /// Internal so the fallback-only kind stays testable while <see cref="FallbackTargets"/>
    /// is empty; the public overload always passes the declared target data.
    /// </remarks>
    internal static IReadOnlyList<SquadDegradationRecord> TargetDegradations(
        IEnumerable<SquadTarget> targets,
        SquadDeploymentScope scope,
        IReadOnlySet<string> hookedTargets,
        IReadOnlySet<string> fallbackTargets)
    {
        ArgumentNullException.ThrowIfNull(targets);

        List<SquadDegradationRecord> records = [];
        foreach (SquadTarget target in targets)
        {
            string token = SquadTargetCatalog.GetToken(target);
            string? reason = DegradationReason(token, scope, hookedTargets, fallbackTargets);
            if (reason is not null)
            {
                records.Add(new SquadDegradationRecord(
                    Target: token,
                    CanonicalIdentity: ArbiterIdentity,
                    OutputIdentity: ArbiterIdentity,
                    Code: DegradationCode,
                    InstructionDigest: string.Empty,
                    Details: reason));
            }
        }

        return records;
    }

    private static string? DegradationReason(
        string token,
        SquadDeploymentScope scope,
        IReadOnlySet<string> hookedTargets,
        IReadOnlySet<string> fallbackTargets)
    {
        if (scope == SquadDeploymentScope.Global)
        {
            return GlobalScopeReason;
        }

        if (fallbackTargets.Contains(token))
        {
            return FallbackOnlyReason;
        }

        return hookedTargets.Contains(token) ? null : NoHookSupportReason;
    }
}
