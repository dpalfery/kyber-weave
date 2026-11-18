namespace KyberWeave.Core.Docs.Graph;

/// <summary>Resolves a relative Markdown link to a repository-relative path.</summary>
internal static class DocumentLinkPaths
{
    /// <summary>Resolves a relative link against the linking document's directory.</summary>
    internal static string? Resolve(string fromRelativePath, string link)
    {
        string directory = Path.GetDirectoryName(fromRelativePath)?.Replace('\\', '/') ?? string.Empty;
        string combined = string.IsNullOrEmpty(directory) ? link : $"{directory}/{link}";

        List<string> parts = new();
        foreach (string segment in combined.Split('/'))
        {
            if (segment is "." or "") continue;
            if (segment == "..")
            {
                if (parts.Count == 0) return null;
                parts.RemoveAt(parts.Count - 1);
                continue;
            }

            parts.Add(segment);
        }

        return parts.Count == 0 ? null : string.Join('/', parts);
    }
}
