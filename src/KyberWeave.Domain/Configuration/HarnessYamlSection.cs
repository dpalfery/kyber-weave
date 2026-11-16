namespace KyberWeave.Core.Configuration;

/// <summary>The <c>harness:</c> section of <c>kyber-weave.yml</c>.</summary>
internal sealed class HarnessYamlSection
{
    public Dictionary<string, HarnessProfileYamlSection>? Profiles { get; set; }
}
