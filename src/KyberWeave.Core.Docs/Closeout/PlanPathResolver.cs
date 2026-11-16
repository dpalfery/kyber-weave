using KyberWeave.Core.Configuration;
using KyberWeave.Data.Docs.FileSystem;

namespace KyberWeave.Core.Docs.Closeout;

/// <summary>
/// Resolves a plan or specification argument against the physical file system. Forwards to <see cref="PlanFileResolver"/>
/// until the callers that still build it themselves take it from the container.
/// </summary>
/// <param name="repoRoot">The repository root.</param>
/// <param name="config">The effective configuration.</param>
public sealed class PlanPathResolver(string repoRoot, KyberWeaveConfig config)
    : PlanFileResolver(repoRoot, config, new PhysicalRepositoryFileStore());
