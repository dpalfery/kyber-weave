using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Tests.Corpus;
using Microsoft.Extensions.DependencyInjection;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// A project root, a twin host holding the same project (the project-scope baseline) and a home directory, each
/// over its own in-memory file system and log, so the user-level hook runs with no disk. Payloads are built from the
/// Claude and Cursor fixtures with the fields a row needs edited.
/// </summary>
internal sealed class ArbiterGlobalHookScenario : IDisposable
{
    public const string Harness = "claude";
    public const string DispatchMatcher = "^(Agent|Task)$";
    public const string ConductorCommand = "kyber-weave-arbiter hook --harness claude --caller conductor";
    public const string CsharpDevCommand = "kyber-weave-arbiter hook --harness claude --caller csharp-dev";
    public const string EvaluatingLog = "evaluating under the project configuration";
    public const string DeferredLog = "deferred-to-project";
    public const string NotTwiceLog = "not evaluated twice";
    public const string FailClosedCode = "KW-ARB-HOOK-001";
    public const string UnmarkedPrompt = "Plan the parser.";

    private ArbiterGlobalHookScenario(InMemoryArbiterHost project, InMemoryArbiterHost twin)
    {
        Project = project;
        Twin = twin;
    }

    /// <summary>Gets the host the user-level hook and the project hook run against.</summary>
    public InMemoryArbiterHost Project { get; }

    /// <summary>Gets the host that runs the project-scope baseline of the same project.</summary>
    public InMemoryArbiterHost Twin { get; }

    /// <summary>Gets the project root, the same path in both hosts.</summary>
    public string ProjectRoot => Project.RepoRoot;

    /// <summary>Gets the home directory the user-level hook is told.</summary>
    public string Home => Project.HomeDirectory;

    /// <summary>Gets the project root of the twin host.</summary>
    public string TwinRoot => Twin.RepoRoot;

    /// <summary>Creates both hosts, each holding the same project configuration.</summary>
    /// <param name="projectEnabled">Whether the project configuration enables the Arbiter.</param>
    /// <param name="clock">The clock the project host runs on; the host's own fake clock when null.</param>
    public static ArbiterGlobalHookScenario Create(bool projectEnabled = true, TimeProvider? clock = null)
    {
        InMemoryArbiterHost project = clock is null
            ? InMemoryArbiterHost.Create()
            : InMemoryArbiterHost.Create(services => services.AddSingleton(clock));
        ArbiterGlobalHookScenario scenario = new(project, InMemoryArbiterHost.Create());
        string yaml = projectEnabled ? "arbiter:\n  enabled: true\n" : "arbiter:\n  enabled: false\n";
        scenario.Project.WriteRepoConfig(yaml);
        scenario.Twin.WriteRepoConfig(yaml);
        return scenario;
    }

    /// <summary>Writes a file under the project root in both hosts, so both hold the same tree.</summary>
    public void WriteProjectFile(string relativePath, string content)
    {
        _ = Project.Write(Path.Combine(ProjectRoot, relativePath), content);
        _ = Twin.Write(Path.Combine(Twin.RepoRoot, relativePath), content);
    }

    public void WriteHomeFile(string relativePath, string content) => Project.Write(Path.Combine(Home, relativePath), content);

    /// <summary>Runs the user-level hook against this scenario's home.</summary>
    public HookRun RunGlobal(string payload, string harness = Harness) =>
        Run(Project, harness, null, payload, HookScope.Global, Home);

    /// <summary>Runs the project-scope hook on the project host: no scope and no home.</summary>
    public HookRun RunProject(string payload, string? caller = null) =>
        Run(Project, Harness, caller, payload, HookScope.Project, null);

    /// <summary>Runs the project-scope hook on the twin host.</summary>
    public HookRun RunTwin(string payload, string? caller = null) =>
        Run(Twin, Harness, caller, payload, HookScope.Project, null);

    /// <summary>
    /// Masks the <c>DECISION_ID</c> a deny document carries. Each evaluation mints a new id, so two
    /// runs of the same dispatch differ only there; every other byte must still match the twin.
    /// </summary>
    public static string WithoutDecisionId(string stdout) =>
        Regex.Replace(stdout, "DECISION_ID: [0-9A-Za-z-]+", "DECISION_ID: <minted>", RegexOptions.None, TimeSpan.FromSeconds(5));

    /// <summary>The number of decisions recorded under the host's project root.</summary>
    public static int DecisionLines(InMemoryArbiterHost host) => host.Decisions.Count;

    public static string ClaimPath(string root, string toolUseId, string phase) =>
        Path.Combine(root, "artifacts", "arbiter", "claims", $"{toolUseId}.{phase}");

    public void WriteClaim(string toolUseId, string phase, string source) =>
        Project.Write(ClaimPath(ProjectRoot, toolUseId, phase), source + "\n");

    public bool ClaimExists(string toolUseId, string phase) =>
        Project.Files.FileExists(ClaimPath(ProjectRoot, toolUseId, phase));

    public string ClaimOwner(string toolUseId, string phase) =>
        Project.Files.ReadAllText(ClaimPath(ProjectRoot, toolUseId, phase)).Trim();

