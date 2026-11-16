namespace KyberWeave.Core.Squad.Deployment;

/// <summary>
/// One tree Devin loads alongside <c>.devin/</c> that defines identities <c>.devin/</c>
/// already defines.
/// </summary>
/// <param name="SourceRoot">The overlapping tree, relative to the working directory, e.g. <c>.claude/skills</c>.</param>
/// <param name="Kind"><c>skill</c> or <c>agent</c>.</param>
/// <param name="ImportSetting">
/// The <c>read_config_from</c> key that turns the import off, or null when Devin reads the
/// tree natively and it cannot be turned off.
/// </param>
/// <param name="Identities">The duplicated identities, in stable order.</param>
public sealed record DevinImportOverlapEntry(
    string SourceRoot,
    string Kind,
    string? ImportSetting,
    IReadOnlyList<string> Identities);

/// <summary>The outcome of checking a working directory for identities Devin would load twice.</summary>
/// <param name="Overlaps">Each overlapping tree Devin would still load, in stable order.</param>
public sealed record DevinImportOverlapReport(IReadOnlyList<DevinImportOverlapEntry> Overlaps);
