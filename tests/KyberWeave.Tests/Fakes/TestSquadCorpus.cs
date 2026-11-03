using System.Collections.Concurrent;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using KyberWeave.Core.Squad.Rendering;

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
