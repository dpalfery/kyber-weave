using KyberWeave.Core.Squad.Deployment;

namespace KyberWeave.Core.Squad.Rendering;

/// <summary>Asks the wrapped resolver once per target, so every consumer in one render sees one answer.</summary>
/// <param name="inner">The resolver to ask.</param>
public sealed class MemoizedSquadGlobalRoots(ISquadGlobalRootResolver inner) : ISquadGlobalRootResolver
{
    private readonly Dictionary<SquadTarget, string> _answers = [];

    /// <inheritdoc />
    public string? HomeDirectory => inner.HomeDirectory;

    /// <inheritdoc />
    public string ResolveGlobalRoot(SquadTarget target)
    {
        if (!_answers.TryGetValue(target, out string? root))
        {
            root = inner.ResolveGlobalRoot(target);
            _answers[target] = root;
        }

        return root;
    }
}
