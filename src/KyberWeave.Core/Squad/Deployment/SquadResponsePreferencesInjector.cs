using System.Text;
using System.Text.RegularExpressions;
using KyberWeave.Core.Squad.Rendering;

namespace KyberWeave.Core.Squad.Deployment;

/// <summary>
/// Injects the effective response-preferences text into the rendered entry files for each
/// supported Squad target.
/// </summary>
public static partial class SquadResponsePreferencesInjector
{
    private const string ResponsePreferencesTag = "response-preferences";

    /// <summary>
    /// Replaces the inner content of the single response-preferences block in each entry file.
    /// </summary>
    public static SquadRenderResult Inject(SquadRenderResult renderResult, string effectiveText)
    {
        ArgumentNullException.ThrowIfNull(renderResult);

        string effective = string.IsNullOrWhiteSpace(effectiveText)
            ? SquadResponseGuidanceSource.DefaultText
            : effectiveText;

        List<SquadDeploymentFile> files = new(renderResult.Files.Count);
        foreach (SquadDeploymentFile file in renderResult.Files)
        {
            bool hasBlock = ContainsResponsePreferencesBlock(file.Content.Span);
            string? canonicalCarrier = GetCanonicalCarrierName(file.RelativePath);

            if (hasBlock)
            {
                if (canonicalCarrier is null || !IsCanonicalCarrier(canonicalCarrier))
                    throw new SquadRenderValidationException(
                        $"The file '{file.RelativePath}' carries a <response-preferences> block but its canonical identity is not 'conductor' or 'bug-crusher'.");

                string content = Encoding.UTF8.GetString(file.Content.ToArray());
                MatchCollection matches = ResponsePreferencesRegex().Matches(content);
                if (matches.Count != 1)
                    throw new SquadRenderValidationException(
                        $"The entry file '{file.RelativePath}' must contain exactly one <response-preferences> block.");

                string replacement = $"<{ResponsePreferencesTag}>{effective}</{ResponsePreferencesTag}>";
                string updated = ResponsePreferencesRegex().Replace(content, replacement, 1);
                files.Add(new SquadDeploymentFile(file.RelativePath, Encoding.UTF8.GetBytes(updated), file.Target));
                continue;
            }

            if (IsCarrierLikePath(file.RelativePath))
                throw new SquadRenderValidationException(
                    $"The entry file '{file.RelativePath}' must contain exactly one <response-preferences> block.");

            files.Add(file);
        }

        ValidateTargetCarrierSet(files);
        return renderResult with { Files = files };
    }

    private static void ValidateTargetCarrierSet(IReadOnlyList<SquadDeploymentFile> files)
    {
        foreach (string target in files.Select(file => file.Target).Distinct(StringComparer.Ordinal))
        {
            bool hasEntryCandidate = files.Any(file =>
                string.Equals(file.Target, target, StringComparison.Ordinal) &&
                (LooksLikeEntryPath(file.RelativePath) || IsCarrierLikePath(file.RelativePath)));

            if (!hasEntryCandidate)
                continue;

            bool hasCanonicalCarrier = files.Any(file =>
                string.Equals(file.Target, target, StringComparison.Ordinal) &&
                ContainsResponsePreferencesBlock(file.Content.Span) &&
                IsCanonicalCarrier(GetCanonicalCarrierName(file.RelativePath) ?? string.Empty));

            if (!hasCanonicalCarrier)
                throw new SquadRenderValidationException($"The target '{target}' must produce at least one response-preferences entry file.");
        }
    }

    private static bool IsCarrierLikePath(string relativePath)
    {
        string? canonicalCarrier = GetCanonicalCarrierName(relativePath);
        if (canonicalCarrier is not null)
            return true;

        string path = relativePath.Replace('\\', '/');
        string fileName = Path.GetFileName(path) ?? string.Empty;
        string stem = Path.GetFileNameWithoutExtension(fileName);
        string? parentName = Path.GetDirectoryName(path) is string directory ? Path.GetFileName(directory) : null;

        return IsCarrierCandidate(stem) || IsCarrierCandidate(parentName) ||
               (Path.GetDirectoryName(path) is not null &&
                string.Equals(Path.GetFileName(Path.GetDirectoryName(path)!), "skills", StringComparison.OrdinalIgnoreCase) &&
                IsCarrierCandidate(fileName));
    }

    private static bool LooksLikeEntryPath(string relativePath)
    {
        string path = relativePath.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(path))
            return false;

        if (path.StartsWith("./", StringComparison.Ordinal))
            path = path[2..];

        string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return segments.Any(segment =>
                   segment.Equals("agents", StringComparison.OrdinalIgnoreCase) ||
                   segment.Equals("skills", StringComparison.OrdinalIgnoreCase) ||
                   segment.Equals("droids", StringComparison.OrdinalIgnoreCase) ||
                   segment.Equals("commands", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsCarrierCandidate(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return false;

        string value = candidate.Trim();
        if (value.StartsWith("role-", StringComparison.OrdinalIgnoreCase))
            value = value[5..];
        else if (value.StartsWith("lowered-", StringComparison.OrdinalIgnoreCase))
            value = value[8..];
        else
            return false;

        return string.Equals(value, "conductor", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "bug-crusher", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsResponsePreferencesBlock(ReadOnlySpan<byte> content)
    {
        string text = Encoding.UTF8.GetString(content.ToArray());
        return ResponsePreferencesRegex().IsMatch(text);
    }

    private static bool IsCanonicalCarrier(string carrierName)
    {
        return string.Equals(carrierName, "conductor", StringComparison.Ordinal) ||
               string.Equals(carrierName, "bug-crusher", StringComparison.Ordinal);
    }

    private static string? GetCanonicalCarrierName(string relativePath)
    {
        string path = relativePath.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(path))
            return null;

        string fileName = Path.GetFileName(path) ?? string.Empty;
        string? parentName = Path.GetDirectoryName(path) is string directory ? Path.GetFileName(directory) : null;

        string candidate = Path.GetFileNameWithoutExtension(fileName);
        if (candidate.EndsWith(".agent", StringComparison.OrdinalIgnoreCase))
            candidate = candidate[..^".agent".Length];

        string? direct = NormalizeCarrierName(candidate);
        if (direct is not null)
            return direct;

        if (parentName is not null)
        {
            direct = NormalizeCarrierName(parentName);
            if (direct is not null)
                return direct;
        }

        if (fileName.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("AGENT.md", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("agent.md", StringComparison.OrdinalIgnoreCase))
        {
            return parentName is not null ? NormalizeCarrierName(parentName) : null;
        }

        return null;
    }

    private static string? NormalizeCarrierName(string candidate)
    {
        string value = candidate.Trim();
        if (string.IsNullOrEmpty(value))
            return null;

        if (value.StartsWith("role-", StringComparison.OrdinalIgnoreCase))
            value = value[5..];
        else if (value.StartsWith("lowered-", StringComparison.OrdinalIgnoreCase))
            value = value[8..];

        value = value.Trim();
        if (string.Equals(value, "conductor", StringComparison.OrdinalIgnoreCase))
            return "conductor";

        if (string.Equals(value, "bug-crusher", StringComparison.OrdinalIgnoreCase))
            return "bug-crusher";

        return null;
    }

    [GeneratedRegex("<response-preferences>.*?</response-preferences>", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex ResponsePreferencesRegex();
}
