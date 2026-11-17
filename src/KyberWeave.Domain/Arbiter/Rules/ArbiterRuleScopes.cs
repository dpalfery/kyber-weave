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

        return Select(rules, ScopeOrder(technology, parents));
    }

    /// <summary>The rules that judge an event whose task is in any of <paramref name="technologies"/>.</summary>
    /// <remarks>
    /// This is the form the worker guard uses for a read-only dispatch: it reads the technology the dispatch named,
    /// and the technology's own and its ancestors' command allow-lists bind the agent. Passing null means every
    /// rule, which is the fail-closed reading when the dispatch names none or cannot be identified.
    /// </remarks>
    /// <param name="rules">Every rule the configuration loaded.</param>
    /// <param name="technologies">The technologies whose rules apply, or null for every rule.</param>
    /// <param name="parents">Each technology folder's <c>builds-on</c> parent, named by <see cref="KyberWeave.Core.Arbiter.ArbiterConfig.RuleFolderParents"/>.</param>
    public static IReadOnlyList<ArbiterRule> Applicable(
        IReadOnlyList<ArbiterRule> rules,
        IReadOnlyCollection<string>? technologies,
        IReadOnlyDictionary<string, string>? parents = null)
    {
        ArgumentNullException.ThrowIfNull(rules);

        if (technologies is null)
            return rules;

        List<string> order = [];
        foreach (string technology in technologies.Where(name => !string.IsNullOrWhiteSpace(name)))
        {
            foreach (string name in ScopeOrder(technology, parents))
            {
                if (!order.Contains(name, StringComparer.Ordinal))
                    order.Add(name);
            }
        }

        return Select(rules, order);
    }

    /// <summary>
    /// The rules of the most specific technology in <paramref name="order"/> for each id, plus the rules with no
    /// technology whose id no scoped rule overrides.
    /// </summary>
    /// <remarks>
    /// A technology folder may restate an id that a role folder or another technology folder already loaded. The
    /// scoped declaration is an override for its own technology: it shadows the earlier rule there and leaves that
    /// rule in force for every other technology. The order is the event's technology first, then each
    /// <c>builds-on</c> ancestor, so a child's declaration wins over its parent's.
    /// </remarks>
    private static IReadOnlyList<ArbiterRule> Select(IReadOnlyList<ArbiterRule> rules, IReadOnlyList<string> order)
    {
        if (order.Count == 0)
        {
            return [.. rules.Where(rule => rule.Technology is null)];
        }

        Dictionary<string, string> scoped = new(StringComparer.Ordinal);
        foreach (string technology in order)
        {
            foreach (ArbiterRule rule in rules)
            {
                if (string.Equals(rule.Technology, technology, StringComparison.Ordinal))
                    scoped.TryAdd(rule.Id, technology);
            }
        }

        return [.. rules.Where(rule => rule.Technology is null
            ? !scoped.ContainsKey(rule.Id)
            : scoped.TryGetValue(rule.Id, out string? winner)
                && string.Equals(winner, rule.Technology, StringComparison.Ordinal))];
    }

    /// <summary>
    /// <paramref name="technology"/> first, then every ancestor its standards name, most specific first. Empty
    /// when it names none.
    /// </summary>
    private static IReadOnlyList<string> ScopeOrder(
        string? technology,
        IReadOnlyDictionary<string, string>? parents)
    {
        List<string> order = [];
        if (string.IsNullOrWhiteSpace(technology))
            return order;

        HashSet<string> seen = new(StringComparer.Ordinal);
        string current = technology.Trim();
        while (seen.Add(current))
        {
            order.Add(current);
            if (parents is null
                || !parents.TryGetValue(current, out string? parent)
                || string.IsNullOrWhiteSpace(parent))
            {
                break;
            }

            current = parent.Trim();
        }

        return order;
    }
}
