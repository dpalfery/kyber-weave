using KyberWeave.Core.Configuration;
using YamlDotNet.Core;

namespace KyberWeave.Cli.Commands.Squad;

/// <summary>
/// Prints the one-line task-gate hint after a project-scope squad deployment.
/// </summary>
/// <remarks>
/// Squad writes no configuration: when the project's host config declares no
/// <c>review.task-gates</c> — or cannot be read at all, which is the same as no gates
/// for this purpose — the verb prints where those gates come from instead of writing
/// them. The load never fails the verb; an unreadable config still deploys.
/// </remarks>
internal static class SquadTaskGateHint
{
    /// <summary>
    /// The hint a project-scope verb prints when the project declares no task gates.
    /// </summary>
    internal const string Message =
        "No task gates are declared for this project's developer agents; run `kyber-weave docs init` to write them for your declared technologies.";

    /// <summary>
    /// Prints <see cref="Message"/> when the project at <paramref name="projectRoot"/>
    /// declares no task gates. Global scope never calls this, because there is no project.
    /// </summary>
    internal static void WriteIfNoTaskGates(string projectRoot)
    {
        bool hasTaskGates;
        try
        {
            KyberWeaveConfigLoadResult result = KyberWeaveConfigLoader.TryLoad(projectRoot);
            hasTaskGates = result.Success
                           && result.Config is not null
                           && result.Config.Review.TaskGates.Count > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or YamlException
                                       or InvalidDataException or ArgumentException)
        {
            // TryLoad already turns invalid YAML and I/O failures into a failed result; what
            // reaches here is a path the loader could not even attempt. Unreadable gates are
            // the same as no gates, so the hint prints and the verb keeps its exit code.
            hasTaskGates = false;
        }

        if (!hasTaskGates)
        {
            SquadCommandComposition.WriteLine(Message);
        }
    }
}
