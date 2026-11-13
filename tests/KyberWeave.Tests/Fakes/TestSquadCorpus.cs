using System.Collections.Concurrent;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using KyberWeave.Core.Squad.Rendering;
using Xunit;

namespace KyberWeave.Tests.Fakes;

/// <summary>
/// Provides a shared, thread-safe cached canonical <see cref="SquadSource"/> and read-only
/// baseline render results across test classes.
/// </summary>
/// <remarks>
/// Loading the 200+ canonical squad files from disk and rendering them for each test
/// was the primary bottleneck of the test suite. This fake caches the loaded source once
/// per test assembly run and provides thread-safe access to default renders. Callers
/// MUST treat returned objects as immutable and read-only.
/// </remarks>
public static class TestSquadCorpus
{
    private static readonly string ProductRoot =
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");

    private static readonly Lazy<SquadSource> LazyCanonicalSource =
        new(static () => SquadSourceLoader.Load(ProductRoot));

    private static readonly
        ConcurrentDictionary<(SquadTarget Target, SquadDeploymentScope Scope), Task<SquadRenderResult>>
        CachedRenders = new();

    /// <summary>
    /// Thread-safe lazy singleton loaded once from <c>products/kyber-squad</c>.
    /// </summary>
    public static SquadSource CanonicalSource => LazyCanonicalSource.Value;

    /// <summary>
    /// The canonical text as a render for <paramref name="target"/> must emit it: the corpus's
    /// <c>{{harness}}</c> placeholder replaced with that target's own token.
    /// </summary>
    /// <remarks>
    /// The renderer answers the placeholder once, in the shared pipeline, so a contract test that
    /// compares rendered output against the canonical corpus byte for byte has to answer it here
    /// too. The replacement is written in this test helper rather than called from
    /// <c>SquadHarnessRenderer.SubstituteHarnessToken</c>: an expectation computed by the code
    /// under test passes whatever that code does, so the renderer could ship the braces and the
    /// suite would not notice. Only the token comes from the product, through
    /// <see cref="SquadTargetCatalog.GetToken"/>, which is the same catalogue the renderer reads —
    /// a hand-copied list of thirteen tokens would go stale the day one changed.
    /// </remarks>
    public static string ExpectedRenderText(SquadTarget target, string canonicalText)
    {
        ArgumentNullException.ThrowIfNull(canonicalText);
        return canonicalText.Replace("{{harness}}", SquadTargetCatalog.GetToken(target), StringComparison.Ordinal);
    }

    /// <summary>
    /// The canonical worker body as a subagent render carries it: the authored body plus the shared
    /// baseline block the renderer appends to every dispatched worker.
    /// </summary>
    /// <remarks>
    /// The block is read from the product file rather than produced by calling the injector, so this
    /// expectation cannot pass whatever the injector happens to do — the same reason
    /// <see cref="ExpectedRenderText" /> spells the substitution out. An entry-point body (the
    /// conductor, bug-crusher, product-owner) carries no block: those read the plan as their own job.
    /// </remarks>
    public static string ExpectedSubagentRenderText(SquadTarget target, string canonicalText)
    {
        string block = File.ReadAllText(
            Path.Combine(ProductRoot, "shared", "worker-baseline.md"));
        int start = block.IndexOf("<worker-baseline>", StringComparison.Ordinal);
        int end = block.IndexOf("</worker-baseline>", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "products/kyber-squad/shared/worker-baseline.md must carry one tagged block.");

        return ExpectedRenderText(target, canonicalText)
            + Environment.NewLine
            + block[start..end].TrimEnd()
                .Replace(
                    SquadWorkerBaselineInjector.SkillsRootPlaceholder,
                    SquadRendererRegistry.ProjectSkillsRoot(target),
                    StringComparison.Ordinal)
            + Environment.NewLine
            + "</worker-baseline>";
    }

    /// <summary>
    /// The body text a render carries for <paramref name="agent" />: the authored body plus the shared
    /// baseline block when the agent is a dispatched worker, and the authored body alone when it is a
    /// session entry point, which reads the plan as part of its own job.
    /// </summary>
    public static string ExpectedBodyText(SquadTarget target, SquadAgent agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        return agent.Invocation == SquadInvocation.Subagent
            ? ExpectedSubagentRenderText(target, agent.InstructionBody)
            : ExpectedRenderText(target, agent.InstructionBody);
    }

    /// <summary>
    /// Gets a thread-safe cached baseline render for the specified target and deployment scope.
    /// Faulted renders are evicted so subsequent retries can succeed.
    /// </summary>
    public static async Task<SquadRenderResult> GetDefaultRenderAsync(SquadTarget target, SquadDeploymentScope scope)
    {
        return await GetDefaultRenderAsync(
            target,
            scope,
            static (t, s) => RenderCoreAsync(t, s),
            CachedRenders).ConfigureAwait(false);
    }

    internal static async Task<SquadRenderResult> GetDefaultRenderAsync(
        SquadTarget target,
        SquadDeploymentScope scope,
        Func<SquadTarget, SquadDeploymentScope, Task<SquadRenderResult>> renderFactory,
        ConcurrentDictionary<(SquadTarget Target, SquadDeploymentScope Scope), Task<SquadRenderResult>>? cache = null)
    {
        ConcurrentDictionary<(SquadTarget Target, SquadDeploymentScope Scope), Task<SquadRenderResult>> dictionary =
            cache ?? CachedRenders;
        (SquadTarget Target, SquadDeploymentScope Scope) key = (target, scope);
        Task<SquadRenderResult> task = dictionary.GetOrAdd(
            key,
            _ => renderFactory(target, scope));

        try
        {
            return await task.ConfigureAwait(false);
        }
        catch
        {
            dictionary.TryRemove(KeyValuePair.Create(key, task));
            throw;
        }
    }

    /// <summary>
    /// Gets a thread-safe cached baseline project-scope render for the specified target.
    /// </summary>
    public static Task<SquadRenderResult> GetProjectRenderAsync(SquadTarget target)
    {
        return GetDefaultRenderAsync(target, SquadDeploymentScope.Project);
    }

    private static async Task<SquadRenderResult> RenderCoreAsync(SquadTarget target, SquadDeploymentScope scope)
    {
        ISquadGlobalRootResolver? globalRoots = scope == SquadDeploymentScope.Global
            ? SquadCommandComposition.ResolveGlobalRoots()
            : null;

        SquadRenderRequest request = new(
            ProductRoot,
            [target],
            scope,
            GlobalRoots: globalRoots,
            PreloadedSource: CanonicalSource);

        ISquadRenderer renderer = SquadCommandComposition.ResolveRenderer();
        SquadRenderResult result = await renderer.RenderAsync(request).ConfigureAwait(false);
        if (!result.Success || result.Errors.Count > 0)
        {
            throw new InvalidOperationException(
                $"Failed to render baseline for {target} at {scope}: {string.Join("; ", result.Errors)}");
        }

        return result;
    }
}
