namespace KyberDash.Data.Collector.Sources;

/// <summary>
/// Harness-id alias over another <see cref="IHarnessMapper"/>: resolves in the engines' lookup
/// under a family member's harness id while delegating the mapping to the family mapper.
/// </summary>
/// <remarks>
/// One mapper class covers a harness family — for example <c>ClineMapper</c> maps both
/// <c>cline</c> and <c>cline-cli</c> units — but the engines' lookup keys on
/// <see cref="IHarnessMapper.HarnessId"/> alone. An alias registration under the family member's
/// id routes those units to the family mapper without duplicating its mapping rule.
/// </remarks>
public class HarnessMapperAlias(IHarnessMapper inner, string harnessId) : IHarnessMapper
{
    /// <summary>Gets the family member harness identifier this alias registers under.</summary>
    public string HarnessId => harnessId;

    /// <inheritdoc />
    public CollectedBatch Map(NativeSourceUnit unit, DateTimeOffset importedAtUtc) => inner.Map(unit, importedAtUtc);
}
