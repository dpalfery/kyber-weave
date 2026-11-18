namespace KyberWeave.Core.Docs.Model;

/// <summary>
/// The closed set of document shapes. Adding a member is a change to the documentation
/// ontology, not an authoring decision.
/// </summary>
public enum DocType
{
    Unknown = 0,
    Architecture,
    Onboarding,
    Requirements,
    Adr,
    Plan,
    Spec,
    Todo,
    Runbook,
    Reference,
    Rule,
    Governance,
    Index,

    /// <summary>
    /// A technology's coding standard: how code of one stack is written in this repository.
    /// Distinct from <see cref="Rule"/>, which governs the repository as a whole, because a
    /// standard is scoped by the <c>technology</c> key and is what a portable agent looks up
    /// when it needs the local answer rather than the one it shipped with.
    /// </summary>
    CodingStandard,

    /// <summary>
    /// How to carry out work in one technology or role: commands, tool servers, query
    /// patterns and output shape. Scoped by <c>technology</c> or <c>role</c>, and required to
    /// sit in that scope's folder, because a skill that drifts away from its standard stops
    /// matching the work it describes.
    /// </summary>
    Skill
}
