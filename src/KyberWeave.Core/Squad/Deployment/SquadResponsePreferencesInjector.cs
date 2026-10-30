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
            if (!IsEntryFile(file.RelativePath))
            {
                if (ContainsResponsePreferencesBlock(file.Content.Span))
                    throw new SquadRenderValidationException(
                        $"The non-entry file '{file.RelativePath}' must not contain a <response-preferences> block.");

                files.Add(file);
                continue;
            }

            string content = Encoding.UTF8.GetString(file.Content.ToArray());
            MatchCollection matches = ResponsePreferencesRegex().Matches(content);
            if (matches.Count != 1)
                throw new SquadRenderValidationException(
                    $"The entry file '{file.RelativePath}' must contain exactly one <response-preferences> block.");

            string replacement = $"<{ResponsePreferencesTag}>{effective}</{ResponsePreferencesTag}>";
            string updated = ResponsePreferencesRegex().Replace(content, replacement, 1);
            files.Add(new SquadDeploymentFile(file.RelativePath, Encoding.UTF8.GetBytes(updated), file.Target));
        }

        return renderResult with { Files = files };
    }

    private static bool ContainsResponsePreferencesBlock(ReadOnlySpan<byte> content)
    {
        string text = Encoding.UTF8.GetString(content.ToArray());
        return ResponsePreferencesRegex().IsMatch(text);
    }

    private static bool IsEntryFile(string relativePath)
    {
        string path = relativePath.Replace('\\', '/');
        if (path is "agents/conductor.md" or "agents/bug-crusher.md")
            return true;

        if (path.StartsWith(".zcode/commands/", StringComparison.Ordinal))
            return true;

        if (path is "skills/bug-crusher/SKILL.md")
            return true;

        if (path.StartsWith("skills/role-", StringComparison.Ordinal) ||
            path.StartsWith("skills/lowered-", StringComparison.Ordinal))
            return true;

        return false;
    }

    [GeneratedRegex("<response-preferences>.*?</response-preferences>", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex ResponsePreferencesRegex();
}
