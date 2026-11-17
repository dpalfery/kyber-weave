namespace KyberWeave.Core.Arbiter.Rules;

/// <summary>
/// Selects the rules that judge one event, by the technology its task names.
/// </summary>
/// <remarks>
/// A rule loaded from a technology folder carries that folder's name in <see cref="ArbiterRule.Technology"/>. It
/// judges only an event whose task is in that technology, or in a technology that builds on it through its
/// standard's <c>builds-on</c> key, so a parent's rule reaches its child's task and a child's rule does not reach
/// its parent's. A rule with no technology came from a role folder or from <c>arbiter.rules</c> and judges every
/// event. An event that names no task technology, such as a task-less dispatch or the conductor's own action, is
/// judged by those rules alone.
/// </remarks>
public static class ArbiterRuleScopes
{
    /// <summary>The rules that judge an event whose task names <paramref name="technology"/>.</summary>
    /// <param name="rules">Every rule the configuration loaded.</param>
    /// <param name="technology">The event's task technology, or null when it names none.</param>
    /// <param name="parents">Each technology folder's <c>builds-on</c> parent, named by <see cref="KyberWeave.Core.Arbiter.ArbiterConfig.RuleFolderParents"/>.</param>
    public static IReadOnlyList<ArbiterRule> Applicable(
        IReadOnlyList<ArbiterRule> rules,
        string? technology,
        IReadOnlyDictionary<string, string>? parents = null)
    {
        ArgumentNullException.ThrowIfNull(rules);

        IReadOnlySet<string> scope = ScopeOf(technology, parents);
        return scope.Count == 0
            ? [.. rules.Where(rule => rule.Technology is null)]
            : [.. rules.Where(rule => rule.Technology is null || scope.Contains(rule.Technology))];
    }

    /// <summary>The rules that judge an event whose task is in any of <paramref name="technologies"/>.</summary>
    /// <remarks>
    /// This is the fail-closed form the worker guard uses for a read-only dispatch: the tool call it sees names no
    /// dispatch technology, so every technology's command allow-list still binds the agent. Passing null means
    /// every rule, which is the same fail-closed reading with no set to build.
    /// </remarks>
    /// <param name="rules">Every rule the configuration loaded.</param>
    /// <param name="technologies">The technologies whose rules apply, or null for every rule.</param>
    public static IReadOnlyList<ArbiterRule> Applicable(
        IReadOnlyList<ArbiterRule> rules,
        IReadOnlyCollection<string>? technologies)
    {
        ArgumentNullException.ThrowIfNull(rules);

        if (technologies is null)
            return rules;

        HashSet<string> scope = new(
            technologies.Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name.Trim()),
            StringComparer.Ordinal);
        return [.. rules.Where(rule => rule.Technology is null || scope.Contains(rule.Technology))];
    }

    /// <summary><paramref name="technology"/> and every ancestor its standards name, or none when it names none.</summary>
    private static IReadOnlySet<string> ScopeOf(
        string? technology,
        IReadOnlyDictionary<string, string>? parents)
    {
        HashSet<string> scope = new(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(technology))
            return scope;

        string current = technology.Trim();
        while (scope.Add(current)
               && parents is not null
               && parents.TryGetValue(current, out string? parent)
               && !string.IsNullOrWhiteSpace(parent))
        {
            current = parent.Trim();
        }

        return scope;
    }
}
