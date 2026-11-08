using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Scaffolding;
using Xunit;
using YamlDotNet.Core;

namespace KyberWeave.Tests;

/// <summary>
///     The three run settings a run-start command needs: whether a failed tool stops the run, which
///     branch a run is compared against, and whether the harness name is published at all.
/// </summary>
/// <remarks>
///     <para>
///         ASSUMED CONTRACT (RED tests: the implementer provides these, or updates this comment and the tests
///         together). The typed surface these settings will appear on does not exist yet, so every assertion
///         below reads the one surface that exists today: the Config Reg block an agent actually reads.
///     </para>
///     <para>
///         <c>.kyber-weave/kyber-weave.yml</c> gains:
///     </para>
///     <list type="bullet">
///         <item>
///             <description>
///                 <c>stop-on-tool-failure</c> — a boolean at the top level. Absent means a failed tool stops the
///                 run, and the setting is published as a <see cref="ConfigRegEntryKind.Mode" /> property while set.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <c>run.base-branch</c> — a branch name. Absent means <c>origin/HEAD</c>, and it is published as a
///                 mode property only when the operator wrote it.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <c>run.harness-names</c> — a boolean, default true, published as a mode property only when set.
///             </description>
///         </item>
///     </list>
///     <para>
///         Property names are asserted by the setting name they must contain rather than by an exact
///         rendered name, so the implementer may spell the property <c>&lt;run-base-branch&gt;</c> or
///         <c>&lt;run.base-branch&gt;</c> without the pin moving. An unrecognised value is refused with a
///         <see cref="YamlException" /> the way an unrecognised <c>autonomy:</c> value already is, because a
///         setting nobody can act on is worse than no setting.
///     </para>
/// </remarks>
public sealed class RunSettingsConfigTests
{
    private const string StopOnToolFailureSetting = "stop-on-tool-failure";
    private const string BaseBranchSetting = "base-branch";
    private const string HarnessNamesSetting = "harness-names";

    /// <summary>
    ///     A configured <c>stop-on-tool-failure</c> reaches the registry an agent reads, as a setting
    ///     rather than a path: there is no file named <c>false</c> for the registry validator to find.
    /// </summary>
    [Fact]
    public void AConfiguredStopOnToolFailurePublishesAModeProperty()
    {
        KyberWeaveConfig config = KyberWeaveConfigLoader.LoadFromYaml("stop-on-tool-failure: false\n");

        ConfigRegEntry entry = Assert.Single(
            config.ConfigReg.Resolve(config.Ontology),
            candidate => candidate.Name.Contains(StopOnToolFailureSetting, StringComparison.Ordinal));
        Assert.Equal(ConfigRegEntryKind.Mode, entry.Kind);

        string line = RenderedLine(config, StopOnToolFailureSetting);
        Assert.Contains("`", line, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A configured <c>run.base-branch</c> is published, and a repository that never set it publishes
    ///     nothing: the block states what the operator chose, and an absent line is what an agent reads as
    ///     the documented <c>origin/HEAD</c> default.
    /// </summary>
    [Fact]
    public void AConfiguredRunBaseBranchIsPublishedAndAnUnsetOneIsSilent()
    {
        KyberWeaveConfig configured =
            KyberWeaveConfigLoader.LoadFromYaml("run:\n  base-branch: develop\n");
        KyberWeaveConfig unset = KyberWeaveConfigLoader.LoadFromYaml("run:\n  harness-names: false\n");

        ConfigRegEntry entry = Assert.Single(
            configured.ConfigReg.Resolve(configured.Ontology),
            candidate => candidate.Name.Contains(BaseBranchSetting, StringComparison.Ordinal));
        Assert.Equal(ConfigRegEntryKind.Mode, entry.Kind);

        string line = RenderedLine(configured, BaseBranchSetting);
        Assert.Contains("develop", line, StringComparison.Ordinal);

        Assert.DoesNotContain(
            unset.ConfigReg.Resolve(unset.Ontology),
            candidate => candidate.Name.Contains(BaseBranchSetting, StringComparison.Ordinal));
    }

    /// <summary>
    ///     A configured <c>run.harness-names</c> is published, and a repository that left the default alone
    ///     publishes nothing, so the block never restates a default an agent already applies.
    /// </summary>
    [Fact]
    public void AConfiguredHarnessNamesSettingIsPublishedAndAnUnsetOneIsSilent()
    {
        KyberWeaveConfig configured =
            KyberWeaveConfigLoader.LoadFromYaml("run:\n  harness-names: false\n");
        KyberWeaveConfig unset = KyberWeaveConfigLoader.LoadFromYaml("run:\n  base-branch: main\n");

        ConfigRegEntry entry = Assert.Single(
            configured.ConfigReg.Resolve(configured.Ontology),
            candidate => candidate.Name.Contains(HarnessNamesSetting, StringComparison.Ordinal));
        Assert.Equal(ConfigRegEntryKind.Mode, entry.Kind);

        Assert.DoesNotContain(
            unset.ConfigReg.Resolve(unset.Ontology),
            candidate => candidate.Name.Contains(HarnessNamesSetting, StringComparison.Ordinal));
    }

    /// <summary>
    ///     A value outside the two booleans is refused where the operator wrote it, with a message naming
    ///     the setting, rather than being read as false by a later parse.
    /// </summary>
    /// <param name="value">A value YAML accepts as a scalar but the setting does not accept.</param>
    [Theory]
    [InlineData("sometimes")]
    [InlineData("1.5")]
    [InlineData("stop")]
    public void AnUnrecognisedStopOnToolFailureValueIsRefused(string value)
    {
        string message = AssertInvalid($"stop-on-tool-failure: {value}\n").Message;

        Assert.Contains(StopOnToolFailureSetting, message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The same rule inside the <c>run:</c> block, which reaches the loader through a different shape.
    /// </summary>
    /// <param name="value">A value YAML accepts as a scalar but the setting does not accept.</param>
    [Theory]
    [InlineData("sometimes")]
    [InlineData("yes-please")]
    public void AnUnrecognisedHarnessNamesValueIsRefused(string value)
    {
        string message = AssertInvalid($"run:\n  harness-names: {value}\n").Message;

        Assert.Contains(HarnessNamesSetting, message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The rendered property line carrying <paramref name="nameFragment" />, so a failure names the
    ///     setting rather than dumping the whole generated block.
    /// </summary>
    private static string RenderedLine(KyberWeaveConfig config, string nameFragment)
    {
        string block = ConfigRegMarkdown.Render(config.ConfigReg.Resolve(config.Ontology));
        return Assert.Single(
            block.Split('\n'),
            line => line.StartsWith("- **<", StringComparison.Ordinal) &&
                    line.Contains(nameFragment, StringComparison.Ordinal));
    }

    private static YamlException AssertInvalid(string yaml)
    {
        return Assert.ThrowsAny<YamlException>(() => KyberWeaveConfigLoader.LoadFromYaml(yaml));
    }
}
