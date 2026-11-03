using KyberWeave.Core.Squad.Model;

namespace KyberWeave.Cli.Commands.Squad.Infrastructure;

/// <summary>
/// Helper methods for parsing and manipulating YAML frontmatter in deployed agent and skill files.
/// </summary>
internal static class SquadFrontmatterHelper
{
    public static string ResolveIdentity(string relativePath)
    {
        string fileName = Path.GetFileName(relativePath);
        if (fileName.Equals("agent.md", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
        {
            string? parent = Path.GetDirectoryName(relativePath);
            return string.IsNullOrEmpty(parent) ? fileName : Path.GetFileName(parent);
        }

        if (fileName.EndsWith(".agent.md", StringComparison.Ordinal))
        {
            return fileName[..^".agent.md".Length];
        }

        return Path.GetFileNameWithoutExtension(fileName);
    }

    public static bool TryParseFrontmatter(
        string content,
        out string frontmatter,
        out string body,
        out string newline)
    {
        frontmatter = string.Empty;
        body = string.Empty;
        newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

        string trimmedContent = content.StartsWith('\uFEFF') ? content[1..] : content;

        if (!trimmedContent.StartsWith("---", StringComparison.Ordinal))
        {
            return false;
        }

        int firstNewline = trimmedContent.IndexOf('\n', StringComparison.Ordinal);
        if (firstNewline < 0)
        {
            return false;
        }

        string secondDelimiter = $"{newline}---";
        int secondDashes = trimmedContent.IndexOf(secondDelimiter, firstNewline, StringComparison.Ordinal);
        if (secondDashes < 0)
        {
            secondDelimiter = "\n---";
            secondDashes = trimmedContent.IndexOf(secondDelimiter, firstNewline, StringComparison.Ordinal);
            if (secondDashes < 0)
            {
                return false;
            }
        }

        frontmatter = trimmedContent[(firstNewline + 1)..secondDashes].Trim('\r', '\n');
        int relIdx = trimmedContent.AsSpan(secondDashes + secondDelimiter.Length).IndexOfAny('\r', '\n');
        int endOfSecondDashes = relIdx >= 0 ? secondDashes + secondDelimiter.Length + relIdx : -1;
        body = endOfSecondDashes >= 0 ? trimmedContent[endOfSecondDashes..] : string.Empty;
        return true;
    }

    public static string? ExtractField(string frontmatter, string fieldName)
    {
        string[] lines = frontmatter.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        string prefix = $"{fieldName}:";
        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                string val = trimmed[prefix.Length..].Trim().Trim('\'', '"');
                return string.IsNullOrWhiteSpace(val) ? null : val;
            }
        }

        return null;
    }

    public static string UpdateFrontmatterModel(string frontmatter, string? newModel, string newline)
    {
        string[] lines = frontmatter.Split(['\r', '\n'], StringSplitOptions.None);
        List<string> result = [];
        bool modelFound = false;

        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("model:", StringComparison.OrdinalIgnoreCase))
            {
                modelFound = true;
                if (!string.IsNullOrWhiteSpace(newModel))
                {
                    result.Add($"model: {newModel}");
                }
            }
            else
            {
                result.Add(line);
            }
        }

        if (!modelFound && !string.IsNullOrWhiteSpace(newModel))
        {
            int insertIndex = result.FindIndex(l => l.Trim().StartsWith("name:", StringComparison.OrdinalIgnoreCase));
            if (insertIndex >= 0)
            {
                result.Insert(insertIndex + 1, $"model: {newModel}");
            }
            else
            {
                result.Insert(0, $"model: {newModel}");
            }
        }

        return string.Join(newline, result);
    }

    public static string? ResolveModel(SquadModelProfiles? modelProfiles, string category, string targetToken)
    {
        if (modelProfiles?.Profiles is null)
        {
            return null;
        }

        string? resolved = null;
        if (modelProfiles.Profiles.TryGetValue(category, out SquadModelProfile? profile))
        {
            if (profile.HarnessModels.TryGetValue(targetToken, out string? hm))
            {
                resolved = hm;
            }
            else if (profile.Default is not null)
            {
                resolved = profile.Default;
            }
        }
        else if (modelProfiles.Profiles.TryGetValue("general", out SquadModelProfile? genProfile))
        {
            if (genProfile.HarnessModels.TryGetValue(targetToken, out string? hm))
            {
                resolved = hm;
            }
            else if (genProfile.Default is not null)
            {
                resolved = genProfile.Default;
            }
        }

        if (string.Equals(resolved, "inherit", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return resolved;
    }
}
