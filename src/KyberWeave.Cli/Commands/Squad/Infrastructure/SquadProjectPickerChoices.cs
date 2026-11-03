using KyberWeave.Core.Squad.Deployment;

namespace KyberWeave.Cli.Commands.Squad.Infrastructure;

/// <summary>
/// The pure half of the project picker: the choices it offers, the rule that makes a selection
/// valid, and the mapping from a chosen label back to a project path. Kept free of any console so
/// the rules the operator sees can be tested directly.
/// </summary>
/// <remarks>
/// <c>All</c> means every project that is not stale, <c>None</c> means nothing, and a project's
/// label is its path, with <c>(stale: reason)</c> appended when the registry marks it stale. A stale
/// project stays selectable so the operator can see it, but the caller skips it with a warning
/// rather than updating a root that no longer carries a deployment.
/// </remarks>
internal static class SquadProjectPickerChoices
{
    /// <summary>The label that selects every project that is not stale.</summary>
    internal const string AllLabel = "All";

    /// <summary>The label that selects nothing; it cannot be combined with another choice.</summary>
    internal const string NoneLabel = "None";

    /// <summary>
    /// Builds the choices in display order: <c>All</c>, <c>None</c>, then one label per registered project.
    /// </summary>
    internal static IReadOnlyList<string> Build(IReadOnlyList<SquadProjectRegistryEntry> projects)
    {
        ArgumentNullException.ThrowIfNull(projects);

        List<string> labels = [AllLabel, NoneLabel];
        foreach (SquadProjectRegistryEntry project in projects)
        {
            labels.Add(LabelFor(project));
        }

        return labels;
    }

    /// <summary>
    /// Accepts a selection unless <c>None</c> is combined with any other choice. An empty selection is
    /// valid and means nothing.
    /// </summary>
    /// <param name="choices">The labels the operator selected.</param>
    /// <param name="error">The reason the selection is refused, or <see langword="null"/> when it is valid.</param>
    internal static bool TryValidateProjectChoices(IReadOnlyList<string> choices, out string? error)
    {
        ArgumentNullException.ThrowIfNull(choices);

        bool hasNone = choices.Contains(NoneLabel, StringComparer.Ordinal);
        if (hasNone && choices.Count > 1)
        {
            error = "None selects no project, so it cannot be combined with another choice. " +
                "Clear it to choose All or specific projects, or select None on its own.";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Maps the operator's selected labels to project paths, in registry order and without duplicates.
    /// <c>All</c> expands to every project that is not stale; a stale project chosen by name is kept so
    /// the caller can warn about it.
    /// </summary>
    internal static IReadOnlyList<string> ToPaths(
        IReadOnlyList<string> chosenLabels,
        IReadOnlyList<SquadProjectRegistryEntry> projects)
    {
        ArgumentNullException.ThrowIfNull(chosenLabels);
        ArgumentNullException.ThrowIfNull(projects);

        bool all = chosenLabels.Contains(AllLabel, StringComparer.Ordinal);
        List<string> paths = [];
        foreach (SquadProjectRegistryEntry project in projects)
        {
            bool selected = (all && !project.Stale) ||
                chosenLabels.Contains(LabelFor(project), StringComparer.Ordinal);
            if (selected && !paths.Contains(project.Path, StringComparer.Ordinal))
            {
                paths.Add(project.Path);
            }
        }

        return paths;
    }

    /// <summary>The label one registered project is offered under.</summary>
    internal static string LabelFor(SquadProjectRegistryEntry project)
    {
        ArgumentNullException.ThrowIfNull(project);

        return project.Stale
            ? $"{project.Path} (stale: {project.StaleReason ?? "unknown"})"
            : project.Path;
    }
}
