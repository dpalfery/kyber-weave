namespace KyberWeave.Core.Arbiter.Stores;

/// <summary>Size and retention limits for the run stores, from <c>arbiter.stores</c>.</summary>
/// <param name="MaxItemBytes">The largest single item, in UTF-8 bytes. An item exactly at the limit is kept.</param>
/// <param name="RetainDays">How many days a store directory is kept after its last write.</param>
public sealed record ArbiterStoreOptions(int MaxItemBytes = 16384, int RetainDays = 14);

/// <summary>What an append did with its item.</summary>
public enum ArbiterStoreStatus
{
    /// <summary>The item was appended.</summary>
    Stored,

    /// <summary>The item was over the byte limit. It is refused, never shortened to fit.</summary>
    TooLarge,

    /// <summary>The store could not be written. Reported, never thrown.</summary>
    Unavailable,
}
