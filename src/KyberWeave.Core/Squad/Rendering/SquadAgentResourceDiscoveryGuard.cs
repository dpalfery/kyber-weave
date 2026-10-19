using System.Globalization;
using System.Text;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;

namespace KyberWeave.Core.Squad.Rendering;

/// <summary>
/// Projects an agent's declared resource closure beside its principal in a form the
/// harness agent scanner ignores.
/// </summary>
/// <remarks>
/// <para>
/// Some targets discover agents by scanning the agents directory recursively for
/// <c>*.md</c> files and deriving an agent name from each match's path. Projecting an
/// agent's resource closure beside its principal with a <c>.md</c> extension would
/// therefore register every reference file as a phantom agent. Mapping the extension to
/// <c>.markdown</c> keeps each file co-located with — and wholly owned by — its
/// principal while making it invisible to a <c>*.md</c>-only scanner, with no content
/// mutation and no registry participation. Dot-prefixing is not an alternative: the
/// scanners match dotfiles too. Non-markdown resources project verbatim because the
/// scanner never matches them.
/// </para>
/// <para>
/// The authored-link rewrite is driven by the declared resource closure rather than a
/// pattern over prose, reusing <see cref="SquadResourceLinkRewriter"/> with the
/// extension map, and the deviation from verbatim projection is recorded as a
/// <c>resource-links-rewritten</c> degradation. The map is validated fail-closed: a
/// closure holding both <c>x.md</c> and <c>x.markdown</c> would alias to one deployed
/// path, so the render throws rather than merging them.
/// </para>
/// </remarks>
internal static class SquadAgentResourceDiscoveryGuard
{
    /// <summary>
    /// Maps an authored resource path to its deployed path: an ordinal <c>.md</c> suffix
    /// becomes <c>.markdown</c>; anything else projects verbatim.
    /// </summary>
    internal static string MapResourcePath(string relativePath)
    {
        return relativePath.EndsWith(".md", StringComparison.Ordinal)
            ? string.Concat(relativePath[..^".md".Length], ".markdown")
            : relativePath;
    }

    /// <summary>
    /// Rewrites the agent body's in-closure links to their discovery-guarded paths.
    /// A resource-less agent has no closure to drive the rewrite, so its body passes
    /// through untouched.
    /// </summary>
    internal static string RewriteBody(SquadAgent agent)
    {
        if (agent.Resources.Count == 0)
        {
            return agent.InstructionBody;
        }

        HashSet<string> resourcePaths = agent.Resources
            .Select(resource => resource.RelativePath)
            .ToHashSet(StringComparer.Ordinal);
        return SquadResourceLinkRewriter.Rewrite(
            agent.InstructionBody,
            resourcePaths,
            MapResourcePath,
            out _);
    }

    /// <summary>
    /// Projects <paramref name="resources"/> beside <paramref name="principal"/> with the
    /// discovery-guard extension map, and records the <c>resource-links-rewritten</c>
    /// degradation. Fails closed when the closure aliases two authored paths onto one
    /// deployed path.
    /// </summary>
    internal static void Append(
        List<SquadDeploymentFile> files,
        SquadAgent agent,
        SquadDeploymentFile principal,
        IReadOnlyList<SquadResource> resources,
        List<SquadDegradationRecord> degradations,
        string targetToken,
        string scannerQuote)
    {
        if (resources.Count == 0)
        {
            return;
        }

        // The extension map aliases x.md onto x.markdown. A closure holding both spellings
        // would deploy two files to one path; fail closed rather than merge them.
        HashSet<string> closure = resources
            .Select(resource => resource.RelativePath)
            .ToHashSet(StringComparer.Ordinal);
        foreach (SquadResource resource in resources)
        {
            string mapped = MapResourcePath(resource.RelativePath);
            if (!string.Equals(mapped, resource.RelativePath, StringComparison.Ordinal) &&
                closure.Contains(mapped))
            {
                throw new SquadRenderValidationException(
                    $"Cannot project resources for agent '{agent.Name}': its closure contains both " +
                    $"'{resource.RelativePath}' and '{mapped}'. The discovery guard maps '.md' to " +
                    "'.markdown', so the two files would overwrite each other beside their principal.");
            }
        }

        // Whether any deployed path actually changes extension is decided by the
        // resources themselves, not by which body links the rewriter matched: a
        // .md resource is renamed on disk even when the body only names it in
        // prose or inside a code span the rewriter deliberately leaves untouched.
        bool anyRenamed = resources.Any(resource =>
            !string.Equals(
                MapResourcePath(resource.RelativePath),
                resource.RelativePath,
                StringComparison.Ordinal));

        int separator = principal.RelativePath.LastIndexOf('/');
        string principalDirectory = separator < 0 ? string.Empty : principal.RelativePath[..separator];
        foreach (SquadResource resource in resources)
        {
            string relativePath = principalDirectory.Length == 0
                ? MapResourcePath(resource.RelativePath)
                : $"{principalDirectory}/{MapResourcePath(resource.RelativePath)}";
            files.Add(new SquadDeploymentFile(
                relativePath,
                Encoding.UTF8.GetBytes(resource.Content),
                targetToken));
        }

        degradations.Add(new SquadDegradationRecord(
            targetToken,
            agent.Name,
            agent.Name,
            "resource-links-rewritten",
            agent.BodyDigest,
            $"The harness agent scanner matches '{scannerQuote}' recursively, so this agent's " +
            $"{resources.Count.ToString(CultureInfo.InvariantCulture)} resource file(s) cannot be " +
            "projected beside their principal with a '.md' extension without registering as " +
            "phantom agents. " +
            (anyRenamed
                ? "They are projected with a '.markdown' extension instead, and the agent's authored links were rewritten to '.markdown' so they still resolve."
                : "They project verbatim because the scanner never matches non-markdown files, and no authored link required rewriting.")));
    }
}
