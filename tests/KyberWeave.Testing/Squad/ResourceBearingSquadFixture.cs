using System.Text;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Corpus;
using KyberWeave.Testing.Squad;
using KyberWeave.Tests.Fakes;

namespace KyberWeave.Tests;

/// <summary>
/// Shared C3 fixture for recursive agent/skill resources. The fixture copies the canonical source into a disposable tree so
/// the tests never mutate product content.
/// </summary>
/// <remarks>
/// Pass <c>inMemory: true</c> and no disk is touched. The disk mode stays only until the renderer classes that still render
/// from a real folder move onto the in-memory tree; remove it then.
/// </remarks>
internal sealed class ResourceBearingSquadFixture : IDisposable
{
    private static readonly ICorpusSource Corpus = CheckoutCorpusSource.Checkout;

    internal const string AgentName = "bug-crusher-investigator";
    internal const string SkillName = "azure-cli";
    internal const string AgentLink = "[Agent runbook](bug-crusher-investigator/references/runbook.md)";
    internal const string AgentTextOnlyLink = "[Payload](bug-crusher-investigator/assets/agent-payload.txt)";
    internal const string SkillLink = "[Skill guide](references/guide.md)";
    internal const string AgentRunbook = "[Payload](../assets/agent-payload.txt)\nAgent runbook.\n";
    internal const string AgentPayload = "agent payload\n";
    internal const string SkillGuide = "[Snippet](../assets/skill-snippet.txt)\nSkill guide.\n";
    internal const string SkillSnippet = "skill snippet\n";

    private readonly TempDirectory? _temp;
    private readonly CanonicalSquadTree? _tree;

    private ResourceBearingSquadFixture(bool inMemory)
    {
        if (inMemory)
        {
            _tree = CanonicalSquadTree.Create();
            ProductRoot = _tree.ProductRoot;
        }
        else
        {
            _temp = new TempDirectory();
            ProductRoot = Path.Combine(_temp.Path, "kyber-squad");
            CopyCanonicalToDisk(ProductRoot);
        }
    }

    internal string ProductRoot { get; }

    /// <summary>Gets the in-memory file system holding the tree; fails for a fixture built on disk.</summary>
    internal CanonicalSquadTree Tree =>
        _tree ?? throw new InvalidOperationException("This fixture was built on disk; build it with inMemory: true.");

    internal static ResourceBearingSquadFixture Create(bool inMemory = true)
    {
        ResourceBearingSquadFixture fixture = new(inMemory);

        fixture.AppendAgentLink(AgentLink);
        fixture.Append(
            $"skills/{SkillName}/SKILL.md",
            "\n" + SkillLink + "\n");
        fixture.Write($"agents/{AgentName}/references/runbook.md", AgentRunbook);
        fixture.Write($"agents/{AgentName}/assets/agent-payload.txt", AgentPayload);
        fixture.Write($"skills/{SkillName}/references/guide.md", SkillGuide);
        fixture.Write($"skills/{SkillName}/assets/skill-snippet.txt", SkillSnippet);
        return fixture;
    }

    /// <summary>
    /// An agent whose closure holds only non-markdown resources: the discovery guard is a
    /// no-op for it, so its projection is byte-identical to the native layout.
    /// </summary>
    internal static ResourceBearingSquadFixture CreateWithTextOnlyAgentResources(bool inMemory = true)
    {
        ResourceBearingSquadFixture fixture = new(inMemory);

        fixture.AppendAgentLink(AgentTextOnlyLink);
        fixture.Write($"agents/{AgentName}/assets/agent-payload.txt", AgentPayload);
        return fixture;
    }

    /// <summary>
    /// An agent whose body links only to a <c>.markdown</c> resource, which itself links to a
    /// <c>.md</c> resource. The <c>.md</c> resource is renamed on disk (it ends in <c>.md</c>),
    /// but no authored link in the body points at it, so the guard's degradation must not claim
    /// a link was rewritten.
    /// </summary>
    internal static ResourceBearingSquadFixture CreateWithUnlinkedMarkdownResource(bool inMemory = true)
    {
        ResourceBearingSquadFixture fixture = new(inMemory);

        fixture.AppendAgentLink("[Guide](bug-crusher-investigator/references/guide.markdown)");
        fixture.WriteAgentResource("references/guide.markdown", "[Notes](notes.md)\n");
        fixture.WriteAgentResource("references/notes.md", "notes\n");
        return fixture;
    }

    internal void WriteAgentResource(string relativePath, string content) =>
        Write($"agents/{AgentName}/{relativePath}", content);

    internal void AppendAgentLink(string link) =>
        Append($"agents/{AgentName}.md", "\n" + link + "\n");

    /// <summary>Creates a registry over the renderers that reads its render inputs from where this fixture's tree lives.</summary>
    /// <param name="renderers">The renderers the registry holds.</param>
    internal SquadRendererRegistry Registry(IEnumerable<ISquadRenderer> renderers) =>
        _tree is null ? SquadTestRegistry.Create(renderers) : _tree.Registry(renderers);

    /// <summary>Reads the request's render inputs from where this fixture's tree lives.</summary>
    /// <param name="request">The request to prepare.</param>
    internal SquadRenderRequest Prepare(SquadRenderRequest request) =>
        _tree is null ? request.Prepared() : _tree.Prepare(request);

    /// <summary>Loads the Squad source this fixture holds.</summary>
    internal SquadSource Load() =>
        _tree is null ? RealSquadServices.Load(ProductRoot) : _tree.Load();

    public void Dispose() => _temp?.Dispose();

    private static void CopyCanonicalToDisk(string destinationDirectory)
    {
        string sourceDirectory = Path.Combine(Corpus.Root, "products", "kyber-squad");
        Directory.CreateDirectory(destinationDirectory);
        foreach (string sourcePath in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(sourceDirectory, sourcePath);
            string destinationPath = Path.Combine(destinationDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourcePath, destinationPath);
        }
    }

    private void Append(string relativePath, string content)
    {
        string path = PathOf(relativePath);
        if (_tree is null)
        {
            File.AppendAllText(path, content, new UTF8Encoding(false));
        }
        else
        {
            _tree.Files.WriteAllText(path, _tree.Files.ReadAllText(path) + content);
        }
    }

    private void Write(string relativePath, string content)
    {
        string path = PathOf(relativePath);
        if (_tree is null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content, new UTF8Encoding(false));
        }
        else
        {
            _tree.Files.CreateDirectory(Path.GetDirectoryName(path)!);
            _tree.Files.WriteAllText(path, content);
        }
    }

    private string PathOf(string relativePath) =>
        Path.Combine(ProductRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
}
