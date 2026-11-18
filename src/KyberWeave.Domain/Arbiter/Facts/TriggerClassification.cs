namespace KyberWeave.Core.Arbiter.Facts;

/// <summary>One dispatch classified to its trigger, or passing with no trigger.</summary>
/// <param name="Trigger">The classified trigger, or null when the dispatch passes.</param>
/// <param name="Caller">The resolved caller, or null when unidentified.</param>
/// <param name="CallerSource">How the caller was named (see <see cref="ArbiterCallerSources"/>).</param>
/// <param name="Target">The dispatch target.</param>
/// <param name="TargetClass">The target class from the embedded catalog.</param>
/// <param name="Headers">The valid dispatch headers.</param>
/// <param name="Marker">The planner marker, when the prompt carries one.</param>
/// <param name="PassedWithoutConfig">True: the result required no host configuration.</param>
public sealed record TriggerClassification(
    string? Trigger,
    string? Caller,
    string CallerSource,
    string? Target,
    string TargetClass,
    IReadOnlyDictionary<string, string> Headers,
    string? Marker,
    bool PassedWithoutConfig)
{
    /// <summary>
    /// The raw header values the prompt writes, including the ones <see cref="Headers"/> rejected
    /// (see <c>HeaderBlock.RawValues</c>). Read by the plan lookup and the model questions only.
    /// </summary>
    public IReadOnlyDictionary<string, string> RawHeaders { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}
