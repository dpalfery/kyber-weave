using System.Text;
using System.Text.RegularExpressions;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Rendering;

namespace KyberWeave.Core.Squad.Deployment;

/// <summary>
/// Injects the shared worker baseline into every dispatched worker's canonical body, so the
/// always-on rules reach each harness's render from one source (decision D27).
/// </summary>
/// <remarks>
/// <para>
/// The rules the baseline carries were repeated in 27 files, and agent-file text is re-sent on every
/// round trip of every worker. They are written once, in
/// <c>products/kyber-squad/shared/worker-baseline.md</c>, and applied here as the pipeline loads its
/// source. The alternative — injecting into the finished render — needs a list of the file that
/// carries each agent's body, and that file differs in thirteen ways: an agent Markdown file, a
/// lowered role skill, a TOML document. A second such list would be a second source of truth that
/// goes wrong the day a harness ships.
/// </para>
/// <para>
/// Only a dispatched worker (<see cref="SquadInvocation.Subagent"/>) carries the block. The
/// conductor, bug-crusher and product-owner are session entry points that read the plan or the
/// specification as part of their own job, so the plan-read rule would contradict the body it was
/// pasted into.
/// </para>
/// <para>
/// <see cref="SquadAgent.BodyDigest"/> is deliberately not recomputed. It identifies the authored
/// body, which degradation records cite, not the render-time projection.
/// </para>
/// </remarks>
public static class SquadWorkerBaselineInjector
{
    /// <summary>The baseline's path below a Squad source root, relative and portable.</summary>
    public const string RelativePath = "shared/worker-baseline.md";

    /// <summary>The tag that makes the injected paragraph findable and countable in a rendered body.</summary>
    public const string TagName = "worker-baseline";

    /// <summary>
    /// Where the baseline names the directory the harness's skills were installed to; the render
    /// pipeline answers it per harness and scope through <see cref="BindSkillsRoot"/>.
    /// </summary>
    public const string SkillsRootPlaceholder = "{{skills-root}}";

    private const string BlockOpen = "<" + TagName + ">";
    private const string BlockClose = "</" + TagName + ">";

    /// <summary>
    /// Takes the injection block out of the baseline file's text.
    /// </summary>
    /// <param name="text">The baseline file's text.</param>
    /// <param name="path">The file's path, named in the errors.</param>
    /// <returns>The block, tags included.</returns>
    /// <exception cref="SquadRenderValidationException">The file does not carry exactly one non-empty block.</exception>
    public static string ExtractBlock(string text, string path)
    {
        int openIndex = text.IndexOf(BlockOpen, StringComparison.Ordinal);
        int closeIndex = openIndex < 0
            ? -1
            : text.IndexOf(BlockClose, openIndex + BlockOpen.Length, StringComparison.Ordinal);

        if (openIndex < 0 || closeIndex < 0)
        {
            throw new SquadRenderValidationException(
                $"The worker baseline at '{path}' must carry one {BlockOpen} block; the block is the text every " +
                "worker render receives, so text outside it never reaches a worker.");
        }

        if (text.IndexOf(BlockOpen, openIndex + BlockOpen.Length, StringComparison.Ordinal) >= 0 ||
            text.IndexOf(BlockClose, closeIndex + BlockClose.Length, StringComparison.Ordinal) >= 0)
        {
            throw new SquadRenderValidationException(
                $"The worker baseline at '{path}' carries more than one {BlockOpen} block. One block, injected " +
                "once per render, is the invariant this file exists to keep.");
        }

        string block = text[(openIndex + BlockOpen.Length)..closeIndex].Trim();
        if (block.Length == 0)
        {
            throw new SquadRenderValidationException(
                $"The {BlockOpen} block at '{path}' is empty, so every worker would carry an empty paragraph.");
        }

        return string.Concat(BlockOpen, "\n", block, "\n", BlockClose);
    }

    /// <summary>
    /// Answers <see cref="SkillsRootPlaceholder"/> in <paramref name="block"/> with the directory this
    /// render's skills install to. With no root, the paragraph that carries the placeholder is
    /// dropped, so a rendered body never ships an unanswered placeholder or a guessed path.
    /// </summary>
    /// <param name="block">The block from the file reader; null or blank comes back as is.</param>
    /// <param name="skillsRoot">The install root, or null when this render cannot know it.</param>
    /// <returns>The block ready for <see cref="Apply"/>.</returns>
    public static string? BindSkillsRoot(string? block, string? skillsRoot)
    {
        if (string.IsNullOrWhiteSpace(block))
        {
            return block;
        }

        if (skillsRoot is not null)
        {
            return block.Replace(SkillsRootPlaceholder, skillsRoot, StringComparison.Ordinal);
        }

        string[] paragraphs = Regex.Split(block, @"(?:\r?\n)[ \t]*(?:\r?\n)");
        return string.Join(
            "\n\n",
            paragraphs.Where(paragraph => !paragraph.Contains(SkillsRootPlaceholder, StringComparison.Ordinal)));
    }

    /// <summary>
    /// Returns <paramref name="source"/> with the block in every dispatched worker's body, exactly
    /// once. A body that already carries a block has it replaced in place rather than appended to,
    /// so the count holds however the source was edited.
    /// </summary>
    /// <param name="source">The source the pipeline is about to render.</param>
    /// <param name="block">The block from the file reader; null or blank leaves the source untouched.</param>
    /// <returns>
    /// The source to render. A blank block returns the same instance, so a tree without a baseline
    /// renders byte for byte as it did before.
    /// </returns>
    public static SquadSource Apply(SquadSource source, string? block)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (string.IsNullOrWhiteSpace(block))
        {
            return source;
        }

        return source with
        {
            Agents =
            [
                .. source.Agents.Select(agent => agent.Invocation == SquadInvocation.Subagent
                    ? agent with { InstructionBody = WithBlock(agent.InstructionBody, block) }
                    : agent)
            ]
        };
    }

    private static string WithBlock(string body, string block)
    {
        int openIndex = body.IndexOf(BlockOpen, StringComparison.Ordinal);
        int closeIndex = openIndex < 0
            ? -1
            : body.IndexOf(BlockClose, openIndex + BlockOpen.Length, StringComparison.Ordinal);

        if (openIndex >= 0 && closeIndex < 0)
        {
            return string.Concat(body[..openIndex].TrimEnd(), "\n\n", block, "\n");
        }

        return closeIndex < 0
            ? string.Concat(body.TrimEnd(), "\n\n", block, "\n")
            : string.Concat(body[..openIndex], block, body[(closeIndex + BlockClose.Length)..]);
    }
}
