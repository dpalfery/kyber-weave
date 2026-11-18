using System.Collections.Concurrent;

namespace KyberWeave.Tests.Fakes;

/// <summary>
///     The <see cref="RunDeliveryTests" /> repositories: one read-only template per state the plan can be in, built
///     once by real git and copied into each test's own scratch directory.
/// </summary>
/// <remarks>
///     <para>
///         A delivery test used to build its own repository and bare remote with git, then run git again to move the
///         plan into the archive. That is a clone and a handful of child processes before every test, and no delivery
///         test decides anything about it. The class therefore builds three templates once: the run repository with the
///         plan still active, with the archive move staged, and with it committed.
///     </para>
///     <para>
///         A copy is a genuine repository with its own history and its own bare remote, so every test still commits,
///         pushes and reads refs for real. The only thing a copy records differently is nothing: the push URL is
///         <c>../remote.git</c>, which git resolves against the checkout it runs in, so the remote beside a copy is the
///         one that copy reaches. A template is only ever read, and the class removes them when it finishes.
///     </para>
/// </remarks>
public sealed class RunDeliveryRepositoryTemplate : IDisposable
{
    private const string ActivePlan = "docs/plans/2026-11-13-widget-export.md";
    private const string ArchivedPlan = "docs/archive/plans/2026-11-13-widget-export.md";

    private readonly ConcurrentDictionary<PlanState, Lazy<string>> _templates = new();
    private readonly TempDirectory _root = new();
    private readonly Lazy<string> _seed;

    /// <summary>Builds the seed the three templates are derived from, on first use.</summary>
    public RunDeliveryRepositoryTemplate()
    {
        _seed = new Lazy<string>(() => BuildSeed());
    }

    /// <summary>The state of the plan a delivery test starts from.</summary>
    public enum PlanState
    {
        /// <summary>The plan is still active: the run has not archived it.</summary>
        Active,

        /// <summary>The plan has been moved into the archive and the move is staged, as the archive tool leaves it.</summary>
        ArchivedStaged,

        /// <summary>The plan has been moved into the archive and the move is committed.</summary>
        ArchivedCommitted,
    }

    /// <summary>
    ///     Copies the template for <paramref name="state" /> into a test's own working directory and bare remote.
    /// </summary>
    /// <param name="state">The state of the plan the test starts from.</param>
    /// <param name="workPath">The checkout the copy is written to, created when absent.</param>
    /// <param name="remotePath">The bare remote the copy is written to, created when absent.</param>
    public void Copy(PlanState state, string workPath, string remotePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);

        string template = _templates.GetOrAdd(state, key => new Lazy<string>(() => Build(key))).Value;
        TestGitRepositoryTemplate.CopyDirectory(Path.Combine(template, "remote.git"), remotePath);
        TestGitRepositoryTemplate.CopyDirectory(Path.Combine(template, "run-delivery-model"), workPath);
    }

    /// <summary>Removes the templates, which no test has touched.</summary>
    public void Dispose()
    {
        // Git writes its object files read-only, which blocks the recursive delete on Windows.
        foreach (string file in Directory.EnumerateFiles(_root.Path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        _root.Dispose();
    }

    private string BuildSeed()
    {
        string seed = Path.Combine(_root.Path, "seed");
        Directory.CreateDirectory(seed);
        TestGitRepositoryTemplate.InitializeRunDeliverySeedTemplate(seed);
        return seed;
    }

    private string Build(PlanState state)
    {
        string template = Path.Combine(_root.Path, state.ToString());
        Directory.CreateDirectory(template);
        TestGitRepositoryTemplate.CopyDirectory(_seed.Value, template);
        if (state == PlanState.Active)
            return template;

        string model = Path.Combine(template, "run-delivery-model");
        TestGitRepositoryTemplate.RunGit(model, "mv", ActivePlan, ArchivedPlan);
        if (state == PlanState.ArchivedCommitted)
            TestGitRepositoryTemplate.Commit(model, "docs(plan): archive widget-export plan");
        return template;
    }
}
