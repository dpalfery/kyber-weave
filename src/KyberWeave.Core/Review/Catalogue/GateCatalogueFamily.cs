namespace KyberWeave.Core.Review.Catalogue;

/// <summary>What a catalogue gate depends on before it is written as a live gate.</summary>
public enum FamilyGateConditionKind
{
    /// <summary>The gate is written only when a file exists under the root.</summary>
    File,

    /// <summary>The gate is written only when the root's <c>package.json</c> defines the named script.</summary>
    Script,

    /// <summary>The gate is written only when the root's <c>package.json</c> does not define the named script.</summary>
    ScriptAbsent,

    /// <summary>The gate is always written commented out, with its note as the reason.</summary>
    Optional,

    /// <summary>The gate is written only for the named test runner (<c>vstest</c> or <c>mtp</c>).</summary>
    Runner
}

/// <summary>One <c>when:</c> condition of a catalogue gate.</summary>
/// <param name="Kind">The kind of condition.</param>
/// <param name="Argument">The file, script name or runner the condition names; null for <see cref="FamilyGateConditionKind.Optional"/>.</param>
public sealed record FamilyGateCondition(FamilyGateConditionKind Kind, string? Argument);

/// <summary>
/// One gate entry of a family file. The same type serves council gates and task gates: a task gate is
/// one that lists the developer <see cref="Agents"/> it supplies.
/// </summary>
/// <param name="Id">The gate id before the family prefix and root suffix are added.</param>
/// <param name="Role">One of <c>format</c>, <c>lint</c>, <c>typecheck</c>, <c>build</c> or <c>test</c>.</param>
/// <param name="Run">The verifying command, as argv, with render placeholders still in it.</param>
/// <param name="Fix">The fixing command, as argv; empty when the family declares none.</param>
/// <param name="Include">Include globs, with render placeholders still in them.</param>
/// <param name="Os"><c>windows</c> or <c>unix</c>; null to run everywhere.</param>
/// <param name="WorkingDirectory">Working directory with render placeholders still in it; null for the repository root.</param>
/// <param name="Blocking">Whether a failing gate blocks the change.</param>
/// <param name="TimeoutSeconds">Seconds before the runner kills the process tree; null for the product default.</param>
/// <param name="Agents">The developer agent slugs a task gate is written for; empty for council gates.</param>
/// <param name="When">The condition under which the gate is written; null means always.</param>
/// <param name="Note">Free text the family author wrote, shown as the reason when the gate is commented out.</param>
/// <param name="FailOnOutput">Whether the gate also fails when its command exits zero but prints to standard output.</param>
public sealed record FamilyGate(
    string Id,
    string Role,
    IReadOnlyList<string> Run,
    IReadOnlyList<string> Fix,
    IReadOnlyList<string> Include,
    string? Os,
    string? WorkingDirectory,
    bool Blocking,
    int? TimeoutSeconds,
    IReadOnlyList<string> Agents,
    FamilyGateCondition? When,
    string? Note,
    bool FailOnOutput = false);

/// <summary>
/// One embedded family file: the gates and task gates one or more technologies ship, and the markers
/// that find their project roots.
/// </summary>
/// <param name="Family">The family name; it prefixes every gate id the family writes.</param>
/// <param name="Technologies">The technologies this family serves.</param>
/// <param name="Markers">File-name globs that mark a project root. The first one stands in when no root exists.</param>
/// <param name="Nesting">Whether every marker directory is a root, or only the outermost ones.</param>
/// <param name="Pair">When true, a root must hold every marker.</param>
/// <param name="Fallback">File-name globs used when no marker matched anywhere and exactly one file matches.</param>
/// <param name="Gates">The council gates, in file order, one entry per variant.</param>
/// <param name="TaskGates">The developer task gates, in file order, one entry per variant.</param>
public sealed record GateCatalogueFamily(
    string Family,
    IReadOnlyList<string> Technologies,
    IReadOnlyList<string> Markers,
    ProjectRootNesting Nesting,
    bool Pair,
    IReadOnlyList<string>? Fallback,
    IReadOnlyList<FamilyGate> Gates,
    IReadOnlyList<FamilyGate> TaskGates)
{
    /// <summary>The distinct council gate ids, in file order, before the family prefix is added.</summary>
    public IReadOnlyList<string> GateIds => Gates.Select(gate => gate.Id).Distinct(StringComparer.Ordinal).ToArray();

    /// <summary>The distinct task gate ids, in file order, before the family prefix is added.</summary>
    public IReadOnlyList<string> TaskGateIds =>
        TaskGates.Select(gate => gate.Id).Distinct(StringComparer.Ordinal).ToArray();
}
