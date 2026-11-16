using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Release;
using KyberWeave.Core.Squad.Rendering;

namespace KyberWeave.Contracts.Repositories;

/// <summary>Decides whether Factory's settings hooks shadow the block Squad would splice.</summary>
public interface ISquadFactoryHooksShadowing
{
    /// <summary>Drops the Factory block when it would shadow the user's settings hooks, recording why.</summary>
    FactoryHooksShadowingOutcome Resolve(
        string targetRoot,
        IReadOnlyList<SquadTarget> targets,
        SquadArbiterWiring? arbiter,
        SquadDeploymentScope scope,
        IReadOnlyList<SquadRenderedBlock>? blocks);
}
