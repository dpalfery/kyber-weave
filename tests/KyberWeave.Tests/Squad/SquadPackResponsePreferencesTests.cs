using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using KyberWeave.Core.Squad.Packaging;
using Xunit;

namespace KyberWeave.Tests.Squad;

/// <summary>
/// Pins decision D-A: canonical sources keep an empty <c>&lt;response-preferences&gt;</c> block,
/// <c>squad install</c> and <c>squad update</c> fill it with the operator's effective text, and
/// <c>squad pack</c> fills every conductor and bug-crusher carrier with the shipped default so a
/// packed archive is self-contained and deterministic. Without this, an archive ships the canonical
/// placeholder line and the operator's home file leaks into a distributable artifact.
/// </summary>
public sealed class SquadPackResponsePreferencesTests : IDisposable
{
    private const string ConductorAgent = "agents/conductor.md";
    private const string BugCrusherAgent = "agents/bug-crusher.md";
    private const string BugCrusherSkill = "skills/bug-crusher/SKILL.md";

    private static readonly Regex BlockPattern = new(
        "<response-preferences>(?<body>.*?)</response-preferences>",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex EmptyBlockPattern = new(
        "<response-preferences>\\s*</response-preferences>",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static string ProductRoot =>
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");

    private static string DefaultText =>
        File.ReadAllText(Path.Combine(ProductRoot, "response-guidance.default.md"), Encoding.UTF8);

    private const string OperatorText = "operator pack-isolation sentinel: must never reach an archive";

    /// <summary>
    /// Guards the packed conductor carrier: a shipped APM archive must carry the default text inside its
    /// response-preferences block, never the canonical placeholder or an empty block, so the archive
    /// routes the same preferences on every machine.
    /// </summary>
    [Fact]
    public void Pack_Apm_ShipsTheShippedDefaultInTheConductorAndBugCrusherCarriers()
    {
        string archivePath = SquadPacker.PackApm(ProductRoot, _temp.Path, "rp-apm");
        using ZipArchive archive = ZipFile.OpenRead(archivePath);

        AssertCarrierShipsDefault(archive, ConductorAgent);
        AssertCarrierShipsDefault(archive, BugCrusherAgent);
        AssertCarrierShipsDefault(archive, BugCrusherSkill);
    }

    /// <summary>
    /// Guards the Agent Plugins archive: the bug-crusher skill is the only carrier it ships, so that
    /// skill must carry the shipped default; a plugin install that kept the placeholder would route
    /// the wrong preferences with no install step to repair it.
    /// </summary>
    [Fact]
    public void Pack_Plugins_ShipsTheShippedDefaultInTheBugCrusherSkillCarrier()
    {
        string archivePath = SquadPacker.PackPlugins(ProductRoot, _temp.Path, "rp-plugins");
        using ZipArchive archive = ZipFile.OpenRead(archivePath);

        AssertCarrierShipsDefault(archive, BugCrusherSkill);
    }

    /// <summary>
    /// Guards against an empty block surviving into a distributable archive: an empty block is the
    /// canonical source's pre-install state, and shipping it would leave the operator with no
    /// preferences at all after an offline install from the archive.
    /// </summary>
    [Fact]
    public void Pack_NoPackedArchiveHoldsAnEmptyResponsePreferencesBlock()
    {
        string apmPath = SquadPacker.PackApm(ProductRoot, _temp.Path, "rp-empty-apm");
        string pluginsPath = SquadPacker.PackPlugins(ProductRoot, _temp.Path, "rp-empty-plugins");

        foreach (string archivePath in new[] { apmPath, pluginsPath })
        {
            using ZipArchive archive = ZipFile.OpenRead(archivePath);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string text = ReadEntryText(entry);
                Assert.False(
                    EmptyBlockPattern.IsMatch(text),
                    $"{Path.GetFileName(archivePath)} entry '{entry.FullName}' holds an empty <response-preferences> block.");
            }
        }
    }

    /// <summary>
    /// Guards byte-level determinism: packing twice from the same inputs must yield identical archive
    /// bytes, member order, and fixed member timestamps, because the release checksum file depends on it.
    /// This test is a regression guard, not a red: the packer is already deterministic, and it must stay so
    /// once the default text is filled in.
    /// </summary>
    [Fact]
    public void Pack_TwiceFromTheSameInputs_ProducesByteIdenticalArchivesWithFixedMemberTimestamps()
    {
        string firstDirectory = Path.Combine(_temp.Path, "first");
        string secondDirectory = Path.Combine(_temp.Path, "second");

        string firstApm = SquadPacker.PackApm(ProductRoot, firstDirectory, "rp-det");
        string secondApm = SquadPacker.PackApm(ProductRoot, secondDirectory, "rp-det");
        string firstPlugins = SquadPacker.PackPlugins(ProductRoot, firstDirectory, "rp-det");
        string secondPlugins = SquadPacker.PackPlugins(ProductRoot, secondDirectory, "rp-det");

        AssertArchivesIdentical(firstApm, secondApm);
        AssertArchivesIdentical(firstPlugins, secondPlugins);
    }

    /// <summary>
    /// Guards the isolation rule as a output guarantee, not a seam test: the packer takes no home or environment
    /// input, so the packed <c>&lt;response-preferences&gt;</c> block must equal the shipped default byte-for-byte
    /// for both carriers. No temp home is built here because the packer cannot be pointed at one; the test
    /// instead asserts that the operator marker text, which a home-reading packer would have to copy in, appears
    /// in no archive entry. The marker is a constant, so this check guards the default content, not a real home.
    /// </summary>
    [Fact]
    public void Pack_OfTheShippedSourceShipsTheDefaultAndNoOperatorMarkerBecauseItTakesNoHomeInput()
    {
        string apmPath = SquadPacker.PackApm(ProductRoot, _temp.Path, "rp-home");
        string pluginsPath = SquadPacker.PackPlugins(ProductRoot, _temp.Path, "rp-home");

        using (ZipArchive apmArchive = ZipFile.OpenRead(apmPath))
        {
            AssertCarrierShipsDefault(apmArchive, ConductorAgent);
        }

        using (ZipArchive pluginsArchive = ZipFile.OpenRead(pluginsPath))
        {
            AssertCarrierShipsDefault(pluginsArchive, BugCrusherSkill);
        }

        foreach (string archivePath in new[] { apmPath, pluginsPath })
        {
            using ZipArchive archive = ZipFile.OpenRead(archivePath);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                Assert.False(
                    ReadEntryText(entry).Contains(OperatorText, StringComparison.Ordinal),
                    $"{Path.GetFileName(archivePath)} entry '{entry.FullName}' contains the operator marker text.");
            }
        }
    }

