using KyberWeave.Core.Docs.Scaffolding;
using KyberWeave.Tests.Corpus;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the shipped KW skill pool and the lists that name it: every shipped standards folder carries a
/// <c>&lt;scope&gt;.skills.yml</c> of skill names and no <c>.skills.md</c>, every listed name is in the pool,
/// each pooled skill is shaped as the catalog expects, and no KW skill is also a squad or companion skill.
/// </summary>
/// <remarks>
/// The lists are read through the same template reader <c>docs init</c> writes from, so a resource name that
/// disagrees with its file fails here rather than in a host repository.
/// </remarks>
public sealed class KwSkillPoolContentTests
{
    private static readonly ICorpusSource Corpus = CheckoutCorpusSource.Checkout;

    private const string StandardsRoot = "products/kyber-squad/standards";
    private const string PoolRoot = StandardsRoot + "/skills";
    private const string ManifestFile = PoolRoot + "/skills.manifest.yml";

    [Fact]
    public void NoShippedStandardsFolderHoldsASkillsMarkdownFile()
    {
        foreach (string scope in ScopeFolders())
        {
            Assert.False(
                Corpus.Exists($"{StandardsRoot}/{scope}/{scope}.skills.md"),
                $"The shipped '{scope}' folder still holds a {scope}.skills.md; its guidance is a KW skill now.");
        }
    }

    [Fact]
    public void EveryShippedFolderHasASkillsYmlListOfNames()
    {
        IReadOnlyList<string> folders = ScopeFolders();
        Assert.NotEmpty(folders);
        Assert.Equal(
            folders.Order(StringComparer.Ordinal),
            KyberStandardsTemplates.AllFolders.Order(StringComparer.Ordinal));

        foreach (string scope in folders)
        {
            Assert.True(
                ShippedKyberStandards.Templates.TryGetPart(scope, KyberStandardsTemplates.Part.Skills, out string? text),
                $"The '{scope}' folder ships no {scope}.skills.yml through the template reader.");
            Assert.Equal(Expected(scope), ParseList(text, scope));
        }
    }

    [Fact]
    public void EveryListedShippedSkillIsInThePool()
    {
        IReadOnlyList<string> pool = PoolNames();
        foreach (string scope in ScopeFolders())
        {
            ShippedKyberStandards.Templates.TryGetPart(scope, KyberStandardsTemplates.Part.Skills, out string? text);
            foreach (string name in ParseList(text!, scope))
            {
                Assert.Contains(name, pool);
            }
        }
    }

    [Fact]
    public void EveryShippedSkillHasNameEqualToFolderAndADescription()
    {
        foreach (string name in PoolNames())
        {
            string text = Corpus.ReadText($"{PoolRoot}/{name}/SKILL.md").ReplaceLineEndings("\n");
            Assert.StartsWith("---\n", text, StringComparison.Ordinal);
            int end = text.IndexOf("\n---\n", 4, StringComparison.Ordinal);
            Assert.True(end > 0, $"{name}/SKILL.md has no closed frontmatter.");
            string[] lines = text[4..end].Split('\n');

            Assert.Contains($"name: {name}", lines);
            Assert.Contains(
                lines,
                line => line.StartsWith("description:", StringComparison.Ordinal)
                        && line["description:".Length..].Trim().Trim('"').Length > 0);
        }
    }

    [Fact]
    public void TheManifestListsExactlyThePoolsFiles()
    {
        Dictionary<string, List<string>> manifest = ParseManifest(Corpus.ReadText(ManifestFile));
        Assert.Equal(
            Corpus.EnumerateDirectories(PoolRoot).Select(Path.GetFileName).Order(StringComparer.Ordinal),
            manifest.Keys.Order(StringComparer.Ordinal));

        foreach ((string name, List<string> listed) in manifest)
        {
            List<string> actual = [.. FilesUnder($"{PoolRoot}/{name}").Select(file => file[($"{PoolRoot}/{name}/").Length..])];
            Assert.Equal(actual.Order(StringComparer.Ordinal), listed.Order(StringComparer.Ordinal));
        }
    }

    [Fact]
    public void NoKwSkillIsASquadSkillAndThePackCarriesNone()
    {
        IReadOnlyList<string> pool = PoolNames();
        string[] others =
        [
            .. Corpus.EnumerateDirectories("products/kyber-squad/skills").Select(Path.GetFileName).OfType<string>(),
            .. Corpus.EnumerateDirectories(".apm/skills").Select(Path.GetFileName).OfType<string>()
        ];

        Assert.Empty(pool.Intersect(others, StringComparer.Ordinal));

        // The squad's own source folders never include standards/, which is where the pool lives.
        Assert.DoesNotContain("standards", Corpus.EnumerateDirectories("products/kyber-squad/skills").Select(Path.GetFileName));
    }

    private static IReadOnlyList<string> ScopeFolders() =>
        [.. Corpus.EnumerateDirectories(StandardsRoot)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(name => !string.Equals(name, "skills", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)];

    private static IReadOnlyList<string> PoolNames() =>
        [.. Corpus.EnumerateDirectories(PoolRoot).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal)];

    private static IEnumerable<string> FilesUnder(string directory) => Corpus.EnumerateFiles(directory);

    /// <summary>The lists D19 assigns: one skill for the technologies that had real guidance, none elsewhere.</summary>
    private static string[] Expected(string scope) => scope switch
    {
        "csharp" or "github-actions" => ["dotnet-build-diagnostics"],
        "aws" => ["aws-cli-queries"],
        "azure" => ["azure-resource-inspection"],
        "go" => ["go-gates"],
        "maui" => ["maui-environment-doctor"],
        "python" => ["pylance-tooling"],
        "rust" => ["rust-cross-build"],
        "sql-server" => ["sql-server-reference"],
        "tauri" => ["tauri-v2-reference"],
        _ => []
    };

    /// <summary>Reads a flow-style or block YAML sequence of names and refuses anything else.</summary>
    private static string[] ParseList(string text, string scope)
    {
        string trimmed = text.Trim();
        if (trimmed.StartsWith('['))
        {
            Assert.EndsWith("]", trimmed, StringComparison.Ordinal);
            string inner = trimmed[1..^1];
            return [.. inner.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];
        }

        string[] lines = trimmed.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.All(lines, line => Assert.True(line.StartsWith("- ", StringComparison.Ordinal), $"The '{scope}' skills list holds '{line}', which is not a name."));
        return [.. lines.Select(line => line[2..].Trim())];
    }

    private static Dictionary<string, List<string>> ParseManifest(string text)
    {
        Dictionary<string, List<string>> result = new(StringComparer.Ordinal);
        List<string>? current = null;
        foreach (string raw in text.ReplaceLineEndings("\n").Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("- name:", StringComparison.Ordinal))
            {
                current = [];
                result[line["- name:".Length..].Trim()] = current;
            }
            else if (line.StartsWith("- ", StringComparison.Ordinal) && current is not null)
            {
                current.Add(line[2..].Trim());
            }
        }

        return result;
    }
}
