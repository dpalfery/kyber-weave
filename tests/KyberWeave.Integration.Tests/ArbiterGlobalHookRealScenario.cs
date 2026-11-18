using System.Text;
using System.Text.Json.Nodes;
using KyberWeave.Arbiter;
using KyberWeave.Tests.Corpus;

namespace KyberWeave.Tests;

/// <summary>
/// A project root, a twin root (the same project again, for the project-scope baseline) and a home
/// directory, all under the system temp folder and deleted on dispose. Payloads are built from the
/// Claude fixtures with the fields a row needs edited.
/// </summary>
internal sealed class ArbiterGlobalHookScenario : IDisposable
{
    public const string Harness = "claude";
    public const string DispatchMatcher = "^(Agent|Task)$";
    public const string ConductorCommand = "kyber-weave-arbiter hook --harness claude --caller conductor";
    public const string DeferredLog = "deferred-to-project";
    public const string NotTwiceLog = "not evaluated twice";
    public const string UnmarkedPrompt = "Plan the parser.";

    private ArbiterGlobalHookScenario(string projectRoot, string twinRoot, string home)
    {
        ProjectRoot = projectRoot;
        TwinRoot = twinRoot;
        Home = home;
    }

    public string ProjectRoot { get; }

    public string TwinRoot { get; }

    public string Home { get; }

    /// <summary>Creates the three roots, each project root holding the same configuration.</summary>
    public static ArbiterGlobalHookScenario Create(bool projectEnabled = true)
    {
        ArbiterGlobalHookScenario scenario = new(CreateTempDirectory(), CreateTempDirectory(), CreateTempDirectory());
        string yaml = projectEnabled ? "arbiter:\n  enabled: true\n" : "arbiter:\n  enabled: false\n";
        foreach (string root in new[] { scenario.ProjectRoot, scenario.TwinRoot })
        {
            string configDirectory = Path.Combine(root, ".kyber-weave");
            Directory.CreateDirectory(configDirectory);
            File.WriteAllText(Path.Combine(configDirectory, "kyber-weave.yml"), yaml);
        }

        return scenario;
    }

    /// <summary>Writes a file under both the project root and the twin root, so both hold the same tree.</summary>
    public void WriteProjectFile(string relativePath, string content)
    {
        WriteFile(ProjectRoot, relativePath, content);
        WriteFile(TwinRoot, relativePath, content);
    }

    /// <summary>Runs the user-level hook against this scenario's home.</summary>
    public Task<HookRun> RunGlobal(string payload, string harness = Harness) =>
        Invoke(["hook", "--harness", harness, "--scope", "global", "--home", Home], payload);

    /// <summary>Runs the project-scope hook: no <c>--scope</c> and no <c>--home</c>.</summary>
    public static Task<HookRun> RunProject(string payload, string? caller = null)
    {
        string[] args = caller is null
            ? ["hook", "--harness", Harness]
            : ["hook", "--harness", Harness, "--caller", caller];
        return Invoke(args, payload);
    }

    public static string DecisionsPath(string root) => Path.Combine(root, "artifacts", "arbiter", "decisions.jsonl");

    /// <summary>The number of decision lines written under the root, zero when the ledger is absent.</summary>
    public static int DecisionLines(string root)
    {
        string path = DecisionsPath(root);
        return File.Exists(path) ? File.ReadAllLines(path).Count(line => line.Length > 0) : 0;
    }

    public static string ClaimPath(string root, string toolUseId, string phase) =>
        Path.Combine(root, "artifacts", "arbiter", "claims", $"{toolUseId}.{phase}");

    /// <summary>A PreToolUse Agent dispatch. A null agent type is the main thread.</summary>
    public static string Dispatch(string root, string toolUseId, string? agentType, string subagentType, string prompt) =>
        Fixture(root, Path.Combine("claude", "pre-agent-dispatch.json"), payload =>
            ApplyDispatch(payload, toolUseId, agentType, subagentType, prompt));

    /// <summary>The PostToolUse completion of a dispatch. Pass the same tool use id as the pre event.</summary>
    public static string PostDispatch(string root, string toolUseId, string? agentType, string subagentType, string prompt) =>
        Fixture(root, Path.Combine("claude", "post-completed.json"), payload =>
            ApplyDispatch(payload, toolUseId, agentType, subagentType, prompt));

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

    public void Dispose()
    {
        foreach (string root in new[] { ProjectRoot, TwinRoot, Home })
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task<HookRun> Invoke(string[] args, string payload)
    {
        using StringReader stdin = new(payload);
        using StringWriter stdout = new();
        using StringWriter stderr = new();
        int exit = await Composition.DispatchAsync(args, stdin, stdout, stderr);
        return new HookRun(exit, stdout.ToString(), stderr.ToString());
    }

    private static string Fixture(string root, string relativeFixture, Action<JsonObject> edit)
    {
        string fixturePath = $"tests/KyberWeave.Application.Tests/Fixtures/arbiter-hooks/{relativeFixture.Replace('\\', '/')}";

        JsonObject payload = JsonNode.Parse(CheckoutCorpusSource.Checkout.ReadText(fixturePath))!.AsObject();
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

    private static void WriteFile(string root, string relativePath, string content)
    {
        string path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "kw-arbiter-global-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}

/// <summary>The captured result of one hook invocation.</summary>
internal sealed record HookRun(int Exit, string Stdout, string Stderr);
