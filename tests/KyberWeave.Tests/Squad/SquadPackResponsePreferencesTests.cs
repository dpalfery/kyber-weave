using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using KyberWeave.Core.Processes;
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
    /// Guards the isolation rule as an output guarantee, not a seam test: the packer takes no home or environment
    /// input, so the packed <c>&lt;response-preferences&gt;</c> block must equal the shipped default byte-for-byte
    /// for both carriers. No temp home is built here because the packer cannot be pointed at one; the test
    /// instead asserts that the operator marker text, which a home-reading packer would have to copy in, appears
    /// in no archive entry. The marker is a constant, so this check guards the default content, not a real home:
    /// <see cref="Pack_ChildProcessWithASentinelHomeShipsTheDefaultAndNoSentinel"/> covers a real home.
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
    /// Guards the isolation rule against a real home without touching this process's environment:
    /// a child <c>squad pack</c> runs with its own <c>HOME</c>/<c>USERPROFILE</c> pointed at a temp
    /// directory holding a sentinel guidance file, and its archives must still ship the default
    /// with the sentinel in no entry. A packer that read the ambient home would copy the sentinel
    /// in. The sentinel home is visible only to the child, so parallel tests can never observe it.
    /// </summary>
    [Fact]
    public void Pack_ChildProcessWithASentinelHomeShipsTheDefaultAndNoSentinel()
    {
        const string sentinel = "operator pack-isolation real-home sentinel: must never reach an archive";
        const string version = "rp-realhome";
        string fakeHome = Path.Combine(_temp.Path, "fake-home");
        string guidanceDir = Path.Combine(fakeHome, ".kyber-weave");
        Directory.CreateDirectory(guidanceDir);
        File.WriteAllText(
            Path.Combine(guidanceDir, "response-guidance.md"),
            "<response-preferences>" + sentinel + "</response-preferences>\n",
            Encoding.UTF8);

        string outDir = Path.Combine(_temp.Path, "dist");
        ProcessStartInfo startInfo = new(CliHostPath())
        {
            // squad pack is maintainer-only and resolves its source from the repository root.
            WorkingDirectory = KyberWeaveTestPaths.ToolRoot,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("squad");
        startInfo.ArgumentList.Add("pack");
        startInfo.ArgumentList.Add("--format");
        startInfo.ArgumentList.Add("all");
        startInfo.ArgumentList.Add("--out");
        startInfo.ArgumentList.Add(outDir);
        startInfo.ArgumentList.Add("--version");
        startInfo.ArgumentList.Add(version);
        startInfo.Environment["HOME"] = fakeHome;
        startInfo.Environment["USERPROFILE"] = fakeHome;

        ProcessResult result = ProcessRunner.Run(startInfo, string.Empty, TimeSpan.FromSeconds(120));
        Assert.True(
            result.ExitCode == 0,
            $"squad pack failed with exit {result.ExitCode}: {result.StandardOutput}\n{result.StandardError}");

        string apmPath = Path.Combine(outDir, $"kyber-squad-{version}.zip");
        string pluginsPath = Path.Combine(outDir, $"kyber-squad-plugin-{version}.zip");

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
                    ReadEntryText(entry).Contains(sentinel, StringComparison.Ordinal),
                    $"{Path.GetFileName(archivePath)} entry '{entry.FullName}' contains the real-home sentinel text.");
            }
        }
    }

    /// <summary>
    /// Guards the isolation proof itself: a packer that accepted an environment or home resolver would make the
    /// no-leak check circular, because a test could only show the seam was consulted, never that it was ignored.
    /// Every public member of <see cref="SquadPacker"/> is scanned, so a new entry point cannot reintroduce a seam.
    /// The parameter scan alone cannot catch ambient reads or neutrally named seams, so
    /// <see cref="Packer_SourceContainsNoAmbientHomeOrEnvironmentReads"/> and
    /// <see cref="Pack_ChildProcessWithASentinelHomeShipsTheDefaultAndNoSentinel"/> cover those paths.
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
    /// A review tripwire, not a proof: no source in the packaging directory may reference the
    /// home-resolution APIs a neutrally named seam would use. A denylist cannot prove the absence
    /// of ambient reads (indirection through a helper or computed strings evades it), so the proof
    /// is behavioural — <see cref="Pack_ChildProcessWithASentinelHomeShipsTheDefaultAndNoSentinel"/>
    /// catches any ambient read regardless of mechanism. This scan exists so a new reference is
    /// reviewed on purpose rather than slipped in.
    /// </summary>
    [Fact]
    public void Packer_SourceContainsNoAmbientHomeOrEnvironmentReads()
    {
        string packagingDir = Path.Combine(
            KyberWeaveTestPaths.ToolRoot, "src", "KyberWeave.Core", "Squad", "Packaging");
        Assert.True(Directory.Exists(packagingDir), $"Expected packaging source at '{packagingDir}'.");

        string[] sources = Directory.GetFiles(packagingDir, "*.cs", SearchOption.AllDirectories);
        Assert.NotEmpty(sources);

        string[] forbidden =
        [
            "GetFolderPath",
            "SpecialFolder",
            "UserProfile",
            "USERPROFILE",
            "\"HOME\"",
            "GetEnvironmentVariable",
            "Environment",
            "ReadEffective",
            "IConfiguration",
            "environmentReader"
        ];

        bool sawAllowedDefaultSeam = false;
        foreach (string sourcePath in sources)
        {
            string source = File.ReadAllText(sourcePath, Encoding.UTF8);
            if (source.Contains("SquadResponseGuidanceSource.DefaultText", StringComparison.Ordinal))
                sawAllowedDefaultSeam = true;

            foreach (string token in forbidden)
            {
                Assert.False(
                    source.Contains(token, StringComparison.Ordinal),
                    $"{Path.GetFileName(sourcePath)} references ambient home/environment API '{token}'; the packer must be a function of the canonical source alone.");
            }
        }

        // The one allowed guidance seam is the shipped default text: the packer fills carriers
        // from the canonical source alone. The home-reading entry point must never appear here.
        Assert.True(
            sawAllowedDefaultSeam,
            "SquadPacker.cs must fill carriers from SquadResponseGuidanceSource.DefaultText.");
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
    [InlineData(nameof(SeamSamples.UserProfilePath), "userProfile")]
    [InlineData(nameof(SeamSamples.ProfilePath), "profileDir")]
    [InlineData(nameof(SeamSamples.ConfigParameter), "config")]
    [InlineData(nameof(SeamSamples.ConfigurationType), "value")]
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

    /// <summary>
    /// Bounds the predicate's overmatching where it matters: the predicate only ever runs over
    /// <see cref="SquadPacker"/>'s own surface, so every real packer parameter must be accepted.
    /// If a broadened token ever trips the guarded API itself, this fails and the predicate —
    /// never the API — is loosened. Near-miss names outside this surface (<c>envelope</c>) are
    /// covered by <see cref="HomeOrEnvironmentPredicateAcceptsInnocuousNearMissNames"/>.
    /// </summary>
    [Fact]
    public void HomeOrEnvironmentPredicateAcceptsEveryRealPackerParameter()
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
                    $"The predicate overmatches the guarded surface: SquadPacker.{method.Name} parameter " +
                    $"'{parameter.Name}' must be accepted; loosen the predicate, not the API.");
            }
        }
    }

    /// <summary>
    /// Pins the predicate's near-miss guards: <c>envelope</c> contains the substring <c>env</c> but
    /// is not an environment input, so it must be accepted. Without this, a substring that never
    /// fails to match would pass the surface guard vacuously in the other direction.
    /// </summary>
    [Fact]
    public void HomeOrEnvironmentPredicateAcceptsInnocuousNearMissNames()
    {
        ParameterInfo parameter = typeof(SeamSamples)
            .GetMethod(nameof(SeamSamples.Enveloped))!
            .GetParameters()
            .Single();

        Assert.False(IsHomeOrEnvironmentInput(parameter));
    }

    /// <summary>
    /// The packer's closed-world seam check: the packer is a pure function of its source, output,
    /// and version arguments, so any home-, environment-, resolver-, config-, setting-, option-,
    /// or profile-flavoured parameter or delegate counts as a seam. The substrings deliberately
    /// overmatch outside this surface (a build <c>profile</c> is not a home input); that
    /// conservatism is safe only because this predicate never runs outside
    /// <see cref="SquadPacker"/>'s own surface — see
    /// <see cref="HomeOrEnvironmentPredicateAcceptsEveryRealPackerParameter"/> — while neutrally
    /// named ambient reads are caught behaviourally by
    /// <see cref="Pack_ChildProcessWithASentinelHomeShipsTheDefaultAndNoSentinel"/>.
    /// </summary>
    private static bool IsHomeOrEnvironmentInput(ParameterInfo parameter)
    {
        string name = parameter.Name ?? string.Empty;
        string typeName = parameter.ParameterType.Name;
        bool namedSeam = Regex.IsMatch(
            name + " " + typeName,
            "home|environment|resolver|resolve|lookup|getenv|userprofile|profile|config|setting|option|env(?!elope)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        bool configLikeType = Regex.IsMatch(
            typeName,
            "configuration|environment|settings|options|profile",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        bool resolverDelegate = parameter.ParameterType.IsGenericType
            && parameter.ParameterType.GetGenericTypeDefinition().FullName?.StartsWith("System.Func`", StringComparison.Ordinal) == true
            && parameter.ParameterType.GetGenericArguments().Contains(typeof(string));
        return namedSeam || configLikeType || resolverDelegate;
    }

    private static class SeamSamples
    {
        public static void ResolverDelegate(Func<string, string?> resolve) { }
        public static void HomePath(string homeDirectory) { }
        public static void EnvironmentParameter(object environment) { }
        public static void ResolverNamed(string lookup) { }
        public static void UserProfilePath(string userProfile) { }
        public static void ProfilePath(string profileDir) { }
        public static void ConfigParameter(string config) { }
        public static void ConfigurationType(FakeConfiguration value) { }
        public static void PlainPath(string sourcePath) { }
        public static void Enveloped(string envelope) { }

        [SuppressMessage(
            "Performance",
            "CA1812:Avoid uninstantiated internal classes",
            Justification = "The type exists only as a parameter-type probe for the surface-guard predicate; it is referenced via reflection, never constructed.")]
        public sealed class FakeConfiguration
        {
        }
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

    /// <summary>The CLI host this suite drives, copied beside the tests by the project reference.</summary>
    private static string CliHostPath()
    {
        string hostName = OperatingSystem.IsWindows() ? "kyber-weave.exe" : "kyber-weave";
        string host = Path.Combine(AppContext.BaseDirectory, hostName);
        Assert.True(
            File.Exists(host),
            $"The kyber-weave host '{host}' is missing from the test output directory. The test project " +
            "references the CLI project, so its absence is a build fault rather than the behaviour under test.");
        return host;
    }
}
