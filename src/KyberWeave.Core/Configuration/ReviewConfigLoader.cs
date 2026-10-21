using System.Collections.ObjectModel;
using System.Globalization;
using YamlDotNet.Core;

namespace KyberWeave.Core.Configuration;

/// <summary>Loads, merges, and validates the <c>review:</c> host configuration section.</summary>
public static class ReviewConfigLoader
{
    /// <summary>
    /// The only argv tokens the runner expands. Anything else in braces is refused here, because a
    /// misspelt token would otherwise reach the runner as a literal argument and fail far from the
    /// line that caused it.
    /// </summary>
    private static readonly string[] KnownTokens = ["changed", "task"];

    internal static ReviewConfig Merge(ReviewConfig defaults, ReviewYamlSection? section)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        if (section is null)
            return defaults;

        return new ReviewConfig
        {
            Gates = section.Gates is null ? defaults.Gates : ParseGates(section.Gates),
            TaskGates = section.TaskGates is null ? defaults.TaskGates : ParseTaskGates(section.TaskGates),
            Coverage = ParseCoverage(section.Coverage, defaults.Coverage),
            Duplicates = ParseDuplicates(section.Duplicates, defaults.Duplicates),
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

            int timeoutSeconds = gate.TimeoutSeconds ?? 900;
            if (timeoutSeconds <= 0)
            {
                throw new YamlException(
                    $"review.gates '{gate.Id}' timeout-seconds must be greater than zero.");
            }

            parsed.Add(new ReviewGate(gate.Id!, [.. gate.Run], gate.Blocking ?? true, timeoutSeconds));
        }

