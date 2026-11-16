using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;

namespace KyberWeave.Core.Squad.Briefs;

/// <summary>Builds briefs by reading the installed reference files through the file-system port.</summary>
/// <param name="files">The file system the reference files are read from.</param>
public sealed class SquadBriefService(IFileSystem files) : ISquadBriefService
{
    /// <inheritdoc />
    public BriefResult Assemble(BriefMap map, string agent, BriefFacts facts, string referencesDirectory)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentException.ThrowIfNullOrWhiteSpace(agent);
        ArgumentException.ThrowIfNullOrWhiteSpace(referencesDirectory);

        IReadOnlyList<BriefFileRule> rules = map.FilesOf(agent)
            ?? throw new BriefMapException(
                $"The brief map has no entry for agent '{agent}'. Agents it describes: {string.Join(", ", map.Agents)}.");

        List<BriefPart> parts = [];
        foreach (BriefFileRule rule in rules)
        {
            string path = ResolveFile(referencesDirectory, rule.File);
            string text = files.ReadAllText(path);
            parts.Add(BriefAssembler.BuildPart(rule, path, text, facts));
        }

        HashSet<string> mapped = rules.Select(rule => rule.File).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] unlisted = files.EnumerateFiles(referencesDirectory, "*", recursive: false)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(name => !mapped.Contains(name) && !mapped.Contains(Path.ChangeExtension(name, ".md")))
            .Order(StringComparer.Ordinal)
            .ToArray();

        return new BriefResult(agent, facts, parts, unlisted);
    }

    /// <inheritdoc />
    public string ResolveFile(string referencesDirectory, string file)
    {
        string authored = Path.Combine(referencesDirectory, file);
        if (files.FileExists(authored))
        {
            return authored;
        }

        string guarded = Path.Combine(referencesDirectory, BriefAssembler.GuardedName(file));
        if (files.FileExists(guarded))
        {
            return guarded;
        }

        throw new BriefMapException(
            $"Reference file '{file}' is named in the brief map but is not installed: neither '{authored}' nor '{guarded}' exists.");
    }
}
