using System.Collections.Concurrent;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Verifies the isolation and caching guarantees of test fixture fakes.
/// </summary>
public sealed class TestFixtureFakesTests
{
    /// <summary>
    /// Keys whose sanitized representations would collide must still map to isolated template directories.
    /// </summary>
    [Fact]
    public void TemplateKeysCollidingOnSeparatorSanitizationYieldDistinctIsolatedRepositories()
    {
        using TempDirectory temp = new();
        string destColon = Path.Combine(temp.Path, "dest-colon");
        string destUnderscore = Path.Combine(temp.Path, "dest-underscore");

        TestGitRepositoryTemplate.CreateRepositoryCopy(
            "a:b",
            destColon,
            static dir => File.WriteAllText(Path.Combine(dir, "marker-colon.txt"), "colon"));

        TestGitRepositoryTemplate.CreateRepositoryCopy(
            "a_b",
            destUnderscore,
            static dir => File.WriteAllText(Path.Combine(dir, "marker-underscore.txt"), "underscore"));

        Assert.True(File.Exists(Path.Combine(destColon, "marker-colon.txt")));
        Assert.False(File.Exists(Path.Combine(destColon, "marker-underscore.txt")));

        Assert.True(File.Exists(Path.Combine(destUnderscore, "marker-underscore.txt")));
        Assert.False(File.Exists(Path.Combine(destUnderscore, "marker-colon.txt")));
    }

    /// <summary>
    /// A key containing backslashes must sanitize path separators so the template directory sits directly under the template root.
    /// </summary>
    [Fact]
    public void TemplateKeyContainingBackslashYieldsDirectoryDirectlyUnderTemplateRoot()
    {
        using TempDirectory temp = new();
        string dest = Path.Combine(temp.Path, "dest-slash");
        string capturedTemplateDir = string.Empty;

        TestGitRepositoryTemplate.CreateRepositoryCopy(
            @"sub\item",
            dest,
            dir =>
            {
                capturedTemplateDir = dir;
                File.WriteAllText(Path.Combine(dir, "marker.txt"), "ok");
            });

        Assert.NotEmpty(capturedTemplateDir);
        string leaf = Path.GetFileName(capturedTemplateDir);
        Assert.DoesNotContain('\\', leaf);
        Assert.DoesNotContain('/', leaf);
        Assert.Equal(TestGitRepositoryTemplate.TemplateRoot, Path.GetDirectoryName(capturedTemplateDir));
    }

    /// <summary>
    /// A faulted repository template initialization must throw, evict the lazy entry from cache,
    /// and clean up any partial directory so a subsequent attempt retries, succeeds, and avoids copying partial files.
    /// </summary>
    [Fact]
    public void FaultedTemplateThrowsEvictsFromCacheAndCleansUpDirectoryAllowingSubsequentCallToSucceed()
    {
        using TempDirectory temp = new();
        string dest1 = Path.Combine(temp.Path, "dest-1");
        string dest2 = Path.Combine(temp.Path, "dest-2");
        string key = "faulted-template-" + Guid.NewGuid().ToString("N");

        int callCount = 0;

        void Initialize(string dir)
        {
            callCount++;
            if (callCount == 1)
            {
                File.WriteAllText(Path.Combine(dir, "partial.txt"), "partial content");
                throw new InvalidOperationException("Simulated template initialization fault");
            }

            File.WriteAllText(Path.Combine(dir, "valid.txt"), "valid content");
        }

        Assert.Throws<InvalidOperationException>(() =>
            TestGitRepositoryTemplate.CreateRepositoryCopy(key, dest1, Initialize));
        Assert.Equal(1, callCount);

        TestGitRepositoryTemplate.CreateRepositoryCopy(key, dest2, Initialize);

        Assert.Equal(2, callCount);
        Assert.True(File.Exists(Path.Combine(dest2, "valid.txt")));
        Assert.False(File.Exists(Path.Combine(dest2, "partial.txt")));
    }

    /// <summary>
    /// A faulted render must throw and be evicted from cache so a subsequent call can retry and succeed.
    /// </summary>
    [Fact]
    public async Task FaultedRenderThrowsAndEvictsFromCacheAllowingSubsequentCallToSucceed()
    {
        ConcurrentDictionary<(SquadTarget Target, SquadDeploymentScope Scope), Task<SquadRenderResult>> testCache =
            new();
        SquadTarget target = SquadTarget.Antigravity;
        SquadDeploymentScope scope = SquadDeploymentScope.Project;

        int callCount = 0;

        Task<SquadRenderResult> FailingFactory(SquadTarget t, SquadDeploymentScope s)
        {
            callCount++;
            return Task.FromException<SquadRenderResult>(new InvalidOperationException("Simulated render fault"));
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TestSquadCorpus.GetDefaultRenderAsync(target, scope, FailingFactory, testCache));
        Assert.Equal(1, callCount);

        SquadRenderResult expectedSuccess = new(true, [], [], [], []);

        Task<SquadRenderResult> SucceedingFactory(SquadTarget t, SquadDeploymentScope s)
        {
            callCount++;
            return Task.FromResult(expectedSuccess);
        }

        SquadRenderResult actualResult = await TestSquadCorpus.GetDefaultRenderAsync(
            target,
            scope,
            SucceedingFactory,
            testCache);

        Assert.Same(expectedSuccess, actualResult);
        Assert.Equal(2, callCount);
    }

    /// <summary>
    /// Successful renders remain cached and do not re-invoke the render factory.
    /// </summary>
    [Fact]
    public async Task SuccessfulRendersAreCachedAndDoNotReinvokeFactory()
    {
        ConcurrentDictionary<(SquadTarget Target, SquadDeploymentScope Scope), Task<SquadRenderResult>> testCache =
            new();
        SquadTarget target = SquadTarget.Claude;
        SquadDeploymentScope scope = SquadDeploymentScope.Global;

        int callCount = 0;
        SquadRenderResult expectedSuccess = new(true, [], [], [], []);

        Task<SquadRenderResult> Factory(SquadTarget t, SquadDeploymentScope s)
        {
            callCount++;
            return Task.FromResult(expectedSuccess);
        }

        SquadRenderResult firstResult = await TestSquadCorpus.GetDefaultRenderAsync(target, scope, Factory, testCache);
        SquadRenderResult secondResult = await TestSquadCorpus.GetDefaultRenderAsync(target, scope, Factory, testCache);

        Assert.Same(expectedSuccess, firstResult);
        Assert.Same(expectedSuccess, secondResult);
        Assert.Equal(1, callCount);
    }
}
