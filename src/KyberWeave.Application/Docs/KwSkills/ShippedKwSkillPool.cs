using System.Text;
using KyberWeave.Contracts.Host;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace KyberWeave.Core.Docs.KwSkills;

/// <summary>
/// Reads the KW skills embedded in the product, by name, through the embedded-resource seam alone: what the
/// manifest lists and each listed file's bytes. It writes nothing, so a caller that only copies a skill
/// into a repository (<c>docs init</c>) needs no home folder or file system.
/// </summary>
internal sealed class ShippedKwSkillPool(IEmbeddedResources resources)
{
    /// <summary>The manifest's resource name.</summary>
    public const string ManifestResource = "KwSkills/skills.manifest.yml";

    private const string ResourcePrefix = "KwSkills/";
    private const string SkillFile = "SKILL.md";

    private IReadOnlyDictionary<string, IReadOnlyList<string>>? _manifest;

    /// <summary>Gets the shipped skill names, in the order the manifest lists them.</summary>
    public IReadOnlyList<string> Names => [.. Manifest().Keys];

    /// <summary>Gets whether a shipped skill has that name.</summary>
    public bool Contains(string name) => Manifest().ContainsKey(name);

    /// <summary>Gets the files the manifest lists for a skill, as slash-separated paths relative to its folder.</summary>
    /// <param name="name">A name the manifest lists.</param>
    public IReadOnlyList<string> FilesOf(string name) => Manifest()[name];

    /// <summary>Reads a shipped skill's <c>SKILL.md</c>.</summary>
    public string ReadSkillFile(string name)
    {
        using StreamReader reader = new(Open(name, SkillFile), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>Opens one file the manifest lists for a skill.</summary>
    /// <param name="name">A name the manifest lists.</param>
    /// <param name="relative">A path the manifest lists for that skill.</param>
    /// <exception cref="InvalidDataException">The manifest lists a file the build does not ship.</exception>
    public Stream Open(string name, string relative)
    {
        string logical = $"{ResourcePrefix}{name}/{relative}";
        try
        {
            return resources.OpenRead(logical);
        }
        catch (MissingEmbeddedResourceException)
        {
            // MSBuild's RecursiveDir ends in the platform separator, so on Windows the logical name
            // carries backslashes where the manifest has slashes.
            try
            {
                return resources.OpenRead($"{ResourcePrefix}{name}\\{relative.Replace('/', '\\')}");
            }
            catch (MissingEmbeddedResourceException)
            {
                throw Bad($"skill '{name}' lists '{relative}', which the build does not ship.");
            }
        }
    }

    private IReadOnlyDictionary<string, IReadOnlyList<string>> Manifest()
    {
        if (_manifest is not null)
        {
            return _manifest;
        }

        string text;
        try
        {
            text = resources.ReadAllText(ManifestResource);
        }
        catch (MissingEmbeddedResourceException)
        {
            return _manifest = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        }

        return _manifest = Parse(text);
    }

    private static Dictionary<string, IReadOnlyList<string>> Parse(string text)
    {
        Dictionary<string, IReadOnlyList<string>> skills = new(StringComparer.Ordinal);
        try
        {
            YamlStream stream = new();
            stream.Load(new StringReader(text));
            if (stream.Documents.Count == 0
                || stream.Documents[0].RootNode is not YamlMappingNode root
                || !root.Children.TryGetValue(new YamlScalarNode("skills"), out YamlNode? list)
                || list is not YamlSequenceNode entries)
            {
                throw Bad("it has no 'skills' list");
            }

            foreach (YamlNode entry in entries)
            {
                if (entry is not YamlMappingNode map
                    || !map.Children.TryGetValue(new YamlScalarNode("name"), out YamlNode? nameNode)
                    || nameNode is not YamlScalarNode { Value: { } name }
                    || !KwSkillNames.IsValid(name)
                    || !map.Children.TryGetValue(new YamlScalarNode("files"), out YamlNode? filesNode)
                    || filesNode is not YamlSequenceNode fileList
                    || !fileList.Children.All(f => f is YamlScalarNode))
                {
                    throw Bad("an entry is not { name, files } with a valid skill name");
                }

                List<string> relatives = [];
                foreach (YamlScalarNode file in fileList.Children.Cast<YamlScalarNode>())
                {
                    if (file.Value is not { Length: > 0 } relative || !IsPlainRelativePath(relative))
                    {
                        throw Bad($"skill '{name}' lists '{file.Value}', which is not a plain relative path.");
                    }

                    relatives.Add(relative);
                }

                if (relatives.Distinct(StringComparer.Ordinal).Count() != relatives.Count)
                {
                    throw Bad($"skill '{name}' lists a file twice.");
                }

                if (!relatives.Contains(SkillFile, StringComparer.Ordinal) || !skills.TryAdd(name, relatives))
                {
                    throw Bad($"skill '{name}' is listed twice or has no {SkillFile}");
                }
            }
        }
        catch (YamlException exception)
        {
            throw Bad(exception.Message);
        }

        return skills;
    }

    /// <summary>
    /// True when <paramref name="relative"/> can only name a file inside its skill's folder: no rooted,
    /// parent, empty or padded segment and no backslash. Every consumer joins the entry under a folder or
    /// asks the build for it as a resource name, so anything else could neither stay inside the folder nor
    /// match a shipped resource.
    /// </summary>
    private static bool IsPlainRelativePath(string relative) =>
        relative.Trim().Length == relative.Length
        && !relative.Contains('\\', StringComparison.Ordinal)
        && !Path.IsPathRooted(relative)
        && relative.Split('/').All(segment => segment is not ("" or "." or ".."));

    private static InvalidDataException Bad(string reason) =>
        new($"{ManifestResource} is not a valid skill manifest: {reason}.");
}
