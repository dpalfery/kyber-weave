using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Rendering;

namespace KyberWeave.Contracts.Services;

/// <summary>What <c>arbiter install</c> was asked to do, after the host has validated the arguments.</summary>
/// <param name="RepositoryPath">The positional path the command was run with.</param>
/// <param name="PathOption">The <c>--path</c> value, which only a project install uses; null otherwise.</param>
/// <param name="ProjectScope">True for a project install, false for the default global one.</param>
/// <param name="TargetToken">The harness token, already known to the target catalogue.</param>
/// <param name="ConfigOption">The <c>--config</c> path, or null.</param>
/// <param name="ProviderOption">The raw <c>--provider</c> value, or null when omitted.</param>
/// <param name="ArbiterKey">The <c>--arbiter-key</c> value, or null.</param>
/// <param name="KeyStdin">Whether the key is read from standard input.</param>
/// <param name="DryRun">Whether to plan without writing.</param>
public sealed record ArbiterInstallRequest(
    string RepositoryPath,
    string? PathOption,
    bool ProjectScope,
    string TargetToken,
    string? ConfigOption,
    string? ProviderOption,
    string? ArbiterKey,
    bool KeyStdin,
    bool DryRun);

/// <summary>Everything an install has decided before it writes anything.</summary>
/// <param name="Root">The resolved deployment root.</param>
/// <param name="Scope">Project or global.</param>
/// <param name="Target">The harness to install.</param>
/// <param name="TargetToken">The harness token.</param>
/// <param name="DryRun">Whether this is a plan-only run.</param>
/// <param name="ConfigPath">The host config file the run reads and writes.</param>
/// <param name="ConfigOption">The <c>--config</c> path, or null, kept for the loads the run performs after writing.</param>
/// <param name="DesiredConfigText">The host config text the run means to write.</param>
/// <param name="ConfigChanged">Whether that text differs from what is on disk.</param>
/// <param name="KeyWriteNeeded">Whether a key has to be stored.</param>
/// <param name="KeyToStore">The key to store, or null when none is written.</param>
/// <param name="Origin">The origin the key is stored under.</param>
/// <param name="NeedsKey">Whether the provider needs a key at all.</param>
/// <param name="Endpoint">The provider endpoint the run resolves.</param>
/// <param name="ExistingReceipt">The receipt already recorded for the root, or null.</param>
public sealed record ArbiterInstallPlan(
    string Root,
    SquadDeploymentScope Scope,
    SquadTarget Target,
    string TargetToken,
    bool DryRun,
    string ConfigPath,
    string? ConfigOption,
    string DesiredConfigText,
    bool ConfigChanged,
    bool KeyWriteNeeded,
    string? KeyToStore,
    string Origin,
    bool NeedsKey,
    string Endpoint,
    SquadReceipt? ExistingReceipt);

/// <summary>One output event of an install, in the order the host prints them.</summary>
public abstract record ArbiterInstallEvent;

/// <summary>The provider choice the run settled on.</summary>
/// <param name="KindName">The provider kind's name.</param>
/// <param name="Kept">Whether <c>--provider</c> was omitted, so the existing kind was kept.</param>
/// <param name="ChangedFrom">The kind the host config held before, when the run changes it.</param>
/// <param name="OverrideNote">The note about the per-user override, or null.</param>
public sealed record ArbiterInstallProviderLine(
    string KindName,
    bool Kept,
    string? ChangedFrom,
    string? OverrideNote) : ArbiterInstallEvent;

/// <summary>The warning that <c>--arbiter-key</c> is visible in process argv and shell history.</summary>
public sealed record ArbiterInstallArgvKeyWarning : ArbiterInstallEvent;

/// <summary>An error that stops the run; the host writes it as the command's error line.</summary>
/// <param name="Message">What went wrong.</param>
public sealed record ArbiterInstallError(string Message) : ArbiterInstallEvent;

/// <summary>The install was already current, so nothing was written.</summary>
/// <param name="TargetToken">The harness token.</param>
/// <param name="Root">The deployment root.</param>
public sealed record ArbiterInstallAlreadyInstalled(string TargetToken, string Root) : ArbiterInstallEvent;

/// <summary>A key was stored in the credential store.</summary>
/// <param name="Origin">The origin the key is stored under.</param>
public sealed record ArbiterInstallStoredKey(string Origin) : ArbiterInstallEvent;

/// <summary>A dry run planned a deployment without writing it.</summary>
/// <param name="PlannedFiles">The number of files the plan would deploy.</param>
/// <param name="Root">The deployment root.</param>
public sealed record ArbiterInstallDryRun(int PlannedFiles, string Root) : ArbiterInstallEvent;

