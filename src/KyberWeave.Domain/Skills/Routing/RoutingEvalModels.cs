using System.Collections.ObjectModel;
using YamlDotNet.Serialization;

namespace KyberWeave.Core.Skills.Routing;

/// <summary>
/// One routing expectation. An <see cref="Expected"/> that is null/empty/"none" asserts a
/// NEGATIVE case: the prompt should fire NO skill (the often-untested failure mode).
/// </summary>
public sealed class RoutingEvalCase
{
    [YamlMember(Alias = "prompt")] public string Prompt { get; init; } = string.Empty;
    [YamlMember(Alias = "expected")] public string? Expected { get; init; }

    [YamlIgnore]
    public bool ExpectsNoFire =>
        string.IsNullOrWhiteSpace(Expected) || Expected!.Equals("none", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A routing eval set: the cases one eval file declares.</summary>
public sealed class RoutingEvalFile
{
    [YamlMember(Alias = "cases")] public Collection<RoutingEvalCase> Cases { get; init; } = [];
}

/// <summary>One eval case's outcome against the strategy under test.</summary>
public sealed record RoutingCaseResult(RoutingEvalCase Case, RoutingResult Result, bool Passed)
{
    public string ActualLabel => Result.Fired ? Result.SelectedSkill! : "(no fire)";
    public string ExpectedLabel => Case.ExpectsNoFire ? "(no fire)" : Case.Expected!;
}

/// <summary>The accuracy of a routing eval set: how many cases passed, out of how many.</summary>
/// <remarks>
/// An eval set with no cases scores zero, not one: a file whose <c>cases:</c> key is missing
/// or misspelled, or one that holds no case, would otherwise report a perfect score and pass
/// the <c>route</c> gate without testing anything.
/// </remarks>
public sealed record RoutingEvalSummary(IReadOnlyList<RoutingCaseResult> Results)
{
    public int Total => Results.Count;
    public int Passed => Results.Count(r => r.Passed);
    public double Accuracy => Total == 0 ? 0.0 : (double)Passed / Total;
}
