using KyberWeave.Core.Squad.Deployment;
using Spectre.Console;

namespace KyberWeave.Cli.Commands.Squad.Infrastructure;

/// <summary>
/// The production project picker: a Spectre multi-select over the registered projects.
/// </summary>
/// <remarks>
/// Spectre 0.57 has no validation hook on a multi-select, so an invalid selection (<c>None</c> beside
/// another choice) is reported and the prompt is shown again rather than accepted. Only the console
/// layer lives here; the choices and the rule come from <see cref="SquadProjectPickerChoices"/>, which
/// is what the tests exercise.
/// </remarks>
/// <param name="console">The console the prompt is shown on and answered from.</param>
internal sealed class SpectreSquadProjectPicker(IAnsiConsole console) : ISquadProjectPicker
{
    private readonly IAnsiConsole _console = console ?? throw new ArgumentNullException(nameof(console));

    /// <inheritdoc />
    public IReadOnlyList<string> Pick(IReadOnlyList<SquadProjectRegistryEntry> projects)
    {
        ArgumentNullException.ThrowIfNull(projects);
        if (projects.Count == 0)
        {
            return [];
        }

        IReadOnlyList<string> labels = SquadProjectPickerChoices.Build(projects);
        while (true)
        {
            List<string> chosen = _console.Prompt(
                new MultiSelectionPrompt<string>()
                    .Title("Also update these registered projects?")
                    .InstructionsText("(space toggles a choice, enter accepts)")
                    .NotRequired()
                    .PageSize(12)
                    .AddChoices(labels));

            if (SquadProjectPickerChoices.TryValidateProjectChoices(chosen, out string? error))
            {
                return SquadProjectPickerChoices.ToPaths(chosen, projects);
            }

            _console.MarkupLine($"[red]{Markup.Escape(error ?? "That selection is not valid.")}[/]");
        }
    }
}
