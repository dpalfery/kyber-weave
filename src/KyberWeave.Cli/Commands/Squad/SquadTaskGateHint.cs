using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Configuration;
using YamlDotNet.Core;

namespace KyberWeave.Cli.Commands.Squad;

/// <summary>
/// Prints the one-line task-gate hint after a project-scope squad deployment.
/// </summary>
/// <remarks>
/// Squad writes no configuration: when the project's host config declares neither
/// <c>review.task-gates</c> nor <c>review.technology-gates</c> — or cannot be read at all, which is the
/// same as no gates for this purpose — the verb prints where those gates come from instead of writing
/// them. A tiered roster keys its gates by technology, so testing the agent-keyed form alone would tell
/// a project that declares the right gates that it has none. The load never fails the verb; an
/// unreadable config still deploys.
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
    /// declares no task gates, keyed by agent or by technology. Global scope never calls this, because
    /// there is no project.
    /// </summary>
    /// <param name="projectRoot">The project root whose host config is read.</param>
    /// <param name="configs">The repository the host config is read from.</param>
    internal static void WriteIfNoTaskGates(string projectRoot, IKyberWeaveConfigRepository configs)
    {
        if (!HasTaskGates(configs, projectRoot))
        {
            SquadCommandComposition.WriteLine(Message);
        }
    }

    /// <summary>Whether the project at <paramref name="projectRoot"/> declares a task gate by agent or by technology.</summary>
    /// <param name="configs">The repository the host config is read from.</param>
    /// <param name="projectRoot">The project root whose host config is read.</param>
    /// <remarks>An unreadable config is the same as no gates: the hint never fails the verb.</remarks>
    internal static bool HasTaskGates(IKyberWeaveConfigRepository configs, string projectRoot)
    {
        try
        {
            KyberWeaveConfigLoadResult result = configs.TryLoad(projectRoot);
            return result.Success
                   && result.Config is not null
                   && (result.Config.Review.TaskGates.Count > 0
                       || result.Config.Review.TechnologyGates.Count > 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or YamlException
                                       or InvalidDataException or ArgumentException
                                       or System.Security.SecurityException or NotSupportedException)
        {
            // TryLoad already turns invalid YAML and I/O failures into a failed result; what
            // reaches here is a path the loader could not even attempt. Unreadable gates are
            // the same as no gates, so the hint prints and the verb keeps its exit code.
            return false;
        }
    }
}
