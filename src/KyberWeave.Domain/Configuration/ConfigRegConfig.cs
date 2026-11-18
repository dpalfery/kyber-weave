namespace KyberWeave.Core.Configuration;

/// <summary>What the value of a registry entry means.</summary>
/// <remarks>
/// The distinction exists because the registry's guarantee is "every path value resolves",
/// and a setting is not a path. Carrying the kind on the entry keeps that guarantee intact
/// for paths: a validator can skip the one kind whose value is not meant to be found on
/// disk, instead of learning which property names are special.
/// </remarks>
public enum ConfigRegEntryKind
{
    /// <summary>A repository-relative path that must resolve to a file or directory.</summary>
    Path,

    /// <summary>A mode word describing configuration state; nothing on disk answers to it.</summary>
    Mode
}

/// <summary>One property of the configuration registry.</summary>
/// <param name="Name">Lookup name, without the angle brackets it is written in.</param>
/// <param name="Path">
/// For <see cref="ConfigRegEntryKind.Path"/>, the repository-relative path, forward-slashed.
/// For <see cref="ConfigRegEntryKind.Mode"/>, the mode word the property publishes — the
/// field is named for its usual occupant and is not a path in that case.
/// </param>
/// <param name="Kind">Whether <paramref name="Path"/> is a path or a mode word.</param>
public sealed record ConfigRegEntry(
    string Name,
    string Path,
    ConfigRegEntryKind Kind = ConfigRegEntryKind.Path);

/// <summary>
/// The repository's configuration registry: named paths that portable agents and skills look
/// up instead of embedding a relative path out of their own directory.
/// </summary>
/// <remarks>
/// <para>
/// Only the host's own additions are stored. Everything <c>docs init</c> creates is
/// <em>derived</em> from the documentation root and the declared technologies rather than
/// written into the configuration file — a stored copy of a derivable path is a path that
/// goes stale the day <c>docs-root</c> changes, and the operator would then be repairing
/// values they never authored.
/// </para>
/// <para>
/// An addition may reuse a built-in name, which replaces that entry in place. A repository
/// that keeps its ADRs somewhere else gets to say so; what it may not do is remove the name,
/// because a skill written against the registry resolves properties by name and a missing one
/// is indistinguishable from a typo.
/// </para>
/// <para>
/// A reserved name is the exception to that replacement: a <em>mode</em> property carries a
/// setting rather than a path, so declaring one under <c>config-reg:</c> is refused rather
/// than allowed to replace the derived entry with a path. The registry's promise survives —
/// every path value resolves — because only the entry kind that is not a path is exempt.
/// </para>
/// <para>
/// A setting that publishes a mode property is held here rather than on a type of its own,
/// because this is what renders the line: the value an agent reads and the value the code
/// applies then come from one place and cannot disagree. <c>autonomy:</c> was the first, the
/// run settings are the rest.
/// </para>
/// </remarks>
public sealed class ConfigRegConfig
{
    public const string DocsRootProperty = "docs-root";
    public const string DocumentationIndexProperty = "documentation-index";
    public const string DocumentationOntologyProperty = "documentation-ontology";
    public const string ComponentCatalogProperty = "component-catalog";
    public const string StandardsRootProperty = "standards-root";
    public const string PlanIndexProperty = "plan-index";
    public const string TodoIndexProperty = "todo-index";
    public const string SpecificationIndexProperty = "specification-index";

    /// <summary>
    /// Registry property carrying the repository's autonomy setting. Reserved: it holds a mode
    /// word, not a path, so a host may not declare it under <c>config-reg:</c>.
    /// </summary>
    public const string AutonomyProperty = "autonomy";

    public const string SupervisedAutonomy = "supervised";
    public const string AutonomousAutonomy = "autonomous";

    /// <summary>
    /// Registry property carrying this repository's answer to the onboarding question. A
    /// <see cref="ConfigRegEntryKind.Mode"/> entry, published from the top-level
    /// <c>onboarding:</c> setting and reserved: a host may not declare it under
    /// <c>config-reg:</c>.
    /// </summary>
    public const string OnboardingProperty = "onboarding";

    /// <summary>
    /// The value <see cref="OnboardingProperty"/> carries when the repository has answered and
    /// accepted the onboarding step. Absence of the property is the unanswered state, so there
    /// is no word for it.
    /// </summary>
    public const string OnboardingDone = "done";

    /// <summary>
    /// The value <see cref="OnboardingProperty"/> carries when the repository was offered the
    /// onboarding step and chose not to take it. It behaves as <see cref="OnboardingDone"/>
    /// does for every reader: the question has been asked and answered, so it is never repeated.
    /// </summary>
    public const string OnboardingDeclined = "declined";

    /// <summary>Every value <c>onboarding:</c> accepts, in the order a refusal names them.</summary>
    public static IReadOnlyList<string> OnboardingAnswers { get; } = [OnboardingDone, OnboardingDeclined];

