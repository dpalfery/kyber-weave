using KyberWeave.Core.Squad.Deployment;

namespace KyberWeave.Cli.Commands.Squad.Infrastructure;

/// <summary>
/// Chooses which registered projects a global Squad command applies to, when the operator has
/// not named them with <c>--projects</c>.
/// </summary>
/// <remarks>
/// Injectable so a test can answer the question without a terminal. The production picker is
/// interactive and is only consulted when the console can prompt; a non-interactive run must
/// name its projects explicitly rather than block on a prompt that can never be answered.
/// </remarks>
internal interface ISquadProjectPicker
{
    /// <summary>Returns the selected entries' root keys or paths.</summary>
    /// <param name="projects">The registered projects to choose from.</param>
    IReadOnlyList<string> Pick(IReadOnlyList<SquadProjectRegistryEntry> projects);
}
