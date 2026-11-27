namespace KyberWeave.Domain.StatusLine;

/// <summary>How a receipt-owned file compares to the digest the receipt recorded for it.</summary>
public enum UtilitiesStatusLineFileState
{
    /// <summary>The file exists and still holds the bytes the deploy wrote.</summary>
    Ok,

    /// <summary>The file is gone.</summary>
    Missing,

    /// <summary>The file exists but its bytes differ from the recorded digest.</summary>
    Drift,

    /// <summary>The file exists but could not be read.</summary>
    Unreadable
}
