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

    private readonly string _homeStateRoot;
    private readonly Func<byte[]> _readDefault;

    /// <param name="homeStateRoot">
    ///     The home state root, <c>&lt;home&gt;/.kyber-weave</c>. Fully qualified, because every path
    ///     this type writes is one an operator later finds by hand.
    /// </param>
    public SquadResponseGuidanceDeployer(string homeStateRoot)
        : this(homeStateRoot, null)
    {
    }

    /// <param name="homeStateRoot">The home state root, as the public constructor documents it.</param>
    /// <param name="readDefault">
    ///     Reads the shipped default text, or null to read it from this assembly's embedded resource.
    ///     Injected so a test can drive the one failure mode a real deployment cannot provoke — a build
    ///     that shipped without its default — without putting a resource-loading seam in the path that
    ///     ships.
    /// </param>
    internal SquadResponseGuidanceDeployer(string homeStateRoot, Func<byte[]>? readDefault)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeStateRoot);
        if (!Path.IsPathFullyQualified(homeStateRoot))
            throw new ArgumentException(
                $"The Squad home state root '{homeStateRoot}' must be a fully qualified path.",
                nameof(homeStateRoot));

        _homeStateRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(homeStateRoot));
        _readDefault = readDefault ?? ReadDefaultContent;
    }

    /// <summary>
    ///     Whether a failure leaves the guidance undeployed but the rest of the run intact, rather than
    ///     meaning this code asked for something it should not have.
    /// </summary>
    /// <remarks>
    ///     The degradable set is what a filesystem or sandbox can do to a run on an otherwise healthy
    ///     host: <see cref="IOException" /> and its subclasses, <see cref="UnauthorizedAccessException" />,
    ///     <see cref="NotSupportedException" /> for a path form the platform cannot represent, and
    ///     <see cref="System.Security.SecurityException" /> for a host policy that denies the write.
    ///     <para>
    ///         <see cref="ArgumentException" /> and <see cref="InvalidOperationException" /> are
    ///         deliberately left out: both mean the caller or this code broke its own contract, and
    ///         turning that into a warning an operator cannot act on is how a defect survives.
    ///     </para>
    /// </remarks>
    internal static bool IsDegradableFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is IOException or UnauthorizedAccessException or NotSupportedException
            or System.Security.SecurityException;
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

        // Resolving the path probes the attributes of every segment on the way to it, so a host that
        // denies that read fails here rather than in the file work below. A path that leaves the home
        // root raises SquadPathContainmentException, an InvalidOperationException outside the degradable
        // set: that is a breach of the root's boundary, not something the host inflicted, and it must
        // keep failing the run.
        string path;
        try
        {
            path = SquadPathPolicy.ResolveFile(_homeStateRoot, FileName);
        }
        catch (Exception exception) when (IsDegradableFailure(exception))
        {
            warnings.Add(UnavailableMessage(Path.Combine(_homeStateRoot, FileName), exception));
            return null;
        }

        // Read under the same degrade policy as the write. A default that cannot be read is a defect
        // in this build, not in the operator's environment, but it still must not cost them their
        // deployment: the rest of the run is unaffected and the warning says what to do about it.
        byte[] shipped;
        try
        {
            shipped = _readDefault();
        }
        catch (Exception exception) when (IsDegradableFailure(exception))
        {
            warnings.Add(ShippedDefaultUnavailableMessage(exception));
            return null;
        }

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
                    $"Kept the edited response guidance at '{path}' because no deployment record says Squad " +
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
        catch (Exception exception) when (IsDegradableFailure(exception))
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

    /// <summary>
    ///     The warning for a default this build cannot supply. It names reinstallation rather than a
    ///     retry because the defect travels inside the binary: another run of the same build fails the
    ///     same way, so telling an operator to rerun would send them round a loop that never closes.
    /// </summary>
    private static string ShippedDefaultUnavailableMessage(Exception exception)
    {
        return $"The default response guidance this build ships could not be read " +
               $"({exception.Message}), so no response guidance was written. The deployment itself is " +
               "unaffected. Reinstall Kyber-Weave from a release that ships the response guidance.";
    }

    private static byte[] ReadDefaultContent()
    {
        Assembly assembly = typeof(SquadResponseGuidanceDeployer).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(DefaultResourceName)
                              ?? throw new SquadShippedGuidanceUnavailableException(
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

/// <summary>
///     Raised when this assembly carries no readable default response guidance, so a global update has
///     nothing to deploy.
/// </summary>
/// <remarks>
///     An <see cref="IOException" /> subclass on purpose: an embedded stream that cannot be opened is
///     the same class of failure as a file that cannot be read, and it belongs on the degradable side of
///     <see cref="SquadResponseGuidanceDeployer.IsDegradableFailure" /> so a broken build costs a warning
///     rather than the whole deployment. The type is distinct so the warning can name a defect a rerun
///     cannot fix, which a plain <see cref="IOException" /> from the filesystem must not claim.
/// </remarks>
internal sealed class SquadShippedGuidanceUnavailableException : IOException
{
    public SquadShippedGuidanceUnavailableException()
    {
    }

    public SquadShippedGuidanceUnavailableException(string message)
        : base(message)
    {
    }

    public SquadShippedGuidanceUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