    /// <summary>
    /// Registry property reporting whether this repository opted in to the intake assessment.
    /// A <see cref="ConfigRegEntryKind.Mode"/> entry, published from <c>ontology.intake:</c>
    /// and reserved: a host may not declare it under <c>config-reg:</c>.
    /// </summary>
    public const string IntakeAssessmentProperty = "intake-assessment";

    /// <summary>
    /// The value <see cref="IntakeAssessmentProperty"/> carries when the assessment is on.
    /// Absence of the property is the off state, so there is no word for it: a skill that
    /// finds no property skips the assessment, and one that finds <em>a</em> value runs it.
    /// </summary>
    public const string IntakeAssessmentEnabled = "enabled";

    /// <summary>
    /// Registry property carrying whether a failed tool stops a run. A
    /// <see cref="ConfigRegEntryKind.Mode"/> entry, published from the top-level
    /// <c>stop-on-tool-failure:</c> setting and reserved: a host may not declare it under
    /// <c>config-reg:</c>.
    /// </summary>
    public const string StopOnToolFailureProperty = "stop-on-tool-failure";

    /// <summary>
    /// Registry property carrying the branch a run is compared against. A
    /// <see cref="ConfigRegEntryKind.Mode"/> entry, published from <c>run.base-branch</c> and
    /// reserved: a host may not declare it under <c>config-reg:</c>.
    /// </summary>
    public const string RunBaseBranchProperty = "run-base-branch";

    /// <summary>
    /// The branch a run is compared against when the operator named none. <c>origin/HEAD</c>
    /// rather than <c>main</c>: the default branch differs per repository, and a hard-coded
    /// <c>main</c> compares against a branch that may not exist while looking authoritative.
    /// </summary>
    public const string DefaultBaseBranch = "origin/HEAD";

    /// <summary>Registry property for the rules folder's index.</summary>
    public const string RulesIndexProperty = "rules-index";

    /// <summary>Suffix that turns a declared technology into its registry property name.</summary>
    public const string CodingStandardSuffix = "-coding-standard";

    /// <summary>The registry property naming the worker roster a run dispatches from.</summary>
    public const string WorkerRosterProperty = "worker-roster";

    /// <summary>Entries the host added to <c>config-reg:</c>, in the order it wrote them.</summary>
    public IReadOnlyList<ConfigRegEntry> Additions { get; init; } = [];

    /// <summary>
    /// The top-level <c>autonomy:</c> setting — <see cref="SupervisedAutonomy"/> or
    /// <see cref="AutonomousAutonomy"/> — or null when the repository did not set one, which
    /// agents read as supervised. Rendered into the Config Reg block only when set, and never
    /// resolved as a path.
    /// </summary>
    public string? Autonomy { get; init; }

    /// <summary>
    /// The top-level <c>onboarding:</c> setting — <see cref="OnboardingDone"/> or
    /// <see cref="OnboardingDeclined"/> — or null when the repository never answered, which
    /// <c>run start</c> reads as "stop and offer the onboarding skill". Rendered into the
    /// Config Reg block only when set, and never resolved as a path.
    /// </summary>
    /// <remarks>
    /// Null rather than a default word, because the three states are not two: unanswered is
    /// what makes a run stop, and a default would erase the difference between a repository
    /// that declined and one nobody ever asked.
    /// </remarks>
    public string? Onboarding { get; init; }

    /// <summary>
    /// The top-level <c>stop-on-tool-failure:</c> setting as the operator wrote it, or null when
    /// the repository did not set one. Read <see cref="StopsOnToolFailure"/> for the decision a
    /// run makes; this member only reports what the file said.
    /// </summary>
    public bool? StopOnToolFailure { get; init; }

    /// <summary>
    /// Whether a failed tool stops a run. An absent setting stops, and that default is applied
    /// here in code: absence is the common case, and instruction text an agent may or may not
    /// have read is not something a run can rely on for it.
    /// </summary>
    public bool StopsOnToolFailure => StopOnToolFailure ?? true;

    /// <summary>
    /// The <c>run.base-branch</c> setting as the operator wrote it, or null when the repository
    /// did not set one. Read <see cref="BaseBranch"/> for the branch a run actually uses.
    /// </summary>
    public string? RunBaseBranch { get; init; }

    /// <summary>The worker roster setting to publish, or null when the host wrote none.</summary>
    public string? WorkerRoster { get; init; }

    /// <summary>
    /// The branch a run is compared against: the configured one, or
    /// <see cref="DefaultBaseBranch"/> when the operator never set one.
    /// </summary>
    public string BaseBranch =>
        string.IsNullOrWhiteSpace(RunBaseBranch) ? DefaultBaseBranch : RunBaseBranch;

    public static ConfigRegConfig ProductDefaults { get; } = new();
}
