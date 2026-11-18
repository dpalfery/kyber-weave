using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Mcp;

namespace KyberWeave.Tests.Fakes;

/// <summary>
/// The MCP wiring service in a unit test: it records the root every call was given and answers with what the test
/// scripted, so a command's own decisions can be asserted without a repository on disk.
/// </summary>
public sealed class FakeMcpWiringService : IMcpWiringService
{
    private readonly List<string> _inspectedRoots = [];
    private readonly List<string> _repairedRoots = [];
    private readonly List<IReadOnlyList<string>> _repairTargets = [];
    private readonly List<bool> _repairDryRuns = [];
    private readonly List<string> _unwiredRoots = [];
    private readonly List<string> _scannedRoots = [];
    private readonly List<string?> _scanHarnesses = [];
    private readonly List<string> _migratedRoots = [];
    private readonly List<IReadOnlyList<string>> _migrateTargets = [];

    /// <summary>The roots <see cref="Inspect"/> was asked about, in call order.</summary>
    public IReadOnlyList<string> InspectedRoots => _inspectedRoots;

    /// <summary>The roots <see cref="Repair"/> was asked to write, in call order.</summary>
    public IReadOnlyList<string> RepairedRoots => _repairedRoots;

    /// <summary>The targets each <see cref="Repair"/> was asked to write, in call order.</summary>
    public IReadOnlyList<IReadOnlyList<string>> RepairTargets => _repairTargets;

    /// <summary>Whether each <see cref="Repair"/> was a dry run, in call order.</summary>
    public IReadOnlyList<bool> RepairDryRuns => _repairDryRuns;

    /// <summary>The roots <see cref="Unwire"/> was asked to restore, in call order.</summary>
    public IReadOnlyList<string> UnwiredRoots => _unwiredRoots;

    /// <summary>The roots <see cref="Scan"/> was asked about, in call order.</summary>
    public IReadOnlyList<string> ScannedRoots => _scannedRoots;

    /// <summary>The harness each <see cref="Scan"/> was scoped to, in call order; null means every harness.</summary>
    public IReadOnlyList<string?> ScanHarnesses => _scanHarnesses;

    /// <summary>The roots <see cref="Migrate"/> was asked to rewrite, in call order.</summary>
    public IReadOnlyList<string> MigratedRoots => _migratedRoots;

    /// <summary>The targets each <see cref="Migrate"/> was asked to rewrite, in call order.</summary>
    public IReadOnlyList<IReadOnlyList<string>> MigrateTargets => _migrateTargets;

    /// <summary>The findings <see cref="Inspect"/> reports. Empty by default, which means every harness is wired.</summary>
    public IReadOnlyList<McpWiringFinding> Findings { get; set; } = [];

    /// <summary>The changes <see cref="Scan"/> and <see cref="Migrate"/> report.</summary>
    public IReadOnlyList<McpRenameChange> Changes { get; set; } = [];

    /// <summary>The files <see cref="Repair"/> reports.</summary>
    public IReadOnlyList<McpWiringFileChange> Repairs { get; set; } = [];

    /// <summary>The files <see cref="Unwire"/> reports.</summary>
    public IReadOnlyList<McpWiringFileChange> Removals { get; set; } = [];

    /// <inheritdoc />
    public ILaunchCommandResolver ResolverFor(string repositoryRoot) => new UnresolvedLaunchCommands();

    /// <inheritdoc />
    public McpWiringReport Inspect(string repositoryRoot, McpConfig config) => Inspect(repositoryRoot, config, ResolverFor(repositoryRoot));

    /// <inheritdoc />
    public McpWiringReport Inspect(string repositoryRoot, McpConfig config, ILaunchCommandResolver resolver)
    {
        _inspectedRoots.Add(repositoryRoot);
        return new McpWiringReport(Findings);
    }

    /// <inheritdoc />
    public IReadOnlyList<McpWiringFix> Derive(McpWiringReport report, string repositoryRoot) => [];

    /// <inheritdoc />
    public IReadOnlyList<string> Harnesses(IEnumerable<McpWiringFix> fixes) => [.. fixes.Select(fix => fix.Harness)];

    /// <inheritdoc />
    public McpWiringRepairReport Repair(
        string repositoryRoot,
        McpConfig config,
        ILaunchCommandResolver resolver,
        IReadOnlyList<string> targets,
        bool dryRun = false)
    {
        _repairedRoots.Add(repositoryRoot);
        _repairTargets.Add(targets);
        _repairDryRuns.Add(dryRun);
        return new McpWiringRepairReport(Repairs, dryRun);
    }

    /// <inheritdoc />
    public McpWiringRepairReport Unwire(string repositoryRoot, IReadOnlyList<string> targets)
    {
        _unwiredRoots.Add(repositoryRoot);
        return new McpWiringRepairReport(Removals, DryRun: false);
    }

    /// <inheritdoc />
    public IReadOnlyList<McpRenameChange> Scan(string repositoryRoot, string? harness = null)
    {
        _scannedRoots.Add(repositoryRoot);
        _scanHarnesses.Add(harness);
        return Changes;
    }

    /// <inheritdoc />
    public IReadOnlyList<McpRenameChange> Migrate(string repositoryRoot, IReadOnlyList<string> targets, bool dryRun = false)
    {
        _migratedRoots.Add(repositoryRoot);
        _migrateTargets.Add(targets);
        return Changes;
    }

    /// <summary>A launch resolver that finds nothing, so no case depends on the machine's <c>PATH</c>.</summary>
    private sealed class UnresolvedLaunchCommands : ILaunchCommandResolver
    {
        public string? Resolve(string command) => null;
    }
}
