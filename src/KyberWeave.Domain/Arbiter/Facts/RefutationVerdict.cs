namespace KyberWeave.Core.Arbiter.Facts;

/// <summary>What a refuter's return says about the finding it was asked to refute.</summary>
public enum RefutationVerdict
{
    /// <summary>The refuter could not defeat the finding (<c>SUPPORTED:</c>).</summary>
    Confirmed,

    /// <summary>The refuter defeated the finding (<c>REFUTED:</c>).</summary>
    Refuted,

    /// <summary>The return carries no recognised verdict. The finding is kept; nothing is inferred from it.</summary>
    Unknown,
}
