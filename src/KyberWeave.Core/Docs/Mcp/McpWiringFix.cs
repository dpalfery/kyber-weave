namespace KyberWeave.Core.Docs.Mcp;

/// <summary>
/// One harness file a wiring repair would write, and the servers it would put there.
/// </summary>
/// <param name="Harness">The harness slug, which is also the repair's target.</param>
/// <param name="Path">The repository-relative configuration file the write would land in.</param>
/// <param name="Servers">The server names the write would add, ordinal-sorted.</param>
/// <param name="Create">
/// Whether the file is absent today, so the repair would create it rather than insert into it.
/// </param>
/// <remarks>
/// This is what an operator is shown before consenting to a write, so it names the file and the
/// servers rather than the finding that prompted them: "cursor is missing a server" is a diagnosis,
/// and "this would write .cursor/mcp.json adding kyber-weave" is what consent is given to.
/// </remarks>
public sealed record McpWiringFix(
    string Harness,
    string Path,
    IReadOnlyList<string> Servers,
    bool Create);

/// <summary>
/// Turns an inspection report into the writes a repair would perform, so a caller offering the
/// repair can say exactly what it would do without re-deriving the catalog.
/// </summary>
/// <remarks>
/// <para>
/// The list is derived from the findings rather than from the catalog, because a finding is the only
/// thing that knows both that a gap exists and whether the repair may close it. A writable gap
/// carries <see cref="McpWiringFixability.Fixable"/> and names the harness, the file and the server;
/// a not-detectable line carries no server at all, and a snippet-only harness is one the repair
/// refuses on purpose (<see cref="McpWiringFixability.NotFixable"/>). Neither of those can appear in
/// a list of writes, so neither is offered.
/// </para>
/// <para>
/// Fixes are grouped by harness because a harness is the repair's unit of consent
/// (<see cref="McpWiringRepairer.Repair"/> takes harness slugs), and because the inspector names one
/// file per harness: two findings for the same harness describe two servers going into the same file,
/// which is one write and one question.
/// </para>
/// </remarks>
public static class McpWiringFixes
{
    /// <summary>
    /// The writes that would close <paramref name="report"/>'s fixable gaps, in report order.
    /// </summary>
    /// <param name="report">The inspection's findings.</param>
    /// <param name="repositoryRoot">
    /// The repository the findings' relative paths are relative to, read only to say whether each
    /// file exists yet. Nothing is written.
    /// </param>
    /// <returns>One fix per harness with at least one fixable gap, or an empty list.</returns>
    public static IReadOnlyList<McpWiringFix> Derive(McpWiringReport report, string repositoryRoot)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);

        string root = Path.GetFullPath(repositoryRoot);
        List<string> harnesses = [];
        Dictionary<string, HarnessPlan> plans = new(StringComparer.Ordinal);

        foreach (McpWiringFinding finding in report.Findings)
        {
            // Path.Combine(root, "") is the root itself, so an absent, empty or whitespace path is
            // excluded here rather than resolved into a question about the repository directory.
            if (finding.Fixable != McpWiringFixability.Fixable || string.IsNullOrWhiteSpace(finding.Path))
                continue;

            if (!plans.TryGetValue(finding.Harness, out HarnessPlan? plan))
            {
                plan = new HarnessPlan(finding.Path);
                plans[finding.Harness] = plan;
                harnesses.Add(finding.Harness);
            }

            plan.Servers.Add(finding.Server);
        }

        return
        [
            .. harnesses.Select(harness => new McpWiringFix(
                harness,
                plans[harness].Path,
                [.. plans[harness].Servers],
                Create: !File.Exists(Path.Combine(root, plans[harness].Path))))
        ];
    }

    /// <summary>
    /// The harness slugs to hand a repair, deduplicated and in order.
    /// </summary>
    /// <param name="fixes">The fixes an operator consented to.</param>
    /// <remarks>
    /// One harness can hold several servers, and the repair takes a target once; passing a duplicate
    /// would ask it to write the same file twice in one run.
    /// </remarks>
    public static IReadOnlyList<string> Harnesses(IEnumerable<McpWiringFix> fixes)
    {
        ArgumentNullException.ThrowIfNull(fixes);

        List<string> harnesses = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (McpWiringFix fix in fixes)
        {
            if (seen.Add(fix.Harness))
                harnesses.Add(fix.Harness);
        }

        return harnesses;
    }

    /// <summary>What one harness's write would contain, accumulated as its findings are read.</summary>
    private sealed class HarnessPlan(string path)
    {
        /// <summary>The file the harness's findings named.</summary>
        public string Path { get; } = path;

        /// <summary>The servers to add, ordinal-sorted so the same tree yields the same question.</summary>
        public SortedSet<string> Servers { get; } = new(StringComparer.Ordinal);
    }
}
