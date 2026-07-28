using KyberWeave.Core.Skills.Model;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace KyberWeave.Core.Skills.Routing;

/// <summary>
/// One routing expectation. An <see cref="Expected"/> that is null/empty/"none" asserts a
/// NEGATIVE case: the prompt should fire NO skill (the often-untested failure mode).
/// </summary>
public sealed class RoutingEvalCase
{
    [YamlMember(Alias = "prompt")] public string Prompt { get; set; } = string.Empty;
    [YamlMember(Alias = "expected")] public string? Expected { get; set; }

    [YamlIgnore]
    public bool ExpectsNoFire =>
        string.IsNullOrWhiteSpace(Expected) || Expected!.Equals("none", StringComparison.OrdinalIgnoreCase);
}

public sealed class RoutingEvalFile
{
    [YamlMember(Alias = "cases")] public List<RoutingEvalCase> Cases { get; set; } = new();

    public static RoutingEvalFile Load(string path)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        return deserializer.Deserialize<RoutingEvalFile>(File.ReadAllText(path)) ?? new RoutingEvalFile();
    }
}

public sealed record RoutingCaseResult(RoutingEvalCase Case, RoutingResult Result, bool Passed)
{
    public string ActualLabel => Result.Fired ? Result.SelectedSkill! : "(no fire)";
    public string ExpectedLabel => Case.ExpectsNoFire ? "(no fire)" : Case.Expected!;
}

public sealed record RoutingEvalSummary(IReadOnlyList<RoutingCaseResult> Results)
{
    public int Total => Results.Count;
    public int Passed => Results.Count(r => r.Passed);
    public double Accuracy => Total == 0 ? 1.0 : (double)Passed / Total;
}

/// <summary>Runs a routing eval set against a skill set and reports accuracy.</summary>
public sealed class RoutingEvaluator
{
    private readonly IRoutingStrategy _strategy;
    public RoutingEvaluator(IRoutingStrategy strategy) => _strategy = strategy;

    public RoutingEvalSummary Evaluate(RoutingEvalFile evalFile, SkillSet skills)
    {
        var results = new List<RoutingCaseResult>();
        foreach (var c in evalFile.Cases)
        {
            var result = _strategy.Route(c.Prompt, skills);
            bool passed = c.ExpectsNoFire
                ? !result.Fired
                : result.Fired && string.Equals(result.SelectedSkill, c.Expected, StringComparison.OrdinalIgnoreCase);
            results.Add(new RoutingCaseResult(c, result, passed));
        }
        return new RoutingEvalSummary(results);
    }
}
