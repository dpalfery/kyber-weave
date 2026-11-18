using System.Text.Json.Serialization;

namespace KyberWeave.Core.Arbiter.ModelTargets;

/// <summary>
/// The model targets table the arbiter reads for one harness: <c>model-targets.&lt;harness&gt;.json</c>
/// in the arbiter directory. It names the profile each tier resolves to and the token each agent runs on.
/// </summary>
/// <remarks>
/// <c>IArbiterModelTargetsReader</c> reads it and fails closed: a file that is absent, unreadable, malformed,
/// carries another schema id, or names a different harness than the one requested yields no table.
/// </remarks>
/// <param name="Schema">The schema id the file declares; must equal <see cref="SchemaName"/>.</param>
/// <param name="Harness">The harness the file was written for.</param>
/// <param name="Preset">The preset the table was resolved from.</param>
/// <param name="BundleDigest">The digest of the bundle the table was written with.</param>
/// <param name="Profiles">Each profile name to its model token; a null token means the profile is unset.</param>
/// <param name="Agents">Each agent name to its profile and token.</param>
public sealed record ArbiterModelTargets(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("harness")] string Harness,
    [property: JsonPropertyName("preset")] ArbiterModelTargetsPreset Preset,
    [property: JsonPropertyName("bundle-digest")] string BundleDigest,
    [property: JsonPropertyName("profiles")] IReadOnlyDictionary<string, string?> Profiles,
    [property: JsonPropertyName("agents")] IReadOnlyDictionary<string, ArbiterModelTargetsAgent> Agents)
{
    /// <summary>The schema id a model targets file must declare.</summary>
    public const string SchemaName = "kyber-arbiter.model-targets/v1";
}

/// <summary>The preset a model targets table was resolved from.</summary>
/// <param name="Name">The preset name.</param>
/// <param name="Digest">The digest of the preset content.</param>
public sealed record ArbiterModelTargetsPreset(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("digest")] string Digest);

/// <summary>One agent's entry in a model targets table.</summary>
/// <param name="Profile">The profile the agent is assigned to.</param>
/// <param name="Token">The model token the agent runs on, or null when unset.</param>
public sealed record ArbiterModelTargetsAgent(
    [property: JsonPropertyName("profile")] string Profile,
    [property: JsonPropertyName("token")] string? Token);
