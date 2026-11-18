using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Packaging;
using KyberWeave.Testing.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Unit twin of <c>SquadPackResponsePreferencesTests</c>: packing an in-memory source whose carriers hold the
/// canonical empty <c>&lt;response-preferences&gt;</c> block ships the default text in every carrier, leaves no empty
/// block in any archive, and writes the same bytes on a second pack.
/// </summary>
public sealed class SquadPackResponsePreferencesUnitTests
{
    private const string ConductorAgent = "agents/conductor.md";
    private const string BugCrusherAgent = "agents/bug-crusher.md";
    private const string BugCrusherSkill = "skills/bug-crusher/SKILL.md";

    private static readonly string Source = Path.GetFullPath("/repo/products/kyber-squad");
    private static readonly string Out = Path.GetFullPath("/repo/artifacts");

    private static readonly Regex BlockPattern = new(
        "<response-preferences>(?<body>.*?)</response-preferences>",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private readonly InMemoryFileSystem _files = new(caseSensitive: true);

    /// <summary>The APM archive fills the conductor and both bug-crusher carriers with the shipped default.</summary>
    [Fact]
    public void PackApmShipsTheDefaultInTheConductorAndBugCrusherCarriers()
    {
        SeedCarriers();

        Dictionary<string, string> entries = ReadEntries(NewPacker().PackApm(Source, Out, "rp-apm"));

        AssertCarrierShipsDefault(entries, ConductorAgent);
        AssertCarrierShipsDefault(entries, BugCrusherAgent);
        AssertCarrierShipsDefault(entries, BugCrusherSkill);
    }

    /// <summary>The plugins archive fills the bug-crusher skill carrier with the shipped default.</summary>
    [Fact]
    public void PackPluginsShipsTheDefaultInTheBugCrusherSkillCarrier()
    {
        SeedCarriers();

        Dictionary<string, string> entries = ReadEntries(NewPacker().PackPlugins(Source, Out, "rp-plugins"));

        AssertCarrierShipsDefault(entries, BugCrusherSkill);
    }

    /// <summary>No entry of either archive keeps the canonical empty block, and packing twice writes the same bytes.</summary>
    [Fact]
    public void PackLeavesNoEmptyBlockAndIsDeterministic()
    {
        SeedCarriers();
        SquadPacker packer = NewPacker();

        string apm = packer.PackApm(Source, Out, "rp-det");
        string plugins = packer.PackPlugins(Source, Out, "rp-det");
        byte[] apmBytes = _files.ReadAllBytes(apm);
        byte[] pluginsBytes = _files.ReadAllBytes(plugins);

        foreach (string path in new[] { apm, plugins })
        {
            Assert.DoesNotContain(
                ReadEntries(path),
                entry => Regex.IsMatch(entry.Value, "<response-preferences>\\s*</response-preferences>"));
        }

        _ = packer.PackApm(Source, Out, "rp-det");
        _ = packer.PackPlugins(Source, Out, "rp-det");
        Assert.True(apmBytes.AsSpan().SequenceEqual(_files.ReadAllBytes(apm)));
        Assert.True(pluginsBytes.AsSpan().SequenceEqual(_files.ReadAllBytes(plugins)));
    }

    private SquadPacker NewPacker() => new(_files, new FakeSquadCompanionSkills());

    private void SeedCarriers()
    {
        Write("squad.yml", "name: demo\n");
        foreach (string carrier in new[] { ConductorAgent, BugCrusherAgent, BugCrusherSkill })
        {
            Write(carrier, "---\nname: carrier\n---\nBefore.\n<response-preferences></response-preferences>\nAfter.\n");
        }
    }

    private void Write(string relativePath, string content)
    {
        string path = Path.Combine(Source, relativePath.Replace('/', Path.DirectorySeparatorChar));
        _files.CreateDirectory(Path.GetDirectoryName(path)!);
        _files.WriteAllText(path, content);
    }

    private Dictionary<string, string> ReadEntries(string archivePath)
    {
        using MemoryStream buffer = new(_files.ReadAllBytes(archivePath));
        using ZipArchive archive = new(buffer, ZipArchiveMode.Read);
        return archive.Entries.ToDictionary(
            entry => entry.FullName,
            entry =>
            {
                using StreamReader reader = new(entry.Open(), Encoding.UTF8);
                return reader.ReadToEnd();
            },
            StringComparer.Ordinal);
    }

    private static void AssertCarrierShipsDefault(Dictionary<string, string> entries, string entryName)
    {
        Assert.True(entries.TryGetValue(entryName, out string? text), $"The archive does not carry '{entryName}'.");
        MatchCollection matches = BlockPattern.Matches(text);
        Match block = Assert.Single(matches);
        Assert.Equal(SquadResponseGuidanceSource.DefaultText, block.Groups["body"].Value);
    }
}
