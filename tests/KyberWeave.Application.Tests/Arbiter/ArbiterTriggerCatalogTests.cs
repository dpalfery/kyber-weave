using KyberWeave.Core.Arbiter;
using Xunit;

namespace KyberWeave.Application.Tests.Arbiter;

/// <summary>
/// The family registry and the fact catalogue name the same triggers. The four family names the registry also
/// accepts are deliberate aliases: no dispatch raises them, and no shipped rule declares them.
/// </summary>
public sealed class ArbiterTriggerCatalogTests
{
    /// <summary>The family names the registry accepts as trigger names but the fact catalogue does not need.</summary>
    private static readonly string[] FamilyAliases =
    [
        ArbiterTriggers.ModelTarget,
        ArbiterTriggers.Shortcut,
        ArbiterTriggers.FailureCluster,
        ArbiterTriggers.Advice,
    ];

    [Fact]
    public void TheFactCatalogNamesEveryTriggerThatIsNotAFamilyAlias()
    {
        HashSet<string> families = [.. ArbiterTriggerFamilies.KnownTriggers
            .Where(trigger => !FamilyAliases.Contains(trigger, StringComparer.Ordinal))];
        HashSet<string> catalog = [.. ArbiterTriggerCatalog.FactsByTrigger.Keys];

        Assert.Equal(families.Order(StringComparer.Ordinal), catalog.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void EveryFamilyAliasIsAKnownTriggerWithNoFactRow()
    {
        foreach (string alias in FamilyAliases)
        {
            Assert.Contains(alias, ArbiterTriggerFamilies.KnownTriggers, StringComparer.Ordinal);
            Assert.False(ArbiterTriggerCatalog.FactsByTrigger.ContainsKey(alias), $"Alias '{alias}' gained a fact row.");
        }
    }
}
