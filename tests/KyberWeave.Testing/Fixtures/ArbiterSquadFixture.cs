using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.Arbiter;
using KyberWeave.Tests.FileSystem;
using KyberWeave.Testing.Squad;

namespace KyberWeave.Tests.Fixtures;

/// <summary>
/// Builds a minimal Squad corpus whose agent roster covers every class the Arbiter hook
/// wiring must distinguish: four dispatchers with non-empty <c>delegates-to</c>, three
/// implementation specialists in the <c>worker</c> and <c>publishing-worker</c> capability
/// profiles, and two agents (<c>docs-dev</c>, <c>research-agent</c>) that are neither.
/// <c>docs-dev</c> deliberately carries its own <c>documentation</c> profile so the corpus
/// pins Req 25.3 structurally: the guarded set is defined by the two specialist profiles
/// alone, with no name-based carve-out that could silently rot.
/// </summary>
internal sealed class ArbiterSquadFixture : IDisposable
{
    public const string Conductor = "conductor";
    public const string Architect = "architect";
    public const string ProductOwner = "product-owner";
    public const string CodeReviewer = "code-reviewer";
    public const string CsharpDev = "dev-easy";
    public const string TestDev = "dev-medium";
    public const string GithubDevops = "devops";
    public const string DocsDev = "docs-dev";
    public const string ResearchAgent = "research-agent";

    private readonly TempDirectory? _tempDirectory;
    private readonly InMemoryFileSystem? _files;
    private readonly string _path;

    private ArbiterSquadFixture(InMemoryFileSystem? files)
    {
        _files = files;
        if (files is null)
        {
            _tempDirectory = new TempDirectory();
            _path = _tempDirectory.Path;
        }
        else
        {
            _path = ArbiterTestRoots.Under("squad");
            files.CreateDirectory(_path);
        }
    }

    public string Path => _path;

    /// <summary>Builds the corpus on the real disk, for the tests whose subject is what a render reads from a tree.</summary>
    public static ArbiterSquadFixture Create() => Build(null);

    /// <summary>Builds the corpus in <paramref name="files"/>, so a render needs no disk.</summary>
    /// <param name="files">The in-memory file system the corpus is written to.</param>
    public static ArbiterSquadFixture CreateInMemory(InMemoryFileSystem files) =>
        Build(files ?? throw new ArgumentNullException(nameof(files)));

    /// <summary>Builds a renderer registry that reads its render inputs from this fixture's file system.</summary>
    /// <param name="renderers">The renderers the registry holds.</param>
    public SquadRendererRegistry Registry(IEnumerable<ISquadRenderer> renderers) =>
        _files is null
            ? SquadTestRegistry.Create(renderers)
            : new SquadRendererRegistry(renderers, new InMemorySquadServices(_files).RenderInputReader());

    /// <summary>Reads the request's inputs the way the registry does before it renders, from this fixture's file system.</summary>
    /// <param name="request">The request to prepare.</param>
    public SquadRenderRequest Prepare(SquadRenderRequest request) =>
        _files is null ? request.Prepared() : new InMemorySquadServices(_files).RenderInputReader().Prepare(request);

    /// <summary>Loads the corpus this fixture holds.</summary>
    public KyberWeave.Core.Squad.Model.SquadSource Load() =>
        _files is null ? RealSquadServices.Load(Path) : new InMemorySquadServices(_files).Load(Path);

