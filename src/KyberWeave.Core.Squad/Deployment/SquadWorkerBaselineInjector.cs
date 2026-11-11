using System.Collections.Concurrent;
using System.Text;
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
/// body that migration reports and degradation records record, not the render-time projection.
/// </para>
/// </remarks>
public static class SquadWorkerBaselineInjector
{
    /// <summary>The baseline's path below a Squad source root, relative and portable.</summary>
    public const string RelativePath = "shared/worker-baseline.md";

    /// <summary>The tag that makes the injected paragraph findable and countable in a rendered body.</summary>
    public const string TagName = "worker-baseline";

    private const string BlockOpen = "<" + TagName + ">";
    private const string BlockClose = "</" + TagName + ">";

    private const int MaxCacheEntries = 64;

    private sealed record CacheEntry(DateTime LastWriteTimeUtc, long FileLength, string? Block);

    private static readonly ConcurrentDictionary<string, CacheEntry> CachedBlocks = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>Empties the baseline block cache.</summary>
    public static void ClearCache() => CachedBlocks.Clear();

    /// <summary>
    /// Reads the injection block from <paramref name="sourceDirectory"/>, or returns null when the
    /// tree carries no baseline file.
    /// </summary>
    /// <remarks>
    /// A test fixture renders from a minimal source tree and has no baseline; a shipped corpus does,
    /// and the suite fails when a render carries no block rather than letting the render go quiet.
    /// </remarks>
    /// <param name="sourceDirectory">The Squad source root the render loaded from.</param>
    /// <returns>The block, tags included, or null when the tree carries no baseline file.</returns>
    public static string? ReadBlock(string sourceDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        string normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceDirectory));
        string path = Path.Combine(normalized, RelativePath.Replace('/', Path.DirectorySeparatorChar));

        try
        {
            FileInfo info = new(path);
            if (!info.Exists)
            {
                if (CachedBlocks.TryGetValue(normalized, out CacheEntry? missing) && missing.LastWriteTimeUtc == DateTime.MinValue)
                    return missing.Block;

                if (CachedBlocks.Count >= MaxCacheEntries)
                    CachedBlocks.Clear();

                CachedBlocks[normalized] = new CacheEntry(DateTime.MinValue, 0, null);
                return null;
            }

            DateTime lastWrite = info.LastWriteTimeUtc;
            long length = info.Length;
            if (CachedBlocks.TryGetValue(normalized, out CacheEntry? entry)
                && entry.LastWriteTimeUtc == lastWrite
                && entry.FileLength == length)
            {
                return entry.Block;
            }

            string? block = ReadBlockUncached(path);
            if (CachedBlocks.Count >= MaxCacheEntries)
                CachedBlocks.Clear();

            CachedBlocks[normalized] = new CacheEntry(lastWrite, length, block);
            return block;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ReadBlockUncached(path);
        }
    }

    private static string? ReadBlockUncached(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }

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
    /// Returns <paramref name="source"/> with the block in every dispatched worker's body, exactly
    /// once. A body that already carries a block has it replaced in place rather than appended to,
    /// so the count holds however the source was edited.
    /// </summary>
    /// <param name="source">The source the pipeline is about to render.</param>
    /// <param name="block">The block from <see cref="ReadBlock"/>; null or blank leaves the source untouched.</param>
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