    /// <summary>
    /// Guards the isolation proof itself: a packer that accepted an environment or home resolver would make the
    /// no-leak check circular, because a test could only show the seam was consulted, never that it was ignored.
    /// Every public member of <see cref="SquadPacker"/> is scanned, so a new entry point cannot reintroduce a seam.
    /// </summary>
    [Fact]
    public void Packer_PublicSurfaceAcceptsNoEnvironmentOrHomeParameter()
    {
        MethodInfo[] packerMethods = typeof(SquadPacker)
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        Assert.NotEmpty(packerMethods);
        foreach (MethodInfo method in packerMethods)
        {
            foreach (ParameterInfo parameter in method.GetParameters())
            {
                Assert.False(
                    IsHomeOrEnvironmentInput(parameter),
                    $"SquadPacker.{method.Name} takes a home or environment input '{parameter.Name}' of type '{parameter.ParameterType.Name}'; the packer must take no home or environment input.");
            }
        }
    }

    /// <summary>
    /// Proves the surface predicate bites: each rejected shape below must be flagged, and a plain path parameter
    /// must not be. Without this, a predicate that never matches would pass the surface guard vacuously.
    /// </summary>
    [Theory]
    [InlineData(nameof(SeamSamples.ResolverDelegate), "resolve")]
    [InlineData(nameof(SeamSamples.HomePath), "homeDirectory")]
    [InlineData(nameof(SeamSamples.EnvironmentParameter), "environment")]
    [InlineData(nameof(SeamSamples.ResolverNamed), "lookup")]
    public void HomeOrEnvironmentPredicateRejectsEverySeamShape(string methodName, string parameterName)
    {
        ParameterInfo parameter = typeof(SeamSamples)
            .GetMethod(methodName)!
            .GetParameters()
            .Single(p => p.Name == parameterName);

        Assert.True(IsHomeOrEnvironmentInput(parameter));
    }

