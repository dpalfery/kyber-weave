using System.Diagnostics;
using KyberWeave.Core.Processes;

namespace KyberWeave.Core.Arbiter.Credentials;

/// <summary>Operating-system credential-store port for the Arbiter key.</summary>
/// <remarks>
/// One implementation per OS reads the entry for an endpoint origin. The key itself
/// never appears in configuration; it travels on stdin (macOS, Linux) or through the
/// native credential API (Windows), and never in argv.
/// </remarks>
public interface ICredentialStore
{
    /// <summary>Reads the stored key for <paramref name="origin"/>, or null when absent.</summary>
    string? Read(string origin);

    /// <summary>Stores <paramref name="key"/> for <paramref name="origin"/>.</summary>
    void Write(string origin, string key);

    /// <summary>
    /// Deletes the stored key for <paramref name="origin"/>. A missing entry is already
    /// deleted, so deleting an absent origin succeeds. The default implementation throws
    /// <see cref="NotSupportedException"/>; every shipped store overrides it.
    /// </summary>
    void Delete(string origin) =>
        throw new NotSupportedException("This credential store does not support key deletion.");
}
