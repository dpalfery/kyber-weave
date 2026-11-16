using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Docs.Mcp;
using KyberWeave.Core.Configuration;

namespace KyberWeave.Contracts.Services;

/// <summary>
/// Inspects, repairs, unwires and renames the Kyber-Weave MCP server entries in a repository's harness configuration
/// files. Every write is bounded to repository-scope files, and a refusal changes nothing.
/// </summary>
public interface IMcpWiringService
{
    /// <summary>A launch-command resolver over this process's <c>PATH</c>, resolving against <paramref name="repositoryRoot"/>.</summary>
    /// <param name="repositoryRoot">The directory a relative command is resolved against.</param>
    ILaunchCommandResolver ResolverFor(string repositoryRoot);

    /// <summary>Checks every detectable harness against the declared servers, resolving commands over this process's <c>PATH</c>.</summary>
    /// <param name="repositoryRoot">The repository root.</param>
    /// <param name="config">The MCP declaration.</param>
    McpWiringReport Inspect(string repositoryRoot, McpConfig config);

    /// <summary>Checks every detectable harness against the declared servers.</summary>
    /// <param name="repositoryRoot">The repository root.</param>
    /// <param name="config">The MCP declaration.</param>
    /// <param name="resolver">Resolves each declared command to the file a launch would use.</param>
    McpWiringReport Inspect(string repositoryRoot, McpConfig config, ILaunchCommandResolver resolver);

    /// <summary>Turns an inspection report into the writes a repair would perform.</summary>
    /// <param name="report">The inspection report.</param>
    /// <param name="repositoryRoot">The repository the findings' relative paths are relative to.</param>
    IReadOnlyList<McpWiringFix> Derive(McpWiringReport report, string repositoryRoot);

    /// <summary>The harness slugs a set of fixes would write, each once.</summary>
    /// <param name="fixes">The fixes.</param>
    IReadOnlyList<string> Harnesses(IEnumerable<McpWiringFix> fixes);

    /// <summary>Writes the declared servers into the named harness files, recording what each write replaced.</summary>
    /// <param name="repositoryRoot">The repository root.</param>
    /// <param name="config">The MCP declaration.</param>
    /// <param name="resolver">Resolves each declared command.</param>
    /// <param name="targets">The harness slugs to repair.</param>
    /// <param name="dryRun">True to report what would change without writing.</param>
    McpWiringRepairReport Repair(
        string repositoryRoot,
        McpConfig config,
        ILaunchCommandResolver resolver,
        IReadOnlyList<string> targets,
        bool dryRun = false);

    /// <summary>Restores the bytes a repair recorded, or refuses when the file has drifted.</summary>
    /// <param name="repositoryRoot">The repository root.</param>
    /// <param name="targets">The harness slugs to restore.</param>
    McpWiringRepairReport Unwire(string repositoryRoot, IReadOnlyList<string> targets);

    /// <summary>Reports the retired server keys an install still carries, without writing.</summary>
    /// <param name="repositoryRoot">The repository root.</param>
    /// <param name="harness">One harness slug, or null for all.</param>
    IReadOnlyList<McpRenameChange> Scan(string repositoryRoot, string? harness = null);

    /// <summary>Renames the retired server keys in place, for entries it can prove are Kyber-Weave's.</summary>
    /// <param name="repositoryRoot">The repository root.</param>
    /// <param name="targets">The harness slugs, or the declaration target, to migrate; empty for all.</param>
    /// <param name="dryRun">True to report what would change without writing.</param>
    IReadOnlyList<McpRenameChange> Migrate(string repositoryRoot, IReadOnlyList<string> targets, bool dryRun = false);
}