    private static ArbiterSquadFixture Build(InMemoryFileSystem? files)
    {
        ArbiterSquadFixture fixture = new(files);
        fixture.Write("squad.yml", """
            schema: kyber-squad.squad/v1
            name: arbiter-fixture
            version-source: kyber-weave-assembly
            default-bundle: full
            bundles:
              full: bundles/full.yml
            profiles:
              models: profiles/models.yml
              capabilities: profiles/capabilities.yml
              fallbacks: profiles/fallbacks.yml
            toolchain: toolchain.yml
            mcp: mcp.json
            """);
        fixture.Write("bundles/full.yml", """
            schema: kyber-squad.bundle/v1
            name: full
            agents:
              - conductor
              - architect
              - product-owner
              - code-reviewer
              - dev-easy
              - dev-medium
              - devops
              - docs-dev
              - research-agent
            skills: []
            """);
        fixture.Write("profiles/models.yml", """
            schema: kyber-squad.model-profiles/v1
            profiles:
              general:
                default: inherit
            """);
        fixture.Write("profiles/capabilities.yml", """
            schema: kyber-squad.capability-profiles/v1
            capabilities:
              - filesystem.read
              - delegate
            profiles:
              orchestrator:
                permissions:
                  filesystem.read: allow
                  delegate: allow
              architect:
                permissions:
                  filesystem.read: allow
                  delegate: allow
              product-planning:
                permissions:
                  filesystem.read: allow
                  delegate: allow
              reviewer:
                permissions:
                  filesystem.read: allow
                  delegate: allow
              worker:
                permissions:
                  filesystem.read: allow
                  delegate: deny
              publishing-worker:
                permissions:
                  filesystem.read: allow
                  delegate: deny
              documentation:
                permissions:
                  filesystem.read: allow
                  delegate: deny
              read-only:
                permissions:
                  filesystem.read: allow
                  delegate: deny
            """);
        fixture.Write("profiles/fallbacks.yml", """
            schema: kyber-squad.fallback-profiles/v1
            profiles:
              role-skill:
                no-primary-agent: skill
                no-agent-primitive: skill
                body-source: agent
                output-identity:
                  unoccupied: agent-name
                  shared: reuse-skill
                  collision: role-prefixed-agent-name
                  prefix: role-
            """);
        fixture.Write("toolchain.yml", """
            schema: kyber-squad.toolchain/v1
            required-features:
              - agent-ir/v1
            validated-release: null
            """);
        fixture.Write("mcp.json", """
            {
              "mcpServers": {}
            }
            """);

        fixture.WriteAgent(Architect, "architect", "architect", "[research-agent]");
        fixture.WriteAgent(CodeReviewer, "reviewer", "reviewer", "[research-agent]");
        fixture.WriteAgent(Conductor, "orchestrator", "orchestrator", "[architect, dev-easy, dev-medium, docs-dev]");
        fixture.WriteAgent(ProductOwner, "product-planning", "product-planning", "[research-agent]");
        fixture.WriteAgent(CsharpDev, "worker", "worker", "[]");
        fixture.WriteAgent(DocsDev, "documentation", "documentation", "[]");
        fixture.WriteAgent(GithubDevops, "publishing-worker", "publishing-worker", "[]");
        fixture.WriteAgent(ResearchAgent, "read-only", "read-only", "[]");
        fixture.WriteAgent(TestDev, "worker", "worker", "[]");

        foreach (string schema in new[]
                 {
                     "squad",
                     "bundle",
                     "agent",
                     "model-profiles",
                     "capability-profiles"
                 })
        {
            fixture.Write($"schemas/{schema}.schema.json", """
                {
                  "$schema": "https://json-schema.org/draft/2020-12/schema",
                  "type": "object"
                }
                """);
        }
        fixture.Write("schemas/fallback-profiles.schema.json", """
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "$id": "https://kyber-weave.dev/schemas/kyber-squad/fallback-profiles/v1",
              "type": "object"
            }
            """);

        return fixture;
    }

    public void Dispose() => _tempDirectory?.Dispose();

    public void WriteAgent(string name, string description, string capabilityProfile, string delegatesTo)
    {
        Write($"agents/{name}.md", $"""
            ---
            schema: kyber-squad.agent/v1
            name: {name}
            description: Arbiter fixture agent exercising the {capabilityProfile} profile.
            invocation: subagent
            model-profile: general
            capability-profile: {capabilityProfile}
            copilot-tools: [vscode, read]
            delegates-to: {delegatesTo}
            fallback: role-skill
            aliases: []
            ---
            Follow the {description} instruction body.
            """);
    }

    /// <summary>Writes, or replaces, a file under the corpus root.</summary>
    /// <param name="relativePath">The path below the root.</param>
    /// <param name="content">The file text.</param>
    public void WriteFile(string relativePath, string content) => Write(relativePath, content);

    private void Write(string relativePath, string content)
    {
        string fullPath = System.IO.Path.Combine(Path, relativePath);
        string? directory = System.IO.Path.GetDirectoryName(fullPath);
        if (_files is not null)
        {
            if (directory is not null)
            {
                _files.CreateDirectory(directory);
            }

            _files.WriteAllText(fullPath, content);
            return;
        }

        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(fullPath, content);
    }
}
