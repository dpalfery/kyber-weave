using System.Reflection;
using System.Security.Cryptography;

namespace KyberWeave.Core.Squad.Deployment;

/// <summary>
///     Deploys the default user-home response guidance to
///     <c>&lt;home&gt;/.kyber-weave/response-guidance.md</c> and refreshes it the way the canonical
///     model-profile snapshot is staged: a file whose bytes still match the digest the receipt
///     recorded is Squad's own copy of the shipped default and is replaced, while anything else is the
///     operator's own wording and is preserved.
/// </summary>
/// <remarks>
///     <para>
///         The file belongs to no harness. Every agent reads the one path through the response-format
///         skill, which is why it is owned as a <see cref="SquadHomeFile" /> rather than as a
///         <see cref="SquadOwnedFile" /> deployed beneath a target's global root.
///     </para>
///     <para>
///         <see cref="Deploy" /> returns a record only for bytes it has just read back from the deployed
///         path, and its caller writes the guidance before the receipt that carries that record. Both
///         halves are load-bearing: a digest recorded ahead of its write describes a file that was never
///         written, and the next run then reads the operator's untouched file as an edit — preserved
///         forever and reported as one they made. Ownership follows the write; it never leads it.
///     </para>
///     <para>
///         A file whose bytes already equal the shipped default is adopted without a write, whatever the
///         receipt says. That is how a run repairs itself when a deployment transaction failed after an
///         earlier write: the bytes are the ones Squad would have written, so recording them loses no
///         operator content. The reverse inference is refused deliberately — treating "the receipt already
///         records this release's default, but the file differs" as a pending retry would overwrite a
///         genuine edit made against that same default, which is the one customization this file exists
///         to hold.
///     </para>
///     <para>
///         A file that exists with no ownership record and different bytes is never adopted to: it was
///         written by the operator or by a release older than the receipt that records ownership, and
///         overwriting it would discard that customization. The run reports it instead, so an operator
///         who does want the shipped default knows to delete the file rather than guess why updates
///         stopped changing it.
///     </para>
///     <para>
///         The default text is a resource in this assembly rather than the canonical
///         <c>products/kyber-squad/skills/response-format/user-guidance.md</c>, because a deployed tool
///         has no product tree to read: the skill file travels with the release, while the binary a host
///         already has installed is what performs the deploy. The two files are kept byte-identical by
///         the <c>VerifyResponseGuidanceDefault</c> build target, so they cannot drift into two different
///         defaults.
///     </para>
/// </remarks>
internal sealed class SquadResponseGuidanceDeployer
{
    /// <summary>The file name Squad owns beneath the home state root.</summary>
    public const string FileName = "response-guidance.md";

    /// <summary>The manifest resource the shipped default is read from.</summary>
    private const string DefaultResourceName = "Squad.response-guidance.md";

    private static readonly Lazy<byte[]> DefaultBytes = new(ReadDefaultContent);

    private readonly string _homeStateRoot;

    /// <param name="homeStateRoot">
    ///     The home state root, <c>&lt;home&gt;/.kyber-weave</c>. Fully qualified, because every path
    ///     this type writes is one an operator later finds by hand.
    /// </param>
    public SquadResponseGuidanceDeployer(string homeStateRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeStateRoot);
        if (!Path.IsPathFullyQualified(homeStateRoot))
            throw new ArgumentException(
                $"The Squad home state root '{homeStateRoot}' must be a fully qualified path.",
                nameof(homeStateRoot));

