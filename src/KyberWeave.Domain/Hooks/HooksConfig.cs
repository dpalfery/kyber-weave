namespace KyberWeave.Domain.Hooks;

/// <summary>The effective hook configuration: built-in defaults merged with the repository file.</summary>
/// <param name="TimeBudgetSeconds">Total time the hook may spend running commands for one edit.</param>
/// <param name="StopOnFirstBlocking">Whether to stop after the first blocking failure instead of reporting all.</param>
/// <param name="MaxOutputLines">Most output lines kept per failing rule in the feedback.</param>
/// <param name="MaxOutputBytes">Most output bytes kept per failing rule in the feedback.</param>
/// <param name="Rules">Every rule, enabled or not, in declaration order.</param>
/// <param name="Warnings">Problems found while reading the config, such as unknown keys. Never failures.</param>
public sealed record HooksConfig(
    int TimeBudgetSeconds,
    bool StopOnFirstBlocking,
    int MaxOutputLines,
    int MaxOutputBytes,
    IReadOnlyList<HookRule> Rules,
    IReadOnlyList<string> Warnings);
