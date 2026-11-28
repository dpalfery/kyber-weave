using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Parsing;
using KyberWeave.Core.Skills.Model;
using KyberWeave.Core.Skills.Parsing;
using Markdig.Syntax;

namespace KyberWeave.Application.Skills;

/// <summary>Reads SKILL.md files and the on-disk facts the pure parser needs.</summary>
public sealed class SkillFileReader(IFileSystem files, IPathResolver paths, IProcessEnvironment environment)
{
    private readonly FileReferenceExtractor _references = new(files, paths);

    /// <summary>Loads every skill under a path.</summary>
    /// <param name="rootPath">A skill directory, a tree of skills, or a SKILL.md file.</param>
    /// <returns>One result per SKILL.md found, including the ones that failed to parse.</returns>
    public IReadOnlyList<SkillLoadResult> Load(string rootPath)
    {
        List<SkillLoadResult> results = [];

        if (files.FileExists(rootPath) && Path.GetFileName(rootPath).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
        {
            results.Add(LoadOne(rootPath));
            return results;
        }

        if (!files.DirectoryExists(rootPath))
        {
            results.Add(new SkillLoadResult(rootPath, null, $"Path not found: {rootPath}"));
            return results;
        }

        List<string> skillFiles = files
            .EnumerateFiles(rootPath, "SKILL.md", recursive: true)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        if (skillFiles.Count == 0)
        {
            results.Add(new SkillLoadResult(rootPath, null, $"No SKILL.md files found under: {rootPath}"));
            return results;
        }

        foreach (string file in skillFiles)
            results.Add(LoadOne(file));

        return results;
    }

    /// <summary>Loads only the skills that parsed successfully.</summary>
    /// <param name="rootPath">A skill directory, a tree of skills, or a SKILL.md file.</param>
    /// <returns>The parsed skills.</returns>
    public SkillSet LoadSet(string rootPath)
    {
        List<Skill> skills = [];
        foreach (SkillLoadResult result in Load(rootPath))
        {
            if (result.Success && result.Skill is { } skill)
                skills.Add(skill);
        }

        return new SkillSet(skills);
    }

    /// <summary>Reads and parses one SKILL.md file.</summary>
    /// <param name="skillFilePath">The absolute path of the SKILL.md.</param>
    /// <returns>The parsed skill.</returns>
    /// <exception cref="SkillParseException">The file is missing or its front matter cannot be parsed.</exception>
    public Skill ReadFile(string skillFilePath)
    {
        string fullPath = paths.GetFullPath(skillFilePath, environment.CurrentDirectory);
        if (!files.FileExists(fullPath))
            throw new SkillParseException($"SKILL.md not found at '{skillFilePath}'.");

        string content = files.ReadAllText(fullPath);
        if (Path.GetDirectoryName(fullPath) is not { } directory)
            throw new InvalidOperationException($"Cannot determine the directory of '{fullPath}'.");
        return Read(content, fullPath, directory);
    }

    /// <summary>Parses SKILL.md text against the facts on disk.</summary>
    /// <param name="content">The SKILL.md text.</param>
    /// <param name="skillFilePath">The SKILL.md's path.</param>
    /// <param name="directoryPath">The skill's directory, which may not exist.</param>
    /// <returns>The parsed skill.</returns>
    public Skill Read(string content, string skillFilePath, string directoryPath)
    {
        MarkdownDocument document = SkillParser.ParseDocument(content);
        IReadOnlyList<ExtractedFileReference> extracted =
            _references.ExtractFromDocument(document, directoryPath, FileReferenceOptions.SkillDefault);

        List<SkillReferenceLink> links = extracted
            .Select(r => new SkillReferenceLink(r.Reference, r.Exists))
            .ToList();

        IReadOnlyList<SkillResource> resources = SkillParser.DiscoverResources(
            paths,
            environment.CurrentDirectory,
            directoryPath,
            skillFilePath,
            files.EnumerateFiles(directoryPath, "*", recursive: true));

        return SkillParser.Parse(content, skillFilePath, directoryPath, links, resources);
    }

    private SkillLoadResult LoadOne(string skillFilePath)
    {
        try
        {
            Skill skill = ReadFile(skillFilePath);
            return new SkillLoadResult(skillFilePath, skill, null);
        }
        catch (SkillParseException ex)
        {
            return new SkillLoadResult(skillFilePath, null, ex.Message);
        }
    }
}
