using System.Security.Cryptography;
using System.Text;
using KyberWeave.Contracts.Host;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace KyberWeave.Core.Docs.KwSkills;

/// <summary>The KW skills embedded in the product: what the manifest lists, and a copy of one on disk.</summary>
/// <remarks>
/// A skill's reference files are read by an agent from a folder, so a shipped skill is written out once.
/// The folder name carries a hash of the skill's files, so a changed skill lands in a new folder and an
/// existing identical folder is reused as it stands. Everything is written under the operator's home
/// directory, inside <c>.kyber-weave/kw-skills</c>, and no path is built from anything but the manifest.
/// </remarks>
internal sealed class ShippedKwSkills(
    IEmbeddedResources resources,
    IWritableFileSystem files,
    IPathResolver paths,
    IProcessEnvironment environment)
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

    /// <summary>Reads a shipped skill's <c>SKILL.md</c>.</summary>
    public string ReadSkillFile(string name)
    {
        using StreamReader reader = new(Open(name, SkillFile), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Writes a shipped skill under the home folder unless an identical copy is already there, and
    /// returns the folder.
    /// </summary>
    /// <param name="name">A name the manifest lists.</param>
    /// <exception cref="IOException">The copy could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The home folder refuses the write.</exception>
    /// <exception cref="InvalidDataException">The manifest names a file that would land outside the folder.</exception>
    public string Materialize(string name)
    {
        IReadOnlyList<string> listed = Manifest()[name];
        List<(string Relative, byte[] Bytes)> contents = [];
        using SHA256 hash = SHA256.Create();
        foreach (string relative in listed)
        {
            using Stream stream = Open(name, relative);
            using MemoryStream copy = new();
            stream.CopyTo(copy);
            byte[] bytes = copy.ToArray();
            contents.Add((relative, bytes));
            byte[] label = Encoding.UTF8.GetBytes(relative);
            hash.TransformBlock(label, 0, label.Length, null, 0);
            hash.TransformBlock([0], 0, 1, null, 0);
            hash.TransformBlock(bytes, 0, bytes.Length, null, 0);
            hash.TransformBlock([0], 0, 1, null, 0);
        }

        hash.TransformFinalBlock([], 0, 0);
        string digest = Convert.ToHexStringLower(hash.Hash!)[..12];

        string home = environment.HomeDirectory;
        string root = paths.GetFullPath(Path.Combine(home, ".kyber-weave", "kw-skills"), home);
        string folder = paths.GetFullPath(Path.Combine(root, $"{name}-{digest}"), root);
        if (!paths.IsWithin(root, folder))
        {
            throw new InvalidDataException($"The skill folder for '{name}' is not inside {root}.");
        }

        List<(string Path, byte[] Bytes)> targets = [];
        foreach ((string relative, byte[] bytes) in contents)
        {
            string target = paths.GetFullPath(Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar)), folder);
            if (!paths.IsWithin(folder, target))
            {
                throw new InvalidDataException($"The manifest file '{relative}' of skill '{name}' is not inside its folder.");
            }

            targets.Add((target, bytes));
        }

        if (targets.TrueForAll(t => files.FileExists(t.Path) && files.GetLength(t.Path) == t.Bytes.Length))
        {
            return folder;
        }

        foreach ((string target, byte[] bytes) in targets)
        {
            files.CreateDirectory(Path.GetDirectoryName(target)!);
            files.WriteAllBytesAtomic(target, bytes);
        }

        return folder;
    }

    private Stream Open(string name, string relative)
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
            return resources.OpenRead($"{ResourcePrefix}{name}\\{relative.Replace('/', '\\')}");
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
                    || !fileList.Children.All(f => f is YamlScalarNode { Value: { Length: > 0 } }))
                {
                    throw Bad("an entry is not { name, files } with a valid skill name");
                }

                List<string> relatives = [.. fileList.Children.Cast<YamlScalarNode>().Select(f => f.Value!)];
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

    private static InvalidDataException Bad(string reason) =>
        new($"{ManifestResource} is not a valid skill manifest: {reason}.");
}
