using System.Text.Json.Serialization;

namespace KyberWeave.Core.Arbiter.Facts;

/// <summary>
/// The structured model block a decision record carries when a model target was resolved for the
/// dispatch: which profile and token the target was pointed at, where that choice came from, and
/// whether the harness actually delivered it. Null fields are omitted from the record.
/// </summary>
/// <param name="Profile">The profile the dispatch was targeted at.</param>
/// <param name="Token">The model token the targeted profile resolves to, when the table names one.</param>
/// <param name="RenderedToken">The token the target agent was rendered with, when known.</param>
/// <param name="Source">Where the choice came from: <c>ladder</c>, <c>difficulty</c>, <c>caller</c>, <c>rendered</c> or <c>model-targets</c>.</param>
/// <param name="Delivered">Whether the harness received the targeted token on the dispatch.</param>
/// <param name="Reason">The reason code when the targeting kept or declined the retarget, when there is one.</param>
public sealed record ArbiterModelBlock(
    [property: JsonPropertyName("profile")] string Profile,
    [property: JsonPropertyName("token")] string? Token,
    [property: JsonPropertyName("rendered-token")] string? RenderedToken,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("delivered")] bool Delivered,
    [property: JsonPropertyName("reason")] string? Reason);
