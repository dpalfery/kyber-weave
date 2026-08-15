using System.Text;

namespace KyberWeave.Core.Squad.Deployment;

/// <summary>
/// Centralizes deployment-path identity and containment using the spelling and aliases exposed by
/// the containing filesystem rather than an operating-system-wide case rule.
/// </summary>
internal static class SquadFileSystemPathSemantics
{
    public static bool AreSame(string first, string second)
    {
        var normalizedFirst = NormalizeLexical(first);
        var normalizedSecond = NormalizeLexical(second);
        return string.Equals(normalizedFirst, normalizedSecond, StringComparison.Ordinal);
    }

    public static bool IsWithin(string root, string candidate)
    {
        var normalizedRoot = NormalizeLexical(root);
        var normalizedCandidate = NormalizeLexical(candidate);
        if (HasPathPrefix(normalizedRoot, normalizedCandidate) &&
            !ContainsReparsePoint(normalizedCandidate))
        {
            return true;
        }

        var resolvedRoot = ResolveAliases(normalizedRoot);
        var resolvedCandidate = ResolveAliases(normalizedCandidate);
        if (HasPathPrefix(resolvedRoot, resolvedCandidate))
            return true;

        var canonicalRoot = Canonicalize(root);
        var canonicalCandidate = Canonicalize(candidate);
        return HasPathPrefix(canonicalRoot, canonicalCandidate);
    }

    public static string Canonicalize(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var filesystemRoot = Path.GetPathRoot(fullPath)
            ?? throw new SquadPathContainmentException(
                $"Squad path '{path}' does not have a filesystem root.");
        var current = filesystemRoot;
        var segments = fullPath[filesystemRoot.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        for (var index = 0; index < segments.Length; index++)
        {
            var segment = segments[index];
            var actualEntry = ResolveActualEntry(current, segment);
            if (actualEntry is null)
            {
                for (; index < segments.Length; index++)
                    current = Path.Combine(current, segments[index]);
                break;
            }

            current = actualEntry.FullName;
            if ((actualEntry.Attributes & FileAttributes.ReparsePoint) == 0)
                continue;

            var target = actualEntry.ResolveLinkTarget(returnFinalTarget: true);
            if (target is not null)
                current = Canonicalize(target.FullName);
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(current))
            .Normalize(NormalizationForm.FormC);
    }

    private static FileSystemInfo? ResolveActualEntry(string parentPath, string segment)
    {
        if (!Directory.Exists(parentPath))
            return null;

        var entries = new DirectoryInfo(parentPath).EnumerateFileSystemInfos().ToArray();
        var exact = entries.FirstOrDefault(entry =>
            string.Equals(entry.Name, segment, StringComparison.Ordinal));
        if (exact is not null)
            return exact;

        var lexicalCandidate = Path.Combine(parentPath, segment);
        if (!Directory.Exists(lexicalCandidate) && !File.Exists(lexicalCandidate))
            return null;

        var aliases = entries.Where(entry =>
                string.Equals(entry.Name, segment, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return aliases.Length == 1 ? aliases[0] : null;
    }

    private static bool HasPathPrefix(string root, string candidate)
    {
        var prefix = Path.EndsInDirectorySeparator(root)
            ? root
            : root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, StringComparison.Ordinal);
    }

    private static bool ContainsReparsePoint(string path)
    {
        var filesystemRoot = Path.GetPathRoot(path);
        if (filesystemRoot is null)
            return false;

        var current = filesystemRoot;
        var segments = path[filesystemRoot.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    return true;
            }
            catch (Exception exception) when (
                exception is FileNotFoundException or DirectoryNotFoundException)
            {
                return false;
            }
        }

        return false;
    }

    private static string ResolveAliases(string path)
    {
        var filesystemRoot = Path.GetPathRoot(path)
            ?? throw new SquadPathContainmentException(
                $"Squad path '{path}' does not have a filesystem root.");
        var current = filesystemRoot;
        var segments = path[filesystemRoot.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            FileSystemInfo entry = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : new FileInfo(current);
            if (!entry.Exists || (entry.Attributes & FileAttributes.ReparsePoint) == 0)
                continue;

            var target = entry.ResolveLinkTarget(returnFinalTarget: true);
            if (target is not null)
                current = ResolveAliases(target.FullName);
        }

        return NormalizeLexical(current);
    }

    private static string NormalizeLexical(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))
            .Normalize(NormalizationForm.FormC);
}