        _homeStateRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(homeStateRoot));
    }

    /// <summary>Deploys or refreshes the guidance, returning the ownership record to persist.</summary>
    /// <param name="previous">The home files the receipt currently records.</param>
    /// <param name="warnings">
    ///     Collects what this run could not finish. A guidance file is not part of the deployment
    ///     transaction, so a refused write degrades the run's report instead of failing it — the same
    ///     trade <c>SquadLifecycleService.WriteCanonicalSnapshot</c> makes.
    /// </param>
    /// <returns>
    ///     The record to persist — non-null only when the shipped default's digest was read back from
    ///     the deployed path during this call — or <see langword="null" /> when the receipt must keep
    ///     the records it already has.
    /// </returns>
    public SquadHomeFile? Deploy(IReadOnlyList<SquadHomeFile>? previous, ICollection<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(warnings);

        string path = SquadPathPolicy.ResolveFile(_homeStateRoot, FileName);
        byte[] shipped = DefaultBytes.Value;
        string shippedDigest = Digest(shipped);
        SquadHomeFile record = new(FileName, shippedDigest);

        try
        {
            if (!File.Exists(path))
                return WriteAndVerify(path, shipped, shippedDigest, record, warnings);

            string currentDigest = Digest(File.ReadAllBytes(path));

            // Already the bytes this release ships: nothing to write, and recording them cannot lose
            // anything an operator wrote, whatever the receipt currently claims.
            if (string.Equals(currentDigest, shippedDigest, StringComparison.Ordinal))
                return record;

            string recordedDigest = FindRecordedDigest(previous);
            if (recordedDigest.Length == 0)
            {
                warnings.Add(
                    $"Kept the response guidance at '{path}' because no deployment record says Squad " +
                    "wrote it. Delete the file to take the shipped default.");
                return null;
            }

            // Unmodified since Squad wrote it, so it is this run's copy to replace.
            if (string.Equals(currentDigest, recordedDigest, StringComparison.Ordinal))
                return WriteAndVerify(path, shipped, shippedDigest, record, warnings);

            warnings.Add(
                $"Kept the edited response guidance at '{path}'. Squad replaces that file only " +
                "while it still matches the default Squad shipped; delete it to take the shipped " +
                "default back.");
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            warnings.Add(UnavailableMessage(path, exception));
            return null;
        }
    }

    /// <summary>
    ///     Writes the shipped default and only reports success once the bytes at the destination read
    ///     back as those bytes. The read back is what lets the caller record a digest as a fact rather
    ///     than an intention.
    /// </summary>
    private static SquadHomeFile? WriteAndVerify(
        string path,
        byte[] shipped,
        string shippedDigest,
        SquadHomeFile record,
        ICollection<string> warnings)
    {
        WriteShippedDefault(path, shipped);
        if (string.Equals(Digest(File.ReadAllBytes(path)), shippedDigest, StringComparison.Ordinal))
            return record;

        warnings.Add(
            $"The response guidance at '{path}' did not read back as the default this run wrote, so no " +
            "ownership is recorded for it and whatever is there is left as it stands. Rerun " +
            "'squad update --global' to retry.");
        return null;
    }

    /// <summary>The digest the receipt records for the guidance, or an empty string when it records none.</summary>
    private static string FindRecordedDigest(IReadOnlyList<SquadHomeFile>? previous)
    {
        return previous?
                   .FirstOrDefault(file => string.Equals(file.RelativePath, FileName, StringComparison.Ordinal))
                   ?.Sha256 ?? string.Empty;
    }

    /// <summary>
    ///     Writes the shipped default beside its destination and moves it into place, so an
    ///     interrupted write leaves the previous file whole rather than truncated. A truncated
    ///     guidance file would read back as an operator edit and be preserved forever.
    /// </summary>
    private static void WriteShippedDefault(string path, byte[] content)
    {
        string? directory = Path.GetDirectoryName(path);
        if (directory is not null)
            Directory.CreateDirectory(directory);

        string staged = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(staged, content);
            File.Move(staged, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(staged))
                File.Delete(staged);
        }
    }

    private static string UnavailableMessage(string path, Exception exception)
    {
        return $"The response guidance at '{path}' was not refreshed ({exception.Message}). The deployment " +
               "itself is unaffected; rerun 'squad update --global' to retry.";
    }

    private static byte[] ReadDefaultContent()
    {
        Assembly assembly = typeof(SquadResponseGuidanceDeployer).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(DefaultResourceName)
                              ?? throw new InvalidOperationException(
                                  $"KyberWeave.Core does not carry its default response guidance " +
                                  $"('{DefaultResourceName}'), so a global update has nothing to deploy. " +
                                  "Rebuild the assembly from the canonical products/kyber-squad tree.");
        using MemoryStream buffer = new();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static string Digest(ReadOnlySpan<byte> content)
    {
        return Convert.ToHexStringLower(SHA256.HashData(content));
    }
}
