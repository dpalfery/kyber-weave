using System.Globalization;
using YamlDotNet.Core;

namespace KyberWeave.Core.Configuration;

/// <summary>Loads, merges, and validates the <c>review:</c> host configuration section.</summary>
public static class ReviewConfigLoader
{
    /// <summary>Loads review configuration from a combined Kyber-Weave YAML file.</summary>
    public static ReviewConfig LoadMerged(ReviewConfig defaults, string yamlPath)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        ArgumentException.ThrowIfNullOrWhiteSpace(yamlPath);

        KyberWeaveYamlDocument document = KyberWeaveYamlParser.ParseFile(yamlPath);
        return Merge(defaults, document.Review);
    }

    internal static ReviewConfig Merge(ReviewConfig defaults, ReviewYamlSection? section)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        if (section is null)
            return defaults;

        return new ReviewConfig
        {
            Gates = section.Gates is null ? defaults.Gates : ParseGates(section.Gates),
            Coverage = ParseCoverage(section.Coverage, defaults.Coverage),
            Policy = ParsePolicy(section.Policy, defaults.Policy)
        };
    }

    private static IReadOnlyList<ReviewGate> ParseGates(List<ReviewGateYaml> gates)
    {
        List<ReviewGate> parsed = new(gates.Count);
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (ReviewGateYaml gate in gates)
        {
            if (!ConfigSlug.IsValid(gate.Id))
            {
                throw new YamlException(
                    $"review.gates id '{gate.Id}' is not a valid lookup name. Use lowercase " +
                    "alphanumerics separated by single hyphens.");
            }

            // A gate id is cited in findings, reports, and suppressions, so two gates
            // sharing one makes every downstream reference ambiguous.
            if (!seen.Add(gate.Id!))
                throw new YamlException($"review.gates declares '{gate.Id}' more than once.");

            if (gate.Run is null || gate.Run.Count == 0)
            {
                throw new YamlException(
                    $"review.gates '{gate.Id}' has no run command. Declare it as a list: " +
                    "run: [dotnet, test, -c, Release].");
            }

            if (gate.Run.Exists(string.IsNullOrWhiteSpace))
                throw new YamlException($"review.gates '{gate.Id}' has an empty run argument.");

            parsed.Add(new ReviewGate(gate.Id!, [.. gate.Run], gate.Blocking ?? true));
        }

        return parsed;
    }

    private static ReviewCoverage ParseCoverage(ReviewCoverageYaml? coverage, ReviewCoverage defaults)
    {
        if (coverage is null)
            return defaults;

        double file = coverage.FileLinePercent ?? defaults.FileLinePercent;
        double @class = coverage.ClassLinePercent ?? defaults.ClassLinePercent;

        if (file is < 0 or > 100)
            throw new YamlException("review.coverage.file-line-percent must be between 0 and 100.");
        if (@class is < 0 or > 100)
            throw new YamlException("review.coverage.class-line-percent must be between 0 and 100.");

        return new ReviewCoverage(file, @class);
    }

    private static ReviewPolicy ParsePolicy(ReviewPolicyYaml? policy, ReviewPolicy defaults)
    {
        if (policy is null)
            return defaults;

        int maxLines = policy.MaxReviewableLines ?? defaults.MaxReviewableLines;
        if (maxLines <= 0)
            throw new YamlException("review.policy.max-reviewable-lines must be greater than zero.");

        int majorBlocks = policy.MajorCountBlocks ?? defaults.MajorCountBlocks;
        if (majorBlocks <= 0)
            throw new YamlException("review.policy.major-count-blocks must be greater than zero.");

        int minConfidence = policy.MinConfidence ?? defaults.MinConfidence;
        if (minConfidence is < 1 or > 10)
            throw new YamlException("review.policy.min-confidence must be between 1 and 10.");

        return new ReviewPolicy
        {
            AlwaysHuman = policy.AlwaysHuman is null ? defaults.AlwaysHuman : [.. policy.AlwaysHuman],
            MaxReviewableLines = maxLines,
            MajorCountBlocks = majorBlocks,
            MinConfidence = minConfidence,
            Suppressions = policy.Suppressions is null
                ? defaults.Suppressions
                : ParseSuppressions(policy.Suppressions)
        };
    }

    private static IReadOnlyList<ReviewSuppression> ParseSuppressions(List<ReviewSuppressionYaml> suppressions)
    {
        List<ReviewSuppression> parsed = new(suppressions.Count);

        foreach (ReviewSuppressionYaml suppression in suppressions)
        {
            if (string.IsNullOrWhiteSpace(suppression.Id))
                throw new YamlException("review.policy.suppressions requires an id on every entry.");

            // A suppression without a stated reason is indistinguishable from a mistake by
            // the time anyone reads it, and nobody can judge whether it is still warranted.
            if (string.IsNullOrWhiteSpace(suppression.Reason))
            {
                throw new YamlException(
                    $"review.policy.suppressions '{suppression.Id}' has no reason. A suppression " +
                    "nobody can evaluate later is a permanent one.");
            }

            if (!DateOnly.TryParseExact(
                    suppression.Expires,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateOnly expires))
            {
                throw new YamlException(
                    $"review.policy.suppressions '{suppression.Id}' needs an expires date as " +
                    "yyyy-MM-dd. Suppressions do not last forever.");
            }

            parsed.Add(new ReviewSuppression(suppression.Id!, suppression.Reason!, expires));
        }

        return parsed;
    }
}
