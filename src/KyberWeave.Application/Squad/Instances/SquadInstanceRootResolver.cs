namespace KyberWeave.Core.Squad.Deployment;

/// <summary>
///     Resolves the global root directory for an instance deployment bound to a specific target.
/// </summary>
/// <remarks>
///     Under instance scope, rendering runs with Scope = Global so that harness renderers produce
///     harness-native bare agent paths (e.g., <c>agents/conductor.md</c>) and absolute reference links.
///     This resolver guarantees that requests for the instance's target resolve directly to the instance root,
///     while requests for any other target throw an <see cref="ArgumentException" />.
/// </remarks>
public sealed class SquadInstanceRootResolver : ISquadGlobalRootResolver
{
    private readonly string _instanceRoot;
    private readonly SquadTarget _instanceTarget;

    public SquadInstanceRootResolver(string instanceRoot, SquadTarget instanceTarget)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceRoot);
        _instanceRoot = instanceRoot;
        _instanceTarget = instanceTarget;
    }

    /// <summary>
    ///     Resolves the global root directory for <paramref name="target" />.
    /// </summary>
    /// <param name="target">The target harness to resolve.</param>
    /// <returns>The bound instance root path if <paramref name="target" /> matches the instance target.</returns>
    /// <exception cref="ArgumentException">
    ///     Thrown when <paramref name="target" /> does not match the configured instance
    ///     target.
    /// </exception>
    public string ResolveGlobalRoot(SquadTarget target)
    {
        if (target != _instanceTarget)
            throw new ArgumentException(
                $"Target '{SquadTargetCatalog.GetToken(target)}' is not configured for this instance root resolver (configured for '{SquadTargetCatalog.GetToken(_instanceTarget)}').",
                nameof(target));

        return _instanceRoot;
    }
}