        return parsed;
    }

    /// <summary>
    /// Validates <c>review.task-gates</c>. Every refusal is raised here, where the operator wrote the
    /// entry, because a gate that only fails when a developer agent runs it would surface in the middle
    /// of someone else's task.
    /// </summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<ReviewTaskGate>> ParseTaskGates(
        Dictionary<string, List<ReviewTaskGateYaml>?> taskGates)
    {
        Dictionary<string, IReadOnlyList<ReviewTaskGate>> parsed = new(taskGates.Count, StringComparer.Ordinal);

        foreach (KeyValuePair<string, List<ReviewTaskGateYaml>?> entry in taskGates)
        {
            // The agent slug is looked up by name and reported under that name, so it has the same
            // lookup-name constraint as a gate id.
            if (!ConfigSlug.IsValid(entry.Key))
            {
                throw new YamlException(
                    $"review.task-gates agent '{entry.Key}' is not a valid lookup name. Use the agent's slug: " +
                    "lowercase alphanumerics separated by single hyphens, e.g. 'csharp-dev'.");
            }

            if (entry.Value is null || entry.Value.Count == 0)
            {
                throw new YamlException(
                    $"review.task-gates '{entry.Key}' declares no gates. Give it at least one gate, " +
                    "or remove the agent.");
            }

            parsed[entry.Key] = ParseAgentTaskGates(entry.Key, entry.Value);
        }

        return new ReadOnlyDictionary<string, IReadOnlyList<ReviewTaskGate>>(parsed);
    }

    private static IReadOnlyList<ReviewTaskGate> ParseAgentTaskGates(string agent, List<ReviewTaskGateYaml> gates)
    {
        List<ReviewTaskGate> parsed = new(gates.Count);
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (ReviewTaskGateYaml? gate in gates)
        {
            if (gate is null)
            {
                throw new YamlException(
                    $"review.task-gates '{agent}' has an empty gate entry. Each entry is a mapping with " +
                    "an id and a run command.");
            }

            if (gate.Id is not { } id)
            {
                throw new YamlException(
                    $"review.task-gates '{agent}' has a gate with no id. Give every gate a lookup-name id " +
                    "such as 'format-whitespace'.");
            }

            if (!ConfigSlug.IsValid(id))
            {
                throw new YamlException(
                    $"review.task-gates '{agent}' gate id '{id}' is not a valid lookup name. Use lowercase " +
                    "alphanumerics separated by single hyphens.");
            }

            // Gate ids are unique within an agent only: each agent's gates are reported as that agent's
            // list, so the same id under two agents is the natural shared shape and is allowed.
            if (!seen.Add(id))
                throw new YamlException($"review.task-gates '{agent}' declares gate '{id}' more than once.");

            string gateWhere = $"review.task-gates '{agent}' gate '{id}'";

            IReadOnlyList<string> run = ParseArgv(gate.Run, "run", gateWhere, true);
            IReadOnlyList<string> fix = ParseArgv(gate.Fix, "fix", gateWhere, false);

            if (gate.Include is not null && gate.Include.Exists(string.IsNullOrWhiteSpace))
                throw new YamlException($"{gateWhere} has an empty include pattern. Name a glob such as \"**/*.cs\".");

            if (gate.Os is not null && gate.Os is not ("windows" or "unix"))
            {
                throw new YamlException(
                    $"{gateWhere} os '{gate.Os}' is not recognised. Use 'windows' or 'unix', or omit os to " +
                    "run the gate on every platform.");
            }

            int timeoutSeconds = gate.Timeout ?? 900;
            if (timeoutSeconds <= 0)
                throw new YamlException($"{gateWhere} timeout must be greater than zero seconds.");

            IReadOnlyList<string> include = gate.Include is null ? [] : [.. gate.Include];
            parsed.Add(new ReviewTaskGate(id, include, gate.Os, run, fix, gate.Blocking ?? true, timeoutSeconds));
        }

        return parsed;
    }

    /// <summary>
    /// Validates one argv list. A required list must exist and name a command; an optional one may be
    /// absent but, when written, must not be empty, because an empty fix list would look like a fix
    /// that was declared and quietly does nothing.
    /// </summary>
    private static IReadOnlyList<string> ParseArgv(List<string>? argv, string field, string gateWhere, bool required)
    {
        string where = $"{gateWhere} {field}";

        if (argv is null)
        {
            if (required)
            {
                throw new YamlException(
                    $"{where} has no command. Declare it as a list: {field}: [dotnet, format, KyberWeave.sln].");
            }

            return [];
        }

        if (argv.Count == 0)
            throw new YamlException($"{where} is an empty list. Give it a command, or remove the key.");

        // The program is never expanded. A token there would let a changed file or a task slug choose
        // what executes, so tokens are taken only as arguments after the program.
        if (argv[0] is { } program && program.IndexOfAny(['{', '}']) >= 0)
        {
            throw new YamlException(
                $"{where} has a brace in its program name '{program}'. The program must be a literal, so pass " +
                "{changed} or {task} as a later argument.");
        }

        foreach (string? element in argv)
        {
            if (string.IsNullOrWhiteSpace(element))
                throw new YamlException(
                    $"{where} has an empty argument. Every argv element must name something to run.");

            ValidateTokens(element, where);
        }

        return [.. argv];
    }

    /// <summary>
    /// Refuses a brace token that the runner would not expand. <c>{changed}</c> is expanded only as a
    /// whole argument, because splicing a file list into a larger string would hand the runner a string
    /// it cannot reason about. <c>{task}</c> may sit inside an argument.
    /// </summary>
    private static void ValidateTokens(string element, string where)
    {
        // A character scan rather than string.IndexOf: the culture-neutral overloads read the same
        // on every platform, and the ordinal intent is explicit at each comparison.
        int open = -1;
        for (int i = 0; i < element.Length; i++)
        {
            if (element[i] == '{')
            {
                open = i;
                continue;
            }

            if (element[i] != '}' || open < 0)
                continue;

            string name = element[(open + 1)..i];
            string token = element[open..(i + 1)];
            open = -1;

            if (name == "task")
                continue;

            if (name == "changed")
            {
                if (token != element)
                {
                    throw new YamlException(
                        $"{where} embeds '{{changed}}' in '{element}'. {{changed}} is substituted only when it " +
                        "is a whole argument, so give it its own list element.");
                }

                continue;
            }

            throw new YamlException(
                $"{where} has unknown token '{token}' in '{element}'. Did you mean '{{" +
                NearestKnownToken(name) + "}'? Known tokens are {changed} (whole argument only) and {task}.");
        }
    }

    private static string NearestKnownToken(string name)
    {
        return KnownTokens.OrderBy(token => EditDistance(name, token)).First();
    }

    /// <summary>Levenshtein distance; small inputs only, used to suggest a token spelling.</summary>
    private static int EditDistance(string left, string right)
    {
        int[] previous = new int[right.Length + 1];
        int[] current = new int[right.Length + 1];
        for (int j = 0; j <= right.Length; j++)
            previous[j] = j;

        for (int i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= right.Length; j++)
            {
                int cost = left[i - 1] == right[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
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

    private static ReviewDuplicates ParseDuplicates(ReviewDuplicatesYaml? duplicates, ReviewDuplicates defaults)
    {
        // A threshold below two cannot mean anything: Normalize drops the signature line, so
        // a one-line body is a single statement and matches everywhere. A host that asks for
        // one is told, rather than silently given a different number.
        if (duplicates?.MinimumLines is not { } minimum)
            return defaults;
        if (minimum <= 1)
            throw new YamlException("review.duplicates.minimum-lines must be greater than one.");
        return new ReviewDuplicates(minimum);
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
