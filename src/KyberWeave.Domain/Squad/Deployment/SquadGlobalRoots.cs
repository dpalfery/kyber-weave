using System.Text.Json;

namespace KyberWeave.Core.Squad.Deployment;

/// <summary>Resolves the real per-user global root for each target's agents and skills.</summary>
public interface ISquadGlobalRootResolver
{
    /// <summary>
    ///     Returns the absolute path to the global root for <c>target</c>'s agents and skills,
    ///     reading the target's override environment variable first, falling back to the
    ///     verified default beneath the home directory if the override is null or empty.
    ///     Targets without a renderer throw <see cref="ArgumentOutOfRangeException" />.
    /// </summary>
    string ResolveGlobalRoot(SquadTarget target);

    /// <summary>
    ///     The fully qualified per-user home directory the roots resolve against, or null when the
    ///     resolver does not know it. A render uses it to write a global path as <c>~/…</c>, so the
    ///     rendered text does not change with the machine's user name.
    /// </summary>
    string? HomeDirectory => null;
}
