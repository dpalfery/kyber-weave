namespace KyberWeave.Core.Configuration;

/// <summary>The <c>squad:</c> section of <c>kyber-weave.yml</c>.</summary>
internal sealed class SquadYamlSection
{
    public string? Bundle { get; set; }

    public string? Version { get; set; }

    public List<string>? Targets { get; set; }

    public List<string>? Exclusions { get; set; }

    public string? Translation { get; set; }
}