    [Fact]
    public void HomeOrEnvironmentPredicateAcceptsAPlainSourcePath()
    {
        ParameterInfo parameter = typeof(SeamSamples)
            .GetMethod(nameof(SeamSamples.PlainPath))!
            .GetParameters()
            .Single();

        Assert.False(IsHomeOrEnvironmentInput(parameter));
    }

    private static bool IsHomeOrEnvironmentInput(ParameterInfo parameter)
    {
        string name = parameter.Name ?? string.Empty;
        string typeName = parameter.ParameterType.Name;
        bool namedSeam = Regex.IsMatch(
            name + " " + typeName,
            "home|environment|resolver|resolve|lookup|getenv|env(?!elope)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        bool resolverDelegate = parameter.ParameterType.IsGenericType
            && parameter.ParameterType.GetGenericTypeDefinition().FullName?.StartsWith("System.Func`", StringComparison.Ordinal) == true
            && parameter.ParameterType.GetGenericArguments().Contains(typeof(string));
        return namedSeam || resolverDelegate;
    }

    private static class SeamSamples
    {
        public static void ResolverDelegate(Func<string, string?> resolve) { }
        public static void HomePath(string homeDirectory) { }
        public static void EnvironmentParameter(object environment) { }
        public static void ResolverNamed(string lookup) { }
        public static void PlainPath(string sourcePath) { }
    }


    private static void AssertCarrierShipsDefault(ZipArchive archive, string entryName)
    {
        ZipArchiveEntry? entry = archive.GetEntry(entryName);
        Assert.True(entry is not null, $"The archive does not carry the carrier '{entryName}'.");

        string text = ReadEntryText(entry);
        MatchCollection matches = BlockPattern.Matches(text);
        Assert.True(
            matches.Count == 1,
            $"'{entryName}' must carry exactly one <response-preferences> block in the archive; it carries {matches.Count}.");

        string body = matches[0].Groups["body"].Value;
        Assert.True(
            string.Equals(DefaultText, body, StringComparison.Ordinal),
            $"The <response-preferences> block of '{entryName}' in the archive does not equal the shipped default byte-for-byte.");
    }

    private static void AssertArchivesIdentical(string first, string second)
    {
        byte[] firstBytes = File.ReadAllBytes(first);
        byte[] secondBytes = File.ReadAllBytes(second);
        Assert.True(
            firstBytes.AsSpan().SequenceEqual(secondBytes),
            $"'{Path.GetFileName(first)}' is not byte-identical across two packs from the same inputs.");

        using ZipArchive firstArchive = ZipFile.OpenRead(first);
        using ZipArchive secondArchive = ZipFile.OpenRead(second);
        Assert.Equal(
            firstArchive.Entries.Select(entry => entry.FullName),
            secondArchive.Entries.Select(entry => entry.FullName));

        foreach ((ZipArchiveEntry a, ZipArchiveEntry b) in firstArchive.Entries.Zip(secondArchive.Entries))
        {
            Assert.True(
                a.LastWriteTime == b.LastWriteTime,
                $"'{a.FullName}' has a different timestamp across two packs.");
            Assert.Equal(1980, a.LastWriteTime.Year);
        }
    }

    private static string ReadEntryText(ZipArchiveEntry entry)
    {
        using StreamReader reader = new(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
