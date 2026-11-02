using System.Text;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;

namespace KyberWeave.Cli.Commands.Squad.Infrastructure;

/// <summary>The rewritten files and model diff rows from one preset receipt rewrite pass.</summary>
/// <param name="Files">The receipt files with preset model tokens applied, in receipt order.</param>
/// <param name="Diffs">One row per file whose resolved model changed, ordered ordinally by agent.</param>
internal sealed record SquadPresetRewriteResult(
    IReadOnlyList<SquadDeploymentFile> Files,
    IReadOnlyList<SquadModelDiffItem> Diffs);

/// <summary>
///     Applies preset model tokens to deployed receipt files, agent files only.
/// </summary>
/// <remarks>
///     The preset use and clear loops were near-identical copies, and both shared the same two
///     defects: every receipt <c>.md</c> was treated as an agent (so skill files gained a
///     <c>model:</c> key they never carry), and an unresolvable identity fell back to the
///     <c>general</c> category. One helper closes the defect class instead of patching it twice.
///     Skill discrimination is path shape alone — a final <c>SKILL.md</c> segment or any
///     <c>skills</c> segment — because the roster cannot tell a skill from an agent entry-point
///     projection that shares the agent's name. Token resolution uses
///     <see cref="SquadModelResolution.ResolveToken(SquadModelProfiles, string, string)" />
///     semantics with no general-category fallback, so adopted or unknown files keep their bytes
///     and produce no diff row, matching install-time <c>SquadModelDiff.Compute</c> which
///     iterates only canonical agents.
/// </remarks>
internal static class SquadPresetFileRewriter
{
    /// <summary>
    ///     Rewrites receipt file contents with the model tokens resolved from
    ///     <paramref name="profiles" />.
    /// </summary>
    /// <param name="receiptFiles">The deployment receipt files to consider, in receipt order.</param>
    /// <param name="targetTokens">The harness tokens this preset operation applies to.</param>
    /// <param name="profiles">The effective model profiles (preset-merged for use, bundled for clear).</param>
    /// <param name="agentProfiles">The canonical agent name to profile category map.</param>
    /// <param name="readContent">Reads a receipt file's current bytes, or null when the file is missing.</param>
    /// <returns>The rewritten files and the diff rows for files whose model changed.</returns>
    public static SquadPresetRewriteResult Rewrite(
        IReadOnlyList<SquadOwnedFile> receiptFiles,
        IReadOnlySet<string> targetTokens,
        SquadModelProfiles profiles,
        IReadOnlyDictionary<string, string> agentProfiles,
        Func<SquadOwnedFile, byte[]?> readContent)
    {
        ArgumentNullException.ThrowIfNull(receiptFiles);
        ArgumentNullException.ThrowIfNull(targetTokens);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(agentProfiles);
        ArgumentNullException.ThrowIfNull(readContent);

        List<SquadDeploymentFile> rewritten = new();
        List<SquadModelDiffItem> diffs = new();

        foreach (SquadOwnedFile file in receiptFiles)
        {
            byte[]? rawBytes = readContent(file);
            if (rawBytes is null)
            {
                // A deployed file that vanished from disk leaves the receipt rather than failing
                // the preset operation, exactly as the preset verbs always handled it.
                continue;
            }

            if (!targetTokens.Contains(file.Target) ||
                !file.RelativePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
                IsSkillPath(file.RelativePath))
            {
                rewritten.Add(new SquadDeploymentFile(file.RelativePath, rawBytes, file.Target));
                continue;
            }

            string fileText = Encoding.UTF8.GetString(rawBytes);
            if (!SquadFrontmatterHelper.TryParseFrontmatter(fileText, out string frontmatter, out string body,
                    out string newline))
            {
                rewritten.Add(new SquadDeploymentFile(file.RelativePath, rawBytes, file.Target));
                continue;
            }

            string agentName = SquadFrontmatterHelper.ExtractField(frontmatter, "name")
                               ?? SquadFrontmatterHelper.ResolveIdentity(file.RelativePath);
            string? category = SquadFrontmatterHelper.ExtractField(frontmatter, "model-profile");
            if (category is null)
                agentProfiles.TryGetValue(agentName, out category);
            if (category is null || !profiles.Profiles.ContainsKey(category))
            {
                // The category names no known profile, so there is nothing to resolve it with:
                // adopted and unknown files keep their bytes and produce no diff row rather than
                // borrowing the general category. A known category that resolves to no token is
                // different — that is inherit, handled below.
                rewritten.Add(new SquadDeploymentFile(file.RelativePath, rawBytes, file.Target));
                continue;
            }

            string? currentModel = SquadFrontmatterHelper.ExtractField(frontmatter, "model");
            string? newModel = SquadModelResolution.ResolveToken(profiles, category, file.Target);
            if (string.Equals(currentModel, newModel, StringComparison.Ordinal))
            {
                // The bytes stay identical rather than rebuilt so an already-correct file is
                // never touched and produces no diff row.
                rewritten.Add(new SquadDeploymentFile(file.RelativePath, rawBytes, file.Target));
                continue;
            }

            diffs.Add(new SquadModelDiffItem(agentName, currentModel, newModel));

            string updatedFrontmatter = SquadFrontmatterHelper.UpdateFrontmatterModel(frontmatter, newModel, newline);
            string updatedContent = $"---{newline}{updatedFrontmatter}{newline}---{body}";
            rewritten.Add(new SquadDeploymentFile(file.RelativePath, Encoding.UTF8.GetBytes(updatedContent),
                file.Target));
        }

        diffs.Sort((a, b) => StringComparer.Ordinal.Compare(a.Agent, b.Agent));
        return new SquadPresetRewriteResult(rewritten, diffs);
    }

    /// <summary>
    ///     Reports whether a receipt relative path addresses a skill file rather than an agent file.
    /// </summary>
    /// <param name="relativePath">The receipt relative path to classify.</param>
    /// <returns>
    ///     True when the final segment is <c>SKILL.md</c> or any segment equals <c>skills</c>,
    ///     compared case-insensitively over both path separators.
    /// </returns>
    /// <remarks>
    ///     Every renderer emits canonical skills and primary-agent entry-point skill projections
    ///     under a <c>skills</c> directory, so path shape covers both without consulting the
    ///     roster, where an agent and its projection share one name.
    /// </remarks>
    public static bool IsSkillPath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        string[] segments = relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return false;
        if (segments[^1].Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
            return true;
        foreach (string segment in segments)
        {
            if (segment.Equals("skills", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