/// <summary>The install succeeded.</summary>
/// <param name="TargetToken">The harness token.</param>
/// <param name="Root">The deployment root.</param>
/// <param name="GlobalScope">True when the root is the global user-home scope.</param>
public sealed record ArbiterInstallDone(string TargetToken, string Root, bool GlobalScope) : ArbiterInstallEvent;

/// <summary>The trust step the operator must grant in the harness.</summary>
/// <param name="Wiring">The Arbiter settings the deployment was rendered with.</param>
public sealed record ArbiterInstallTrustSteps(SquadArbiterWiring Wiring) : ArbiterInstallEvent;

/// <summary>The warnings the deployment transaction reported.</summary>
/// <param name="Warnings">One line per warning.</param>
public sealed record ArbiterInstallWarnings(IReadOnlyList<string> Warnings) : ArbiterInstallEvent;

/// <summary>One row of the global hook table.</summary>
/// <param name="Token">The harness token.</param>
/// <param name="Reason">The reason the global wiring gives.</param>
/// <param name="Path">The harness's global root.</param>
/// <param name="Trust">The trust step the harness needs.</param>
public sealed record ArbiterInstallScopeRow(string Token, string Reason, string Path, string Trust);

/// <summary>The global hook table for the targets the user config lists.</summary>
/// <param name="Rows">One row per listed target.</param>
public sealed record ArbiterInstallScopeTable(IReadOnlyList<ArbiterInstallScopeRow> Rows) : ArbiterInstallEvent;

/// <summary>A warning from the verification step.</summary>
/// <param name="Message">What the verification could not confirm.</param>
public sealed record ArbiterInstallProbeWarning(string Message) : ArbiterInstallEvent;

/// <summary>What preparing an install produced.</summary>
/// <param name="ExitCode">The exit code the host returns when the run stops here; 0 when a plan was produced.</param>
/// <param name="Events">The output events, in print order.</param>
/// <param name="Plan">The plan to apply, or null when the run stops after the events.</param>
public sealed record ArbiterInstallPrepared(
    int ExitCode,
    IReadOnlyList<ArbiterInstallEvent> Events,
    ArbiterInstallPlan? Plan);

/// <summary>What applying an install produced.</summary>
/// <param name="ExitCode">The exit code the host returns.</param>
/// <param name="Events">The output events, in print order.</param>
public sealed record ArbiterInstallApplied(int ExitCode, IReadOnlyList<ArbiterInstallEvent> Events);

/// <summary>Runs <c>arbiter install</c>: prepare everything it means to write, then write it.</summary>
/// <remarks>
/// Split in two so the host can ask the operator to confirm the root it is about to write under, which is console I/O
/// the host owns. Neither half writes before <see cref="Apply"/>.
/// </remarks>
public interface IArbiterInstallService
{
    /// <summary>A second identical run is an idempotent no-op: no config rewrite, no duplicate key write, no re-render.</summary>
    /// <param name="request">What the command was asked to do.</param>
    /// <returns>The events so far, and the plan the host confirms and applies.</returns>
    ArbiterInstallPrepared Prepare(ArbiterInstallRequest request);

    /// <summary>
    /// The same preparation without blocking a thread: the docs scaffold, the credential-store lookup and the key
    /// resolution are awaited rather than waited on.
    /// </summary>
    /// <param name="request">What the command was asked to do.</param>
    /// <param name="cancellationToken">Cancels the scaffold and the credential-store lookups.</param>
    /// <returns>The events so far, and the plan the host confirms and applies.</returns>
    Task<ArbiterInstallPrepared> PrepareAsync(ArbiterInstallRequest request, CancellationToken cancellationToken = default);

    /// <summary>Writes the plan, installs the deployment and verifies the result.</summary>
    /// <param name="plan">The plan <see cref="Prepare"/> produced.</param>
    /// <param name="cancellationToken">Cancels the deployment.</param>
    /// <returns>The events, and the exit code the host returns.</returns>
    ArbiterInstallApplied Apply(ArbiterInstallPlan plan, CancellationToken cancellationToken);

    /// <summary>
    /// The same write, install and verify without blocking a thread: the credential-store write and the deployment
    /// are awaited rather than waited on.
    /// </summary>
    /// <param name="plan">The plan <see cref="PrepareAsync"/> produced.</param>
    /// <param name="cancellationToken">Cancels the credential-store write, the deployment and the verification.</param>
    /// <returns>The events, and the exit code the host returns.</returns>
    Task<ArbiterInstallApplied> ApplyAsync(ArbiterInstallPlan plan, CancellationToken cancellationToken = default);
}
