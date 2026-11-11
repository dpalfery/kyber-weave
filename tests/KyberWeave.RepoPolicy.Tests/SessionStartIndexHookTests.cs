using System.Text.Json;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the session-start hooks that build the CodeGraph index: every harness hook file runs
/// <c>kyber-weave doctor --ensure</c> in the background, discards its output, and always exits 0,
/// so a session never waits on indexing or fails because of it.
/// </summary>
public sealed class SessionStartIndexHookTests
{
    private const string Ensure = "kyber-weave doctor --ensure";

    /// <summary>Hook file, the JSON property that holds its command, and the session-start event key it must sit under.</summary>
    public static TheoryData<string, string, string> JsonHooks => new()
    {
        { ".claude/settings.json", "command", "SessionStart" },
        { ".factory/hooks.json", "command", "SessionStart" },
        { ".codex/hooks.json", "command", "SessionStart" },
        { ".commandcode/settings.json", "command", "SessionStart" },
        { ".devin/hooks.v1.json", "command", "SessionStart" },
        { ".agents/hooks.json", "command", "SessionStart" },
        { ".cursor/hooks.json", "command", "sessionStart" },
        { ".github/hooks/codegraph-index.json", "bash", "sessionStart" },
    };

    public static TheoryData<string> Shims => new()
    {
        ".pi/extensions/codegraph-index.ts",
        ".kilo/plugin/codegraph-index.ts",
        ".opencode/plugins/codegraph-index.ts",
    };

    /// <summary>A session-start hook that does not run the ensure command leaves the index unbuilt.</summary>
    [Theory]
    [MemberData(nameof(JsonHooks))]
    public void HookRunsDoctorEnsureInTheBackgroundAndAlwaysExitsZero(string file, string property, string eventKey)
    {
        string command = FindCommand(file, property, eventKey);

        Assert.Contains(Ensure, command, StringComparison.Ordinal);
        Assert.Contains(">/dev/null 2>&1 </dev/null &", command, StringComparison.Ordinal);
        Assert.EndsWith("exit 0", command, StringComparison.Ordinal);
        Assert.Contains("command -v kyber-weave", command, StringComparison.Ordinal);
        Assert.DoesNotContain("$(git rev-parse --show-toplevel)", command, StringComparison.Ordinal);
    }

    /// <summary>A hook with a long timeout would delay session start while the index builds.</summary>
    [Theory]
    [MemberData(nameof(JsonHooks))]
    public void HookDeclaresNoLongTimeout(string file, string property, string eventKey)
    {
        using JsonDocument doc = Load(file);
        JsonElement entry = FindEntry(doc.RootElement, property, eventKey);
        foreach (string name in new[] { "timeout", "timeoutSec" })
        {
            if (entry.TryGetProperty(name, out JsonElement t))
                Assert.True(t.GetInt32() <= 10, $"{file}: the ensure hook must not allow more than 10 seconds.");
        }
    }

    /// <summary>The TypeScript hosts load the shim in-process, so it must spawn detached and survive a missing binary.</summary>
    [Theory]
    [MemberData(nameof(Shims))]
    public void ShimSpawnsDoctorEnsureDetachedWithAnErrorListener(string file)
    {
        string text = File.ReadAllText(Path.Combine(KyberWeaveTestPaths.ToolRoot, file));

        Assert.Contains("\"kyber-weave\", [\"doctor\", \"--ensure\", \".\"]", text, StringComparison.Ordinal);
        Assert.Contains("detached: true", text, StringComparison.Ordinal);
        Assert.Contains(".on(\"error\"", text, StringComparison.Ordinal);
        Assert.Contains(".unref()", text, StringComparison.Ordinal);
        Assert.DoesNotContain("spawnSync", text, StringComparison.Ordinal);
        Assert.DoesNotContain("execSync", text, StringComparison.Ordinal);
    }

    private static JsonDocument Load(string file) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(KyberWeaveTestPaths.ToolRoot, file)));

    private static string FindCommand(string file, string property, string eventKey)
    {
        using JsonDocument doc = Load(file);
        string? command = FindEntry(doc.RootElement, property, eventKey).GetProperty(property).GetString();
        return command ?? throw new InvalidOperationException($"{file}: '{property}' is not a string.");
    }

    private static JsonElement FindEntry(JsonElement root, string property, string eventKey)
    {
        JsonElement scope = default;
        foreach (JsonProperty e in Walk(root))
        {
            if (e.Name == eventKey)
                scope = e.Value;
        }

        if (scope.ValueKind == JsonValueKind.Undefined)
            throw new InvalidOperationException($"No '{eventKey}' event key found.");
        root = scope;

        foreach (JsonProperty p in Walk(root))
        {
            if (p.Name == property && p.Value.ValueKind == JsonValueKind.String
                && p.Value.GetString()!.Contains(Ensure, StringComparison.Ordinal))
            {
                return Parent(root, p);
            }
        }

        throw new InvalidOperationException($"No hook entry runs '{Ensure}'.");
    }

    private static JsonElement Parent(JsonElement root, JsonProperty target)
    {
        foreach (JsonElement e in Objects(root))
        {
            foreach (JsonProperty p in e.EnumerateObject())
            {
                if (p.Name == target.Name && p.Value.GetRawText() == target.Value.GetRawText())
                    return e;
            }
        }

        throw new InvalidOperationException("Parent not found.");
    }

    private static IEnumerable<JsonProperty> Walk(JsonElement e) =>
        Objects(e).SelectMany(o => o.EnumerateObject());

    private static IEnumerable<JsonElement> Objects(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            yield return e;
            foreach (JsonProperty p in e.EnumerateObject())
                foreach (JsonElement c in Objects(p.Value))
                    yield return c;
        }
        else if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement i in e.EnumerateArray())
                foreach (JsonElement c in Objects(i))
                    yield return c;
        }
    }
}
