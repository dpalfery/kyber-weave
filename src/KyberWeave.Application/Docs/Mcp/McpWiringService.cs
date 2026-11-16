using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;

namespace KyberWeave.Core.Docs.Mcp;

/// <summary>The MCP wiring operations the CLI calls, composed from the inspector, repairer, renamer and fix derivation.</summary>
public sealed class McpWiringService(
    McpWiringInspector inspector,
    McpWiringRepairer repairer,
    McpServerRenamer renamer,
    McpWiringFixes fixes,
    ILaunchCommandResolverFactory launch) : IMcpWiringService
{
    private readonly McpWiringInspector _inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
    private readonly McpWiringRepairer _repairer = repairer ?? throw new ArgumentNullException(nameof(repairer));
    private readonly McpServerRenamer _renamer = renamer ?? throw new ArgumentNullException(nameof(renamer));
    private readonly McpWiringFixes _fixes = fixes ?? throw new ArgumentNullException(nameof(fixes));
    private readonly ILaunchCommandResolverFactory _launch = launch ?? throw new ArgumentNullException(nameof(launch));

    /// <inheritdoc />
    public ILaunchCommandResolver ResolverFor(string repositoryRoot) => _launch.FromEnvironment(repositoryRoot);

    /// <inheritdoc />
    public McpWiringReport Inspect(string repositoryRoot, McpConfig config) =>
        _inspector.Inspect(repositoryRoot, config, ResolverFor(repositoryRoot));

    /// <inheritdoc />
    public McpWiringReport Inspect(string repositoryRoot, McpConfig config, ILaunchCommandResolver resolver) =>
        _inspector.Inspect(repositoryRoot, config, resolver);

    /// <inheritdoc />
    public IReadOnlyList<McpWiringFix> Derive(McpWiringReport report, string repositoryRoot) =>
        _fixes.Derive(report, repositoryRoot);

    /// <inheritdoc />
    public IReadOnlyList<string> Harnesses(IEnumerable<McpWiringFix> fixes) => McpWiringFixes.Harnesses(fixes);

    /// <inheritdoc />
    public McpWiringRepairReport Repair(
        string repositoryRoot,
        McpConfig config,
        ILaunchCommandResolver resolver,
        IReadOnlyList<string> targets,
        bool dryRun) =>
        _repairer.Repair(repositoryRoot, config, resolver, targets, dryRun);

    /// <inheritdoc />
    public McpWiringRepairReport Unwire(string repositoryRoot, IReadOnlyList<string> targets) =>
        _repairer.Unwire(repositoryRoot, targets);

    /// <inheritdoc />
    public IReadOnlyList<McpRenameChange> Scan(string repositoryRoot, string? harness) =>
        _renamer.Scan(repositoryRoot, harness);

    /// <inheritdoc />
    public IReadOnlyList<McpRenameChange> Migrate(string repositoryRoot, IReadOnlyList<string> targets, bool dryRun) =>
        _renamer.Migrate(repositoryRoot, targets, dryRun);
}
