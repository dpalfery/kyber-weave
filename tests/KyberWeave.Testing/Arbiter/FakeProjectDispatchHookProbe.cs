using System.Collections.ObjectModel;
using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// The one fake for <see cref="IProjectDispatchHookProbe"/>: a test names the harnesses a project covers, or
/// supplies the answer for each call, without a project hook file.
/// </summary>
/// <remarks>
/// A harness the test does not name answers true, the contract's conservative default for a harness the probe
/// does not read, so a test that is not about coverage keeps the existing deferral.
/// </remarks>
public sealed class FakeProjectDispatchHookProbe : IProjectDispatchHookProbe
{
    private readonly Dictionary<string, bool> _answers = new(StringComparer.Ordinal);

    /// <summary>Gets the calls the fake answered, in order.</summary>
    public Collection<(string ProjectRoot, string HarnessToken)> Calls { get; } = [];

    /// <summary>Gets or sets the answer for every call; null answers from <see cref="Cover"/> and <see cref="Uncover"/>.</summary>
    public Func<string, string, bool>? Answer { get; set; }

    /// <summary>Marks a harness as covered by a project hook.</summary>
    /// <param name="harnessToken">The harness token the project covers.</param>
    /// <returns>This fake, for chaining.</returns>
    public FakeProjectDispatchHookProbe Cover(string harnessToken)
    {
        _answers[harnessToken] = true;
        return this;
    }

    /// <summary>Marks a harness as not covered by a project hook.</summary>
    /// <param name="harnessToken">The harness token the project does not cover.</param>
    /// <returns>This fake, for chaining.</returns>
    public FakeProjectDispatchHookProbe Uncover(string harnessToken)
    {
        _answers[harnessToken] = false;
        return this;
    }

    /// <inheritdoc />
    public bool CoversHarness(string projectRoot, string harnessToken)
    {
        Calls.Add((projectRoot, harnessToken));
        return Answer?.Invoke(projectRoot, harnessToken) ?? _answers.GetValueOrDefault(harnessToken, true);
    }
}
