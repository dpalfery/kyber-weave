using System.Text;

namespace KyberWeave.Core.Squad.Deployment;

/// <summary>Resolves portable paths while enforcing both lexical and physical containment.</summary>
public static class SquadPathPolicy
{
    public static string ResolveFile(string rootPath, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        string portablePath = NormalizeRelativePath(relativePath);
        string root = Path.GetFullPath(rootPath);
        string candidate = Path.GetFullPath(Path.Combine(
            root,
            portablePath.Replace('/', Path.DirectorySeparatorChar)));

        if (!SquadFileSystemPathSemantics.IsWithin(root, candidate) ||
            SquadFileSystemPathSemantics.AreSame(root, candidate))
            throw OutsideRoot(relativePath);

        return candidate;
    }

    public static string NormalizeRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || IsPortableRooted(relativePath))
            throw OutsideRoot(relativePath);

        if (relativePath.Contains('\\', StringComparison.Ordinal) ||
            !relativePath.IsNormalized(NormalizationForm.FormC) ||
            !HasValidUtf8Representation(relativePath))
        {
            throw NonPortable(relativePath);
        }

        string[] segments = relativePath.Split('/');
        if (segments.Any(segment => segment is "." or ".."))
            throw OutsideRoot(relativePath);

        if (segments.Length == 0 ||
            segments.Any(segment =>
                segment.Length == 0 ||
                !IsPortableSegment(segment)))
        {
            throw NonPortable(relativePath);
        }

        return string.Join('/', segments);
    }

    private static bool IsPortableRooted(string path) =>
        Path.IsPathRooted(path) ||
        path[0] is '/' or '\\' ||
        (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':');

    /// <summary>
    /// Returns the identity used by portable target filesystems, where case and trailing dots
    /// or spaces cannot safely distinguish two deployment paths.
    /// </summary>
    public static string GetPortableIdentity(string relativePath)
    {
        string normalized = NormalizeRelativePath(relativePath);
        string[] segments = normalized.Split('/');
        for (int index = 0; index < segments.Length; index++)
        {
            string portableSegment = segments[index].TrimEnd(' ', '.');
            if (portableSegment.Length == 0)
            {
                throw new SquadDeploymentConflictException(
                    $"Squad path '{relativePath}' is not a portable path because a segment " +
                    "contains only dots or spaces.");
            }

            segments[index] = portableSegment;
        }

        return string.Join('/', segments).Normalize(NormalizationForm.FormC);
    }

    private static bool IsPortableSegment(string segment)
    {
        if (segment[^1] is ' ' or '.' ||
            segment.Any(character =>
                char.IsControl(character) ||
                character is '<' or '>' or ':' or '"' or '|' or '?' or '*'))
        {
            return false;
        }

        int dot = segment.IndexOf('.', StringComparison.Ordinal);
        string baseName = dot >= 0 ? segment[..dot] : segment;
        return !IsReservedWindowsName(baseName);
    }

    private static bool IsReservedWindowsName(string baseName)
    {
        string normalized = baseName
            .Replace('\u00b9', '1')
            .Replace('\u00b2', '2')
            .Replace('\u00b3', '3')
            .ToUpperInvariant();
        if (normalized is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$")
            return true;

        return normalized.Length == 4 &&
            (normalized[..3] is "COM" or "LPT") &&
            normalized[3] is >= '1' and <= '9';
    }

    private static bool HasValidUtf8Representation(string value)
    {
        try
        {
            _ = new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true).GetByteCount(value);
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    private static SquadPathContainmentException OutsideRoot(string? relativePath) =>
        new(
            $"Squad deployment path '{relativePath}' escapes the target root. " +
            "Use a relative path whose resolved target remains inside the deployment root.");

    private static SquadDeploymentConflictException NonPortable(string? relativePath) =>
        new(
            $"Squad deployment path '{relativePath}' is not a canonical portable path. " +
            "Use NFC Unicode, forward slashes, and names valid on Windows filesystems.");
}
