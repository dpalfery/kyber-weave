using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using Xunit;
using YamlDotNet.Core;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Review finding 589-A: a for-each question is keyed <c>{rule id}@{index}</c>. A host rule whose own id
/// contains <c>@</c> could equal one of those keys, so the provider's answers-by-rule map would keep one of
/// the two. Host rule ids may not contain <c>@</c>, which makes every expansion key unique.
/// </summary>
public sealed class ArbiterRuleIdAtSignTests
{
    [Fact]
    public void HostRuleLoad_IdWithAtSign_IsRejectedNamingTheAtSign()
    {
        YamlException exception = Assert.Throws<YamlException>(() => KyberWeaveConfigLoader.LoadFromYaml("""
            arbiter:
              rules:
                - id: HOST-A@1
                  trigger: delegate
            """));

        Assert.Contains(RuleValidator.MalformedSection, exception.Message, StringComparison.Ordinal);
        Assert.Contains("'@'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HostEntryValidation_IdWithAtSign_RaisesConfig001NamingTheAtSign()
    {
        IReadOnlyList<Diagnostic> diagnostics = RuleValidator.ValidateHostEntries(
            [new ArbiterRuleYaml { Id = "HOST-A@1", Trigger = "delegate" }],
            new HashSet<string>(StringComparer.Ordinal));

        Diagnostic config001 = Assert.Single(diagnostics, d => d.Code == RuleValidator.MalformedSection);
        Assert.Contains("'@'", config001.Message, StringComparison.Ordinal);
    }
}
