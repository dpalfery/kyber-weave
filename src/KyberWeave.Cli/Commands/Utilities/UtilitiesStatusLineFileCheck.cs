using System.Security.Cryptography;

namespace KyberWeave.Cli.Commands.Utilities;

/// <summary>How a receipt-owned file compares to the digest the receipt recorded for it.</summary>
internal enum UtilitiesStatusLineFileState
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

/// <summary>Compares a receipt-owned file against its recorded SHA-256.</summary>
/// <remarks>
///     Shared by <c>status</c> and <c>doctor</c> so the two cannot disagree about what counts as drift.
///     An unreadable file answers <see cref="UtilitiesStatusLineFileState.Unreadable" /> rather than
///     <see cref="UtilitiesStatusLineFileState.Ok" />, so a removal that cannot prove it wrote a file
///     never deletes it and a status that cannot prove it is intact never reports it healthy.
/// </remarks>
internal static class UtilitiesStatusLineFileCheck
{
    /// <summary>Evaluates the file at <paramref name="physicalPath" /> against <paramref name="expectedSha256" />.</summary>
    public static UtilitiesStatusLineFileState Evaluate(string physicalPath, string expectedSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(physicalPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSha256);

        if (!File.Exists(physicalPath)) return UtilitiesStatusLineFileState.Missing;

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(physicalPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return UtilitiesStatusLineFileState.Unreadable;
        }

        string actualSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        return string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase)
            ? UtilitiesStatusLineFileState.Ok
            : UtilitiesStatusLineFileState.Drift;
    }
}
