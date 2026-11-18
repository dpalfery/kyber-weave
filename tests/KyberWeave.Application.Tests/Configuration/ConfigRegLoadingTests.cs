using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Scaffolding;
using KyberWeave.Core.Docs.Validation;
using Xunit;
using YamlDotNet.Core;
using KyberWeave.Tests;

namespace KyberWeave.Application.Tests.Configuration;

/// <summary>
/// Registry and technology values become directory names and the lookup names skills type by
/// hand, so both are constrained where the operator wrote them rather than where something
/// later builds a path out of them.
/// </summary>
public class ConfigRegLoadingTests
{
    private static YamlException AssertInvalid(string yaml) =>
        Assert.ThrowsAny<YamlException>(() => KyberWeaveConfigAssembler.FromYaml(yaml));

    [Fact]
    public void ReadsHostAdditionsInTheOrderTheyWereWritten()
    {
        KyberWeaveConfig config = KyberWeaveConfigAssembler.FromYaml("""
            config-reg:
              auth-design: docs/reference/auth-design.md
              azure-naming-standard: docs/reference/azure-naming.md
            """);

        Assert.Equal(
            ["auth-design", "azure-naming-standard"],
            config.ConfigReg.Additions.Select(e => e.Name));
    }

    [Theory]
    [InlineData("supervised")]
    [InlineData("autonomous")]
    public void ReadsTheAutonomySetting(string value)
    {
        KyberWeaveConfig config = KyberWeaveConfigAssembler.FromYaml($"autonomy: {value}\n");

        Assert.Equal(value, config.ConfigReg.Autonomy);
    }

    [Fact]
    public void AutonomyIsUnsetByDefault()
    {
        Assert.Null(KyberWeaveConfigDefaults.Product.ConfigReg.Autonomy);
        Assert.Null(KyberWeaveConfigAssembler.FromYaml("config-reg:\n  x: docs/x.md\n").ConfigReg.Autonomy);
    }

    [Theory]
    [InlineData("yolo")]
    [InlineData("Autonomous")]
    [InlineData("true")]
    public void RejectsAnAutonomyValueThatIsNotOneOfTheTwo(string value)
    {
        string message = AssertInvalid($"autonomy: {value}\n").Message;

        Assert.Contains("autonomy", message, StringComparison.Ordinal);
        Assert.Contains("supervised", message, StringComparison.Ordinal);
        Assert.Contains("autonomous", message, StringComparison.Ordinal);
    }

    [Fact]
    public void AutonomyIsAReservedNameInTheConfigRegPathMap()
    {
        string message = AssertInvalid("config-reg:\n  autonomy: docs/x.md\n").Message;

        Assert.Contains("reserved", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A value outside <c>done</c> and <c>declined</c> is refused, naming both, the way <c>autonomy:</c> refuses a
    /// value outside its two words.
    /// </summary>
    /// <remarks>
    /// The setting decides whether <c>run start</c> stops to offer onboarding, so a typo that was silently accepted
    /// would leave a repository that believes it onboarded asking again on every run — or one that declined never
    /// recording it. The refusal has to name the two words, or the operator has no way to write the line correctly.
    /// </remarks>
    [Theory]
    [InlineData("yes")]
    [InlineData("Done")]
    [InlineData("true")]
    public void RejectsAnOnboardingValueThatIsNotOneOfTheTwo(string value)
    {
        string message = AssertInvalid($"onboarding: {value}\n").Message;

        Assert.Contains("onboarding", message, StringComparison.Ordinal);
        Assert.Contains("done", message, StringComparison.Ordinal);
        Assert.Contains("declined", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>onboarding</c> publishes a mode property of its own, so a host may not declare that name as a path.
    /// </summary>
    /// <remarks>
    /// The loader's reserved names and the resolved registry share one namespace. A path entry under
    /// <c>onboarding</c> would render a second, contradictory lookup: one saying <c>done</c>, one naming a file no
    /// portable skill reads.
    /// </remarks>
    [Fact]
    public void OnboardingIsAReservedNameInTheConfigRegPathMap()
    {
        string message = AssertInvalid("config-reg:\n  onboarding: docs/x.md\n").Message;

        Assert.Contains("reserved", message, StringComparison.Ordinal);
        Assert.Contains("onboarding", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The intake assessment publishes a mode property of its own, so a host may not declare that name
    /// as a path either. The loader's map and the resolved registry share one namespace: a path entry
    /// under the reserved name would render a second, contradictory lookup that resolves to a file no
    /// portable skill reads.
    /// </summary>
    [Fact]
    public void TheIntakeAssessmentIsAReservedNameInTheConfigRegPathMap()
    {
        string message = AssertInvalid("config-reg:\n  intake-assessment: docs/x.md\n").Message;

        Assert.Contains("reserved", message, StringComparison.Ordinal);
        Assert.Contains("intake-assessment", message, StringComparison.Ordinal);
        // The error names the key the operator should have written instead, which is the only thing
        // that makes a rejected declaration actionable.
        Assert.Contains("ontology.intake", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Turning the assessment off is a value, not an omission. A later configuration that writes
    /// <c>intake: false</c> clears an earlier <c>true</c> — the null-only fallback
    /// <c>OntologyConfig.Clone</c> makes for every other key — so the merged repository publishes no mode
    /// property rather than inheriting the one the first layer turned on.
    /// </summary>
    [Fact]
    public void ALaterIntakeFalseClearsAnEarlierTrueRatherThanFallingBack()
    {
        OntologyConfig enabled = OntologyConfigLoader.Merge(
            OntologyConfig.ProductDefaults,
            new OntologyYamlSection { Intake = true });
        OntologyConfig disabled = OntologyConfigLoader.Merge(enabled, new OntologyYamlSection { Intake = false });

        Assert.True(enabled.Intake, "'intake: true' must turn the assessment on.");
        Assert.False(
            disabled.Intake,
            "'intake: false' is a value the later layer wrote, so the merge must keep it rather than " +
            "falling back to the true it inherited.");
        Assert.Contains(
            "intake-assessment",
            ConfigRegConfig.ProductDefaults.Resolve(enabled).Select(entry => entry.Name));
        Assert.DoesNotContain(
            "intake-assessment",
            ConfigRegConfig.ProductDefaults.Resolve(disabled).Select(entry => entry.Name));
    }

    [Theory]
    [InlineData("Auth Design")]
    [InlineData("auth_design")]
    [InlineData("-auth")]
    public void RejectsAPropertyNameASkillCouldNotType(string name)
    {
        Assert.Contains("lookup name", AssertInvalid($"config-reg:\n  {name}: docs/x.md\n").Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../elsewhere/x.md")]
    [InlineData("/etc/passwd")]
    public void RejectsAPathThatLeavesTheRepository(string path)
    {
        Assert.ThrowsAny<YamlException>(() => KyberWeaveConfigAssembler.FromYaml($"config-reg:\n  x: {path}\n"));
    }

    [Theory]
    [InlineData("GitHub Actions")]
    [InlineData("standards/dotnet")]
    [InlineData("dotnet--core")]
    public void RejectsATechnologyThatIsNotASlug(string technology)
    {
        Assert.Contains("slug", AssertInvalid($"ontology:\n  technologies:\n    - {technology}\n").Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsADuplicateTechnology()
    {
        Assert.Contains("more than once",
            AssertInvalid("ontology:\n  technologies:\n    - dotnet\n    - dotnet\n").Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void NoTechnologyIsDeclaredByDefault()
    {
        Assert.Empty(KyberWeaveConfigDefaults.Product.Ontology.Technologies);
    }
}