    /// <summary>Whether anything was recorded under a directory, as the log fake or the file system holds it.</summary>
    public static bool RecordedUnder(InMemoryArbiterHost host, string directory) =>
        host.State.Log.AppendedPaths.Any(path => path.StartsWith(directory, StringComparison.Ordinal))
        || host.Files.DirectoryExists(directory);

    public IReadOnlyList<string> ClaimFiles() =>
        Project.Files.DirectoryExists(Path.Combine(ProjectRoot, "artifacts", "arbiter", "claims"))
            ? [.. Project.Files.EnumerateFiles(Path.Combine(ProjectRoot, "artifacts", "arbiter", "claims"), "*", recursive: false)]
            : [];

    public bool ClaimsFolderExists() =>
        Project.Files.DirectoryExists(Path.Combine(ProjectRoot, "artifacts", "arbiter", "claims"));

    /// <summary>A PreToolUse Agent dispatch. A null agent type is the main thread.</summary>
    public static string Dispatch(string root, string toolUseId, string? agentType, string subagentType, string prompt) =>
        Fixture(root, "claude/pre-agent-dispatch.json", payload =>
            ApplyDispatch(payload, toolUseId, agentType, subagentType, prompt));

    /// <summary>The PostToolUse completion of a dispatch. Pass the same tool use id as the pre event.</summary>
    public static string PostDispatch(string root, string toolUseId, string? agentType, string subagentType, string prompt) =>
        Fixture(root, "claude/post-completed.json", payload =>
            ApplyDispatch(payload, toolUseId, agentType, subagentType, prompt));

    public static string ReadPayload(string root, string toolUseId) =>
        Fixture(root, "claude/pre-read-allowed.json", payload =>
            payload["tool_use_id"] = toolUseId);

    public static string CursorTaskDispatch(string root) =>
        Fixture(root, "cursor/pre-task-dispatch.json", _ => { });

    /// <summary>The Claude PreToolUse hand-back fixture, run in the given project.</summary>
    public static string Handback(string root) =>
        Fixture(root, "claude/pre-subagent-handback.json", _ => { });

    /// <summary>A Bash PreToolUse payload from the shipped fixture, run as the architect in the given project.</summary>
    public static string ShellPayload(string root, string command) =>
        Fixture(root, "claude/pre-bash-denied.json", payload =>
        {
            payload["agent_type"] = "architect";
            payload["tool_input"] = new JsonObject { ["command"] = command };
        });

    /// <summary>An agent or skill file whose frontmatter carries the given hook entries.</summary>
    public static string HookDocument(string name, string command, params (string Event, string Matcher)[] entries)
    {
        StringBuilder document = new();
        document.Append("---\n");
        document.Append("name: ").Append(name).Append('\n');
        document.Append("description: Fixture for the user-level Arbiter hook.\n");
        document.Append("hooks:\n");
        foreach (IGrouping<string, (string Event, string Matcher)> group in entries.GroupBy(entry => entry.Event, StringComparer.Ordinal))
        {
            document.Append("  ").Append(group.Key).Append(":\n");
            foreach ((string Event, string Matcher) entry in group)
            {
                document.Append("    - matcher: \"").Append(entry.Matcher).Append("\"\n");
                document.Append("      hooks:\n");
                document.Append("        - type: command\n");
                document.Append("          command: \"").Append(command).Append("\"\n");
            }
        }

        document.Append("---\n\nFixture body.\n");
        return document.ToString();
    }

    /// <summary>A skill file with frontmatter and no hooks.</summary>
    public static string PlainSkillDocument(string name) =>
        $"---\nname: {name}\ndescription: Personal fixture without hooks.\n---\n\nFixture body.\n";

    public void Dispose()
    {
        Project.Dispose();
        Twin.Dispose();
    }

    /// <summary>Runs one hook invocation over the host's in-memory ports.</summary>
    internal static HookRun Run(
        InMemoryArbiterHost host,
        string harness,
        string? caller,
        string payload,
        HookScope scope,
        string? home)
    {
        HookCommand command = host.HookCommand(host.Registry(host.Engine()));
        using StringWriter stdout = new();
        using StringWriter stderr = new();
        int exit = command.Run(harness, caller, payload, stdout, stderr, scope, home);
        return new HookRun(exit, stdout.ToString(), stderr.ToString());
    }

    internal static string Fixture(string root, string relativeFixture, Action<JsonObject> edit)
    {
        JsonObject payload = JsonNode.Parse(
            EmbeddedFixtureText.Read(typeof(ArbiterGlobalHookScenario).Assembly, $"arbiter-hooks/{relativeFixture}"))!.AsObject();
        payload["cwd"] = root;
        payload["transcript_path"] = Path.Combine(root, "transcript.jsonl");
        edit(payload);
        return payload.ToJsonString();
    }

    private static void ApplyDispatch(JsonObject payload, string toolUseId, string? agentType, string subagentType, string prompt)
    {
        if (agentType is null)
        {
            payload.Remove("agent_type");
        }
        else
        {
            payload["agent_type"] = agentType;
        }

        payload["tool_use_id"] = toolUseId;
        JsonObject toolInput = payload["tool_input"]!.AsObject();
        toolInput["subagent_type"] = subagentType;
        toolInput["prompt"] = prompt;
    }
}

/// <summary>The captured result of one hook invocation.</summary>
internal sealed record HookRun(int Exit, string Stdout, string Stderr);
