using KyberWeave.Contracts.Host;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Core.Docs.Delivery;

/// <summary>The ports <see cref="RunDelivery"/> reads and runs through, held together because every delivery needs all of them.</summary>
/// <param name="Files">Reads the plans, review evidence and run records, and checks the plan files.</param>
/// <param name="Process">Runs git for the plan files and bounds the production executor.</param>
/// <param name="RunState">Reads the base each run branch was started from.</param>
/// <param name="Cycles">Reads the finished review cycles under the scratchpad.</param>
/// <param name="Environment">Names the operator's home directory and the directory a relative worktree is resolved against.</param>
/// <param name="Time">Dates the review verdict.</param>
/// <param name="Paths">Resolves the run's worktree to a full path.</param>
/// <param name="Resources">Reads the embedded pull request description template.</param>
public sealed record RunDeliveryPorts(
    IRepositoryFileStore Files,
    IBoundedProcessExecutor Process,
    IRunStateStore RunState,
    IReviewCycleStore Cycles,
    IProcessEnvironment Environment,
    TimeProvider Time,
    IPathResolver Paths,
    IEmbeddedResources Resources);

/// <summary>Runs <see cref="RunDelivery"/> with the production executor and the user profile's run records.</summary>
/// <param name="ports">The ports the delivery reads and runs through.</param>
public sealed class RunDeliveryRunner(RunDeliveryPorts ports) : KyberWeave.Contracts.Services.IRunDeliveryRunner
{
    /// <inheritdoc />
    public DeliveryOutcome Run(string repoRoot, KyberWeave.Core.Configuration.KyberWeaveConfig config, DeliveryRequest request) =>
        new RunDelivery(repoRoot, config, null, null, ports).Run(request);
}
