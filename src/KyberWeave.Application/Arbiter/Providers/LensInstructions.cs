using KyberWeave.Core.Arbiter.Rules;

namespace KyberWeave.Core.Arbiter.Providers;

/// <summary>Resolves <c>instructions-from: lens:</c> to the embedded lens text.</summary>
public static class LensInstructions
{
    private const string LensPrefix = "lens:";

    /// <summary>
    /// Returns the lens text a rule's question should carry. A bare <c>lens:</c> takes the
    /// lens from the <c>lens.name</c> fact. Null means the rule's own question stands: the
    /// rule names no lens, or names one the catalog does not hold.
    /// </summary>
    public static string? Resolve(ArbiterRule rule, ArbiterFactSet facts)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(facts);

        string? source = rule.Ask?.InstructionsFrom;
        if (source is null || !source.StartsWith(LensPrefix, StringComparison.Ordinal))
            return null;

        string name = source[LensPrefix.Length..];
        if (name.Length == 0
            && facts.TryGet("lens.name", out ArbiterFact? fact)
            && fact.Value is string fromFact)
        {
            name = fromFact;
        }

        return name.Length == 0 ? null : ArbiterSquadCatalog.EmbeddedLensText(name);
    }
}
