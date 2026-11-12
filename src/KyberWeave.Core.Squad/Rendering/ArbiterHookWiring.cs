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
    private const string ProjectOnlyReason = "project-only";
    private const string FallbackOnlyReason = "fallback-only";
    private const string EnforcedGlobalReason = "enforced-global";
    private const string PartialReason = "partial";
    private const string DispatchUnconfirmedReason = "dispatch-unconfirmed";
    internal const string HooksDisabledByHarnessReason = "hooks-disabled-by-harness";
    private const string CommandName = "kyber-weave-arbiter";

    private static readonly HashSet<string> KnownReasonCodes = new(StringComparer.Ordinal)
    {
        ProjectOnlyReason,
        FallbackOnlyReason,
        EnforcedGlobalReason,
        PartialReason,
        DispatchUnconfirmedReason,
        HooksDisabledByHarnessReason
    };

    /// <summary>The shape every agent name and harness token written into a hook command takes.</summary>
    private const string CallerPatternText = "^[A-Za-z0-9][A-Za-z0-9_-]*$";

    private static readonly System.Text.RegularExpressions.Regex CallerPattern =
        new(CallerPatternText, System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>The hooked targets in stable derivation order for <see cref="BuildHooks"/>.</summary>
    private static readonly IReadOnlyList<SquadTarget> HookedSquadTargets =
        [SquadTarget.Claude, SquadTarget.Codex, SquadTarget.Copilot, SquadTarget.Cursor, SquadTarget.OpenCode, SquadTarget.Pi,
         SquadTarget.Kilo, SquadTarget.Antigravity, SquadTarget.Factory, SquadTarget.Devin];

    /// <summary>The fallback-only targets in stable derivation order for <see cref="FallbackTargets"/>.</summary>
    private static readonly IReadOnlyList<SquadTarget> FallbackSquadTargets =
        [SquadTarget.Warp, SquadTarget.ZCode];

    /// <summary>The capability profiles whose agents are implementation specialists.</summary>
    private static readonly HashSet<string> GuardedCapabilityProfiles =
        new(StringComparer.Ordinal) { "worker", "publishing-worker" };

    /// <summary>The capability profiles whose agents' Bash and PowerShell calls the architect shell guard checks.</summary>
    private static readonly HashSet<string> ShellGuardedCapabilityProfiles =
        new(StringComparer.Ordinal) { "architect" };

    /// <summary>The targets on which Arbiter hooks are produced today.</summary>
    /// <remarks>
    /// Claude and Copilot in VS Code take per-agent hooks; OpenCode is gated through the
    /// single project-level marker (D24) because it has no per-agent hook primitive. Pi,
    /// Codex and Cursor joined the hooked roster once their hook primitives landed, and
    /// Kilo, Antigravity, Factory and Devin joined in Phase 3. Which form each target takes
    /// is a renderer decision; membership here only means the target is enforced, so
    /// <see cref="TargetDegradations(IEnumerable{SquadTarget}, SquadDeploymentScope)"/> records nothing for it.
    /// </remarks>
    public static IReadOnlySet<string> HookedTargets { get; } = HookedSquadTargets
        .Select(SquadTargetCatalog.GetToken)
        .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The targets whose only enforcement is the advisory D4 marker fallback (R7).
    /// </summary>
    /// <remarks>
    /// Warp and ZCode hold the fallback-only role: they have no hook primitive Squad can
    /// write, so the marker is advisory and planner investigator dispatches stay ungated
    /// there (R7). Because Squad also writes no MCP configuration for either (Req 6.5),
    /// enforcement only begins once the user registers the arbiter server themselves — the
    /// step <see cref="TrustSteps"/> carries for them.
    /// </remarks>
    public static IReadOnlySet<string> FallbackTargets { get; } = FallbackSquadTargets
        .Select(SquadTargetCatalog.GetToken)
        .ToHashSet(StringComparer.Ordinal);

    /// <summary>The trust gate a user must grant — or the setup step they must take —
    /// before a target enforces, keyed by target.</summary>
    /// <remarks>
    /// From design §10.7: Claude needs the workspace trust dialog, and a <c>claude -p</c>
    /// session never counts as trusted — headless runs execute no project sub-agent
    /// frontmatter hooks, which is why trust is surfaced at install time where the audit
    /// cannot observe the gap. Copilot in VS Code needs a trusted workspace with
    /// <c>chat.useHooks</c> on. Codex needs a trusted <c>.codex/</c> layer plus a per-hook
    /// review through <c>/hooks</c>, so every <c>squad update</c> that changes the hook
    /// needs trust again. Pi needs project trust so that <c>.pi/extensions/</c> loads
    /// (R17). Warp and ZCode carry no trust gate — they have no hooks to trust — but their
    /// setup step appears here too (Req 6.5) so that <c>squad install</c> and
    /// <c>squad update</c> print it, the same mitigation §10.7 prescribes. OpenCode's
    /// marker and Cursor's hooks carry no trust gate today.
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
                "Until trust is granted the extension does not load and dispatches stay ungated.",
            [SquadTarget.Warp] =
                "Squad writes no MCP configuration for Warp, so register the `kyber-weave-arbiter " +
                "serve --repo-root <root>` server in Warp's own MCP configuration at `~/.warp/.mcp.json`. " +
                "Until it is registered only the advisory marker fallback enforces there.",
            [SquadTarget.ZCode] =
                "Squad writes no MCP configuration for ZCode, so register the `kyber-weave-arbiter " +
                "serve --repo-root <root>` server in ZCode's own MCP configuration at `~/.zcode/.mcp.json`. " +
                "Until it is registered only the advisory marker fallback enforces there."
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

    /// <summary>
    /// Names the shell-guarded agents: the agents in the <c>architect</c> capability profile, whose Bash and
    /// PowerShell calls the architect shell guard (<c>KW-ARB-SHELL-001</c>) checks against the documented list.
    /// </summary>
    /// <remarks>
    /// Shell-guarded is a separate set from <see cref="GuardedAgents"/>: the read guard covers the
    /// planning-path reads of implementation specialists, and the shell guard covers the architect's
    /// command boundary. The two sets do not overlap in the shipped roster.
    /// </remarks>
    public static IReadOnlySet<string> ShellGuardedAgents(SquadSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        HashSet<string> shellGuarded = new(StringComparer.Ordinal);
        foreach (SquadAgent agent in source.Agents)
        {
            if (ShellGuardedCapabilityProfiles.Contains(agent.CapabilityProfile))
            {
                shellGuarded.Add(agent.Name);
            }
        }

        return shellGuarded;
    }

    /// <summary>Builds the hook command line that gates one agent's dispatches on one target.</summary>
    public static string HookCommandLine(SquadTarget target, string caller, SquadDeploymentScope scope = SquadDeploymentScope.Project) =>
        HookCommandLine(SquadTargetCatalog.GetToken(target), caller, scope);

    /// <summary>Builds the global or project-scoped hook command line for a target token.</summary>
    public static string HookCommandLine(SquadTarget target, SquadDeploymentScope scope = SquadDeploymentScope.Project) =>
        HookCommandLine(SquadTargetCatalog.GetToken(target), scope);

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
    /// <param name="scope">The deployment scope, which decides whether the command carries the global <c>--scope</c> flag.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="caller"/> is not <c>^[A-Za-z0-9][A-Za-z0-9_-]*$</c>, or when
    /// <paramref name="harnessToken"/> is not a plain token.
    /// </exception>
    public static string HookCommandLine(string harnessToken, string caller, SquadDeploymentScope scope = SquadDeploymentScope.Project)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(caller);
        if (!CallerPattern.IsMatch(caller))
        {
            throw new ArgumentException(
                $"Arbiter hook caller '{caller}' is not a plain agent name: it must match " +
                $"{CallerPatternText} so it cannot end the argument or reach the shell as a " +
                "metacharacter.",
                nameof(caller));
        }

        ValidateHarnessToken(harnessToken);
        return AppendScope($"{CommandName} hook --harness {harnessToken} --caller {caller}", scope);
    }

    /// <summary>Builds the project-wide hook command line, which carries no caller.</summary>
    /// <param name="harnessToken">The harness token passed to <c>--harness</c>.</param>
    /// <param name="scope">The deployment scope, which decides whether the command carries the global <c>--scope</c> flag.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="harnessToken"/> is not a plain token.</exception>
    public static string HookCommandLine(string harnessToken, SquadDeploymentScope scope = SquadDeploymentScope.Project)
    {
        ValidateHarnessToken(harnessToken);
        return AppendScope($"{CommandName} hook --harness {harnessToken}", scope);
    }

    /// <summary>Builds the Copilot conductor guard command line.</summary>
    /// <param name="allowedMcpServer">An MCP server whose tools the conductor may call, or null.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="allowedMcpServer"/> is not a valid server name.</exception>
    public static string GuardCommandLine(string? allowedMcpServer = null)
    {
        if (allowedMcpServer is null)
        {
            return $"{CommandName} guard";
        }

        if (!SquadOperatorChannel.IsValidServerName(allowedMcpServer))
        {
            throw new ArgumentException(
                $"Operator channel server name '{allowedMcpServer}' cannot be written safely into a hook command.",
                nameof(allowedMcpServer));
        }

        return $"{CommandName} guard --allow-mcp {allowedMcpServer}";
    }

    private static string AppendScope(string commandLine, SquadDeploymentScope scope)
    {
        return scope == SquadDeploymentScope.Global
            ? $"{commandLine} --scope global"
            : commandLine;
    }

    private static void ValidateHarnessToken(string harnessToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(harnessToken);
        if (!CallerPattern.IsMatch(harnessToken))
        {
            throw new ArgumentException(
                $"Arbiter hook harness token '{harnessToken}' is not a plain token: it must " +
                $"match {CallerPatternText}.",
                nameof(harnessToken));
        }
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
    /// the instruction digest is empty — no instruction body is involved. A project-scoped
    /// target that cannot enforce hooks degrades with <c>project-only</c>, while a
    /// fallback-only target records <c>fallback-only</c> and planner investigator dispatches
    /// stay ungated (R7). Under global scope, the decision is a tiered enforcement record,
    /// not a blanket scope flag (Req 22.4).
    /// </remarks>
    public static IReadOnlyList<SquadDegradationRecord> TargetDegradations(
        IEnumerable<SquadTarget> targets,
        SquadDeploymentScope scope)
    {
        List<SquadDegradationRecord> records = [];
        foreach (SquadTarget target in targets)
        {
            string token = SquadTargetCatalog.GetToken(target);
            string? reason = DegradationReason(token, scope, HookedTargets, FallbackTargets);
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

    /// <summary>The set-parameterized derivation <see cref="TargetDegradations(IEnumerable{SquadTarget}, SquadDeploymentScope)"/> delegates to.</summary>
    /// <remarks>
    /// Internal so classification can also be pinned against hypothetical rosters —
    /// membership alone decides the record kind; the public overload always passes the
    /// declared target data.
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
            string? reason = NormalizeReason(DegradationReason(token, scope, hookedTargets, fallbackTargets));
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

    internal static string? DegradationReason(
        string token,
        SquadDeploymentScope scope,
        IReadOnlySet<string> hookedTargets,
        IReadOnlySet<string> fallbackTargets)
    {
        if (scope == SquadDeploymentScope.Global)
        {
            if (fallbackTargets.Contains(token))
            {
                return FallbackOnlyReason;
            }

            if (token == "pi")
            {
                return PartialReason;
            }

            if (token is "commandcode" or "opencode" or "kilo")
            {
                return DispatchUnconfirmedReason;
            }

            if (token is "copilot-vscode")
            {
                return HooksDisabledByHarnessReason;
            }

            return EnforcedGlobalReason;
        }

        if (fallbackTargets.Contains(token))
        {
            return FallbackOnlyReason;
        }

        if (token == "commandcode")
        {
            return DispatchUnconfirmedReason;
        }

        return hookedTargets.Contains(token) ? null : ProjectOnlyReason;
    }

    private static string? NormalizeReason(string? reason)
    {
        if (reason is not null && KnownReasonCodes.Contains(reason))
        {
            return reason;
        }

        return null;
    }
}
