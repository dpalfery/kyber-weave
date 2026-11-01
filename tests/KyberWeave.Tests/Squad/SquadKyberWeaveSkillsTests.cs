using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Packaging;
using KyberWeave.Core.Squad.Parsing;
using KyberWeave.Core.Squad.Release;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Core.Squad.Validation;
using KyberWeave.Tests.Fakes;
using Xunit;
using Xunit.Sdk;

namespace KyberWeave.Tests.Squad;

/// <summary>
/// Packs the real repository once for a test class: the APM and Agent Plugins archives that
/// <c>squad pack</c> writes from <c>products/kyber-squad</c> plus <c>.apm/skills</c>. The archives are
/// read-only after construction, so every case in the class shares them and the repository is never written.
/// </summary>
public sealed class SquadKyberWeavePackFixture : IDisposable
{
    /// <summary>The release version every pack in this fixture is stamped with.</summary>
    public const string PackedVersion = "1.2.3";

    private readonly TempDirectory _temp = new();

    public SquadKyberWeavePackFixture()
    {
        ApmPack = Pack("apm");
        PluginsPack = Pack("plugins");
    }

    /// <summary>The result of <c>squad pack --format apm</c> run from the repository root.</summary>
    public SquadPackRun ApmPack { get; }

    /// <summary>The result of <c>squad pack --format plugins</c> run from the repository root.</summary>
    public SquadPackRun PluginsPack { get; }

    public void Dispose()
    {
        _temp.Dispose();
    }

    private SquadPackRun Pack(string format)
    {
        string outDirectory = Path.Combine(_temp.Path, format);
        CapturedConsoleExecution<int> run = ProcessConsoleCapture.Run(() =>
            new SquadPackCommand(workingDirectory: KyberWeaveTestPaths.ToolRoot).Execute(
                null!,
                new SquadPackSettings { Format = format, Out = outDirectory, Version = PackedVersion }));
        return new SquadPackRun(run.Result, run.Output, outDirectory);
    }
}

/// <summary>One pack run: its exit code, its console output, and the directory it wrote to.</summary>
public sealed record SquadPackRun(int ExitCode, string Output, string OutDirectory);

/// <summary>
/// The six Kyber-Weave skills authored in <c>.apm/skills</c> must ship with Kyber-Squad, so they deploy at the same
/// level as the Squad skills. Cases (a) to (g) fail on the base because the six are absent from the archives, the
/// loaded source, the render targets, the user-level install, and the checkout release source. Case (h) is a
/// declared characterization that passes by design.
/// </summary>
/// <remarks>
/// Contract: <c>docs/plans/2026-10-31-kyber-skills-user-level-deploy.md</c>, "Test contract (test-first)". The six names
/// are pinned here, and every <c>.apm/skills/*/SKILL.md</c> is discovered at test time so a later skill is covered
/// too. Real-repository cases resolve the root through <see cref="KyberWeaveTestPaths.ToolRoot"/>. Every temp directory
/// is disposed, and install, update, and uninstall run against a temp home.
/// </remarks>
public sealed class SquadKyberWeaveSkillsTests : IClassFixture<SquadKyberWeavePackFixture>, IDisposable
{
    /// <summary>The Squad canonical roster: one <c>SKILL.md</c> per directory under <c>products/kyber-squad/skills</c>.</summary>
    private const int SquadCanonicalSkillCount = 28;

    private const string PackedVersion = SquadKyberWeavePackFixture.PackedVersion;

    /// <summary>The Squad skill whose render path each target's Kyber-Weave skill path is derived from.</summary>
    private const string AnchorSquadSkillFile = "task-gate/SKILL.md";

    private const string ParitySkillName = "kyber-weave-parity-fixture";

    private static readonly string[] KyberWeaveSkillNames =
    [
        "kyber-docs-authoring",
        "kyber-docs-integrity-review",
        "kyber-docs-onboarding-and-assessment",
        "kyber-weave-cli",
        "kyber-weave-model-profile",
        "spec-conductor"
    ];

    private readonly SquadKyberWeavePackFixture _pack;
    private readonly TempDirectory _temp = new();

    public SquadKyberWeaveSkillsTests(SquadKyberWeavePackFixture pack)
    {
        _pack = pack;
    }

    public void Dispose()
    {
        _temp.Dispose();
    }

    /// <summary>
    /// The reported bug: a user-level install carried the Squad skills but not the six Kyber-Weave skills, because pack
    /// archived only <c>products/kyber-squad</c>. The APM archive must carry every skill tree, with the same bytes.
    /// </summary>
    [Fact]
    public void ApmArchiveCarriesEveryKyberWeaveSkillTreeBesideTheSquadSkills()
    {
        AssertExit(_pack.ApmPack.ExitCode, _pack.ApmPack.Output, 0);
        using ZipArchive archive = ZipFile.OpenRead(ArchivePath(_pack.ApmPack, $"kyber-squad-{PackedVersion}.zip"));

        AssertArchiveCarriesSkillTrees(archive);
    }

    /// <summary>
    /// The Agent Plugins archive takes the same skill trees. It must still carry no agents, so the companion skills do
    /// not drag agent entries in with them.
    /// </summary>
    [Fact]
    public void PluginsArchiveCarriesEveryKyberWeaveSkillTreeAndNoAgents()
    {
        AssertExit(_pack.PluginsPack.ExitCode, _pack.PluginsPack.Output, 0);
        using ZipArchive archive =
            ZipFile.OpenRead(ArchivePath(_pack.PluginsPack, $"kyber-squad-plugin-{PackedVersion}.zip"));

        AssertArchiveCarriesSkillTrees(archive);
        Assert.DoesNotContain(
            archive.Entries,
            entry => entry.FullName.StartsWith("agents/", StringComparison.Ordinal));
    }

    /// <summary>
    /// The install-time loader must accept the six skills (frontmatter allow-list, resource closure, no duplicates,
    /// reserved prefixes) and list them in the source every renderer reads.
    /// </summary>
    [Fact]
    public void ApmArchiveLoadsWithTheSquadLoaderAndListsEveryKyberWeaveSkill()
    {
        AssertExit(_pack.ApmPack.ExitCode, _pack.ApmPack.Output, 0);
        string extracted = ExtractApmArchive();

        SquadSource source = SquadSourceLoader.Load(extracted);

        string[] loaded = source.Skills.Select(skill => skill.Name).ToArray();
        string[] expected = AllKyberWeaveSkillNames();
        foreach (string name in expected)
        {
            Assert.Contains(name, loaded);
        }

        Assert.Equal(SquadCanonicalSkillCount + expected.Length, loaded.Length);
    }

    /// <summary>
    /// Every one of the 13 harness targets must render each skill at that target's own skill path: the path the same
    /// target uses for a Squad skill, with the name swapped. Claude's path is exactly <c>skills/&lt;name&gt;/SKILL.md</c>
    /// under its global root.
    /// </summary>
    [Fact]
    public async Task EveryRenderTargetWritesEachKyberWeaveSkillAtItsOwnSkillPath()
    {
        AssertExit(_pack.ApmPack.ExitCode, _pack.ApmPack.Output, 0);
        string extracted = ExtractApmArchive();
        SquadSource source = SquadSourceLoader.Load(extracted);
        using SquadHomeHarness harness = new(Path.Combine(_temp.Path, "home"));
        ISquadRenderer renderer = SquadCommandComposition.ResolveRenderer();
        string[] kyberWeaveSkills = AllKyberWeaveSkillNames();

        foreach (SquadTarget target in Enum.GetValues<SquadTarget>())
        {
            SquadRenderRequest request = new(
                extracted,
                [target],
                SquadDeploymentScope.Global,
                GlobalRoots: harness.GlobalRoots,
                PreloadedSource: source);
            SquadRenderResult render = await renderer.RenderAsync(request);
            Assert.True(
                render.Success && render.Errors.Count == 0,
                $"{target} did not render the loaded source cleanly: {string.Join("; ", render.Errors)}");

            HashSet<string> rendered = render.Files
                .Select(file => NormalizePath(file.RelativePath))
                .ToHashSet(StringComparer.Ordinal);
            string? anchor = rendered.FirstOrDefault(path => IsPathEnding(path, AnchorSquadSkillFile));
            Assert.True(
                anchor is not null,
                $"{target} renders no principal file for the Squad skill 'task-gate', so its skill path is unknown.");
            string skillsPrefix = anchor[..^AnchorSquadSkillFile.Length];

            foreach (string name in kyberWeaveSkills)
            {
                string expected = target == SquadTarget.Claude
                    ? $"skills/{name}/SKILL.md"
                    : $"{skillsPrefix}{name}/SKILL.md";
                Assert.True(
                    rendered.Contains(expected),
                    $"{target} renders no '{expected}' for the Kyber-Weave skill '{name}'.");
            }
        }
    }

    /// <summary>
    /// The user-level path the bug report is about. A global Claude install writes the six skills beside
    /// <c>task-gate</c>, the global receipt tracks them, a same-version update changes nothing, and uninstall removes
    /// them with the Squad skills. The release source is the APM archive from pack, extracted as a release would be.
    /// </summary>
    [Fact]
    public void GlobalInstallDeploysKyberWeaveSkillsBesideSquadSkillsAndUninstallRemovesThem()
    {
        AssertExit(_pack.ApmPack.ExitCode, _pack.ApmPack.Output, 0);
        string archive = ArchivePath(_pack.ApmPack, $"kyber-squad-{PackedVersion}.zip");
        using SquadHomeHarness harness = new(Path.Combine(_temp.Path, "home"));
        ISquadRenderer renderer = SquadCommandComposition.ResolveRenderer();
        using ISquadReleaseSource releaseSource = new ArchiveSquadReleaseSource(archive);
        string anchor = harness.MakeDirectory("anchor");
        string claudeRoot = harness.GlobalRoot(SquadTarget.Claude);
        string receiptPath = Path.Combine(harness.Home, ".kyber-weave", "squad", "global", "squad.receipt.json");
        string claudeToken = SquadTargetCatalog.GetToken(SquadTarget.Claude);
        string[] kyberWeaveSkills = AllKyberWeaveSkillNames();

        CapturedConsoleExecution<int> install = ProcessConsoleCapture.Run(() => new SquadInstallCommand(
                harness.UserPaths,
                harness.Store,
                releaseSource,
                renderer,
                false,
                globalRoots: harness.GlobalRoots)
            .Execute(null!, new SquadInstallSettings
            {
                Path = anchor,
                Global = true,
                Targets = ["claude"],
                Version = PackedVersion,
                Yes = true,
                NoCapturePlugin = true
            }));
        AssertExit(install.Result, install.Output, 0);

        Assert.True(
            File.Exists(Path.Combine(claudeRoot, "skills", "task-gate", "SKILL.md")),
            "squad install --global did not deploy the Squad skill task-gate beside the Kyber-Weave skills.");
        foreach (string name in kyberWeaveSkills)
        {
            string deployed = Path.Combine(claudeRoot, "skills", name, "SKILL.md");
            Assert.True(File.Exists(deployed), $"squad install --global left no '{deployed}'.");
        }

        Assert.True(File.Exists(receiptPath), $"The global receipt is missing at '{receiptPath}'.");
        SquadReceipt installed = harness.Store.DeserializeReceipt(File.ReadAllText(receiptPath, Encoding.UTF8));
        string[] owned = installed.Files
            .Where(file => file.Target == claudeToken)
            .Select(file => NormalizePath(file.RelativePath))
            .ToArray();
        foreach (string name in kyberWeaveSkills)
        {
            Assert.Contains($"skills/{name}/SKILL.md", owned);
        }

        string[] beforeUpdate = OwnedFingerprint(installed);
        CapturedConsoleExecution<int> update = ProcessConsoleCapture.Run(() => new SquadUpdateCommand(
                harness.UserPaths,
                harness.Store,
                releaseSource,
                renderer,
                false,
                static _ => true,
                harness.GlobalRoots)
            .Execute(null!, new SquadUpdateSettings
            {
                Path = anchor,
                Global = true,
                Version = PackedVersion,
                Yes = true,
                NoCapturePlugin = true
            }));
        AssertExit(update.Result, update.Output, 0);
        SquadReceipt updated = harness.Store.DeserializeReceipt(File.ReadAllText(receiptPath, Encoding.UTF8));
        Assert.Equal(beforeUpdate, OwnedFingerprint(updated));

        CapturedConsoleExecution<int> uninstall = ProcessConsoleCapture.Run(() => new SquadUninstallCommand(
                harness.UserPaths,
                harness.Store,
                harness.Lifecycle(renderer),
                false,
                globalRoots: harness.GlobalRoots)
            .Execute(null!, new SquadUninstallSettings { Path = anchor, Global = true, Yes = true }));
        AssertExit(uninstall.Result, uninstall.Output, 0);

        string[] removedNames = [.. kyberWeaveSkills, "task-gate"];
        foreach (string name in removedNames)
        {
            string removed = Path.Combine(claudeRoot, "skills", name, "SKILL.md");
            Assert.False(File.Exists(removed), $"squad uninstall --global left '{removed}' behind.");
        }
    }

    /// <summary>
    /// Fail closed, as the spec requires: an <c>.apm/skills</c> name equal to a Squad skill or to a Squad agent must stop
    /// pack before any archive is written, so neither side silently shadows the other. On the base, pack exits 0.
    /// </summary>
    [Theory]
    [InlineData("task-gate", "products/kyber-squad/skills/task-gate")]
    [InlineData("architect", "products/kyber-squad/agents/architect.md")]
    public void PackFailsClosedWhenAnApmSkillNameClashesWithASquadName(string clashingName, string squadSource)
    {
        string repository = CreateRepositoryWithCompanionSkill(Path.Combine(_temp.Path, "repo"), clashingName);
        string outDirectory = Path.Combine(_temp.Path, "clash-out");

        CapturedConsoleExecution<int> run = ProcessConsoleCapture.Run(() =>
            new SquadPackCommand(workingDirectory: repository).Execute(
                null!,
                new SquadPackSettings { Format = "all", Out = outDirectory, Version = PackedVersion }));

        AssertExit(run.Result, run.Output, 1);
        Assert.Empty(ArchivesIn(outDirectory));
        string output = Compact(run.Output);
        Assert.Contains(Compact(clashingName), output, StringComparison.Ordinal);
        Assert.Contains(Compact(squadSource), output, StringComparison.Ordinal);
        Assert.Contains(Compact($".apm/skills/{clashingName}"), output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The checkout release source feeds a CLI run from a checkout, so it must carry the companion skills too and list
    /// them in its extracted files, as the archive path does.
    /// </summary>
    [Fact]
    public async Task CheckoutReleaseSourceExtractsKyberWeaveSkillsBesideTheSquadSkills()
    {
        string repository = CreateRepositoryWithCompanionSkill(Path.Combine(_temp.Path, "repo"), ParitySkillName);
        string destination = Path.Combine(_temp.Path, "checkout-release");
        using LocalCheckoutSquadReleaseSource source =
            new(Path.Combine(repository, "products", "kyber-squad"));

        SquadReleaseResult result = await source.DownloadAndExtractAsync(
            new SquadReleaseRequest("local/kyber-squad", PackedVersion, destination));

        Assert.Contains($"skills/{ParitySkillName}/SKILL.md", result.ExtractedFiles);
        Assert.True(
            File.Exists(Path.Combine(destination, "skills", ParitySkillName, "SKILL.md")),
            $"The checkout extraction left no skills/{ParitySkillName}/SKILL.md in '{destination}'.");
    }

    /// <summary>
    /// A checkout fails closed on a companion name that equals a Squad skill, with the same diagnostic pack gives. The
    /// release source result has no failure state, so failure is an exception, and nothing is reported as extracted.
    /// </summary>
    [Fact]
    public async Task CheckoutReleaseSourceFailsClosedWhenAKyberWeaveSkillNameClashesWithASquadName()
    {
        string repository = CreateRepositoryWithCompanionSkill(Path.Combine(_temp.Path, "repo"), "task-gate");
        string destination = Path.Combine(_temp.Path, "checkout-clash");
        using LocalCheckoutSquadReleaseSource source =
            new(Path.Combine(repository, "products", "kyber-squad"));
        SquadReleaseRequest request = new("local/kyber-squad", PackedVersion, destination);

        Exception failure = await Assert.ThrowsAnyAsync<Exception>(() => source.DownloadAndExtractAsync(request));

        string message = Compact(failure.Message);
        Assert.Contains(Compact("task-gate"), message, StringComparison.Ordinal);
        Assert.Contains(Compact("products/kyber-squad/skills/task-gate"), message, StringComparison.Ordinal);
        Assert.Contains(Compact(".apm/skills/task-gate"), message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(destination), $"A refused checkout left '{destination}' behind.");
    }

    /// <summary>
    /// Source validation covers the Squad tree itself, not only the staged copy. A Squad skill that is a symbolic link out
    /// of the tree is copied into the staged tree as ordinary bytes, so the composed-tree check alone accepts it and the
    /// outside file ships. Origin/main refused it when it loaded the source; the head exits 0. The outside target is a
    /// valid copy of the real skill with a marker line, so the link is the only defect.
    /// </summary>
    [Theory]
    [InlineData("apm")]
    [InlineData("all")]
    public void PackRefusesASquadSkillSymbolicLinkThatLeavesTheSourceTree(string format)
    {
        if (!CanCreateSymbolicLinks())
            throw SkipException.ForSkip("This host cannot create symbolic links.");

        string repository = CreateRepository(Path.Combine(_temp.Path, "repo"));
        string outside = Path.Combine(_temp.Path, "outside");
        Directory.CreateDirectory(outside);
        string outsideSkill = Path.Combine(outside, "SKILL.md");
        File.Copy(Path.Combine(ProductRoot, "skills", "task-gate", "SKILL.md"), outsideSkill);
        File.AppendAllText(outsideSkill, "OUTSIDE-TREE-MARKER\n", Encoding.UTF8);
        string skillFile = Path.Combine(repository, "products", "kyber-squad", "skills", "task-gate", "SKILL.md");
        File.Delete(skillFile);
        File.CreateSymbolicLink(skillFile, outsideSkill);
        string outDirectory = Path.Combine(_temp.Path, "source-link-out");

        SquadPackRun run = PackInto(repository, format, outDirectory);

        AssertExit(run.ExitCode, run.Output, 1);
        Assert.Empty(ArchivesIn(outDirectory));
        string output = Compact(run.Output);
        Assert.Contains(Compact("skills/task-gate/SKILL.md"), output, StringComparison.Ordinal);
        Assert.Contains(Compact("symbolic link"), output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Decision D5, fail closed: a companion skill that is a symbolic link is refused, not skipped. Skipping it would
    /// release a tree without a skill the operator authored, and nothing would say so. The directory variant links the
    /// whole skill; the file variant links only its SKILL.md. Both targets are valid skills outside the repository, so the
    /// link is the only defect.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PackFailsClosedWhenACompanionSkillIsASymbolicLink(bool linkWholeDirectory)
    {
        if (!CanCreateSymbolicLinks())
            throw SkipException.ForSkip("This host cannot create symbolic links.");

        string repository = CreateRepository(Path.Combine(_temp.Path, "repo"));
        string link = LinkCompanionSkill(repository, ParitySkillName, linkWholeDirectory);
        string outDirectory = Path.Combine(_temp.Path, "companion-link-out");

        SquadPackRun run = PackInto(repository, "all", outDirectory);

        AssertExit(run.ExitCode, run.Output, 1);
        Assert.Empty(ArchivesIn(outDirectory));
        Assert.Contains(Compact(link), Compact(run.Output), StringComparison.Ordinal);
    }

    /// <summary>
    /// Decision D5, fail closed, for the checkout release source: a symbolic-link companion throws before the destination is
    /// created, so a refused checkout leaves nothing behind and reports nothing as extracted.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CheckoutReleaseSourceFailsClosedWhenACompanionSkillIsASymbolicLink(bool linkWholeDirectory)
    {
        if (!CanCreateSymbolicLinks())
            throw SkipException.ForSkip("This host cannot create symbolic links.");

        string repository = CreateRepository(Path.Combine(_temp.Path, "repo"));
        string link = LinkCompanionSkill(repository, ParitySkillName, linkWholeDirectory);
        string destination = Path.Combine(_temp.Path, "checkout-link");
        using LocalCheckoutSquadReleaseSource source =
            new(Path.Combine(repository, "products", "kyber-squad"));
        SquadReleaseRequest request = new("local/kyber-squad", PackedVersion, destination);

        Exception failure = await Assert.ThrowsAnyAsync<Exception>(() => source.DownloadAndExtractAsync(request));

        Assert.Contains(Compact(link), Compact(failure.Message), StringComparison.Ordinal);
        Assert.False(Directory.Exists(destination), $"A refused checkout left '{destination}' behind.");
    }

    /// <summary>
    /// Regression guard, declared: passes on the base by design. A text companion ships with LF line endings whatever the
    /// source uses, so a CRLF checkout packs the same bytes as an LF one. The archive side is compared exactly. If the
    /// companion collection read sources with <c>File.ReadAllBytes</c>, the entries would keep their CR bytes and fail here.
    /// </summary>
    [Fact]
    public void CompanionTextShipsWithLfOnlyBytesWhenTheSourceUsesCrlf()
    {
        string repository = CreateRepository(Path.Combine(_temp.Path, "repo"));
        WriteCrlfCompanionSkill(repository, ParitySkillName);
        Assert.True(
            File.ReadAllBytes(Path.Combine(repository, ".apm", "skills", ParitySkillName, "SKILL.md"))
                .Contains((byte)'\r'),
            "The fixture holds no CR bytes, so this case would prove nothing about line endings.");
        SquadPackRun run = PackInto(repository, "apm", Path.Combine(_temp.Path, "crlf-out"));
        AssertExit(run.ExitCode, run.Output, 0);

        using ZipArchive archive = ZipFile.OpenRead(ArchivePath(run, $"kyber-squad-{PackedVersion}.zip"));
        AssertEntryHoldsLfText(archive, $"skills/{ParitySkillName}/SKILL.md", CompanionSkillLfText(ParitySkillName));
        AssertEntryHoldsLfText(archive, $"skills/{ParitySkillName}/references/notes.md", CompanionNotesLfText);
    }

    /// <summary>
    /// Regression guard, declared: passes on the base by design. Two packs of one source give byte-identical archives and
    /// SHA256SUMS.txt, so a release can be reproduced and checked. This guards entry order and timestamps; line endings are
    /// covered by the CRLF case above.
    /// </summary>
    [Fact]
    public void PackingTheSameCompanionSourceTwiceYieldsByteIdenticalArchivesAndChecksums()
    {
        string repository = CreateRepository(Path.Combine(_temp.Path, "repo"));
        WriteCrlfCompanionSkill(repository, ParitySkillName);
        SquadPackRun first = PackInto(repository, "all", Path.Combine(_temp.Path, "twice-first"));
        SquadPackRun second = PackInto(repository, "all", Path.Combine(_temp.Path, "twice-second"));
        AssertExit(first.ExitCode, first.Output, 0);
        AssertExit(second.ExitCode, second.Output, 0);

        foreach (string fileName in new[]
                 {
                     $"kyber-squad-{PackedVersion}.zip",
                     $"kyber-squad-plugin-{PackedVersion}.zip",
                     "SHA256SUMS.txt"
                 })
        {
            byte[] firstBytes = File.ReadAllBytes(ArchivePath(first, fileName));
            byte[] secondBytes = File.ReadAllBytes(ArchivePath(second, fileName));
            Assert.True(
                firstBytes.SequenceEqual(secondBytes),
                $"'{fileName}' differs between two packs of the same source.");
        }
    }

    /// <summary>
    /// A direct caller of <see cref="SquadPacker"/> that names a clashing companion keeps the archive it already had. The
    /// packer used to delete the target archive before it collected the entries, so a refused pack destroyed the last good
    /// release at that path.
    /// </summary>
    [Fact]
    public void DirectPackApmKeepsThePreviousArchiveWhenACompanionNameClashes()
    {
        string repository = CreateRepositoryWithCompanionSkill(Path.Combine(_temp.Path, "repo"), "task-gate");
        string outDirectory = Path.Combine(_temp.Path, "direct-out");
        Directory.CreateDirectory(outDirectory);
        string archivePath = Path.Combine(outDirectory, $"kyber-squad-{PackedVersion}.zip");
        byte[] previous = Encoding.UTF8.GetBytes("previous archive bytes");
        File.WriteAllBytes(archivePath, previous);

        SquadSourceValidationException failure = Assert.Throws<SquadSourceValidationException>(() =>
            SquadPacker.PackApm(
                Path.Combine(repository, "products", "kyber-squad"),
                outDirectory,
                PackedVersion,
                Path.Combine(repository, ".apm", "skills")));

        Assert.Contains("task-gate", failure.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(archivePath), $"The refused pack deleted the previous archive at '{archivePath}'.");
        Assert.True(
            previous.SequenceEqual(File.ReadAllBytes(archivePath)),
            $"The refused pack changed the previous archive at '{archivePath}'.");
    }

    /// <summary>
    /// Characterization, declared: passes on the base by design. It pins the single-source rule: a Kyber-Weave skill is
    /// authored once in <c>.apm/skills</c> and never copied into <c>products/kyber-squad</c>, so no copy can drift from
    /// the APM package. The products-only loader must not list one either.
    /// </summary>
    [Fact]
    public void KyberWeaveSkillsAreNeverDuplicatedIntoTheSquadSourceTree()
    {
        string[] loaded = TestSquadCorpus.CanonicalSource.Skills.Select(skill => skill.Name).ToArray();

        foreach (string name in AllKyberWeaveSkillNames())
        {
            Assert.False(
                Directory.Exists(Path.Combine(ProductRoot, "skills", name)),
                $"'products/kyber-squad/skills/{name}' exists. The skill is authored in .apm/skills and must not be copied into the Squad tree.");
            Assert.DoesNotContain(name, loaded);
        }
    }

    private static string ProductRoot => Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");

    private static string ApmSkillsRoot => Path.Combine(KyberWeaveTestPaths.ToolRoot, ".apm", "skills");

    /// <summary>The six pinned names, plus every <c>.apm/skills</c> directory that holds a <c>SKILL.md</c>, ordinally sorted.</summary>
    private static string[] AllKyberWeaveSkillNames()
    {
        string[] discovered = Directory.EnumerateDirectories(ApmSkillsRoot)
            .Where(directory => File.Exists(Path.Combine(directory, "SKILL.md")))
            .Select(directory => Path.GetRelativePath(ApmSkillsRoot, directory))
            .ToArray();
        return KyberWeaveSkillNames
            .Union(discovered, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static string[] SquadSkillNames()
    {
        string skillsRoot = Path.Combine(ProductRoot, "skills");
        return Directory.EnumerateDirectories(skillsRoot)
            .Where(directory => File.Exists(Path.Combine(directory, "SKILL.md")))
            .Select(directory => Path.GetRelativePath(skillsRoot, directory))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Asserts that an archive carries each Kyber-Weave skill tree at <c>skills/&lt;name&gt;/&lt;relative&gt;</c> with
    /// the same text, keeps the 28 Squad skills, and holds exactly one <c>SKILL.md</c> per skill.
    /// </summary>
    private static void AssertArchiveCarriesSkillTrees(ZipArchive archive)
    {
        string[] kyberWeaveSkills = AllKyberWeaveSkillNames();
        foreach (string name in kyberWeaveSkills)
        {
            string skillRoot = Path.Combine(ApmSkillsRoot, name);
            Assert.True(Directory.Exists(skillRoot), $"'.apm/skills/{name}' is missing from the repository.");
            foreach (string sourceFile in Directory.EnumerateFiles(skillRoot, "*", SearchOption.AllDirectories)
                         .Order(StringComparer.Ordinal))
            {
                string entryName = $"skills/{name}/{NormalizePath(Path.GetRelativePath(skillRoot, sourceFile))}";
                ZipArchiveEntry? entry = archive.GetEntry(entryName);
                Assert.True(
                    entry is not null,
                    $"The archive has no entry '{entryName}', so the Kyber-Weave skill '{name}' does not ship.");
                // The expected side is the source with its line endings folded to LF, as UTF-8 bytes. The archive side is
                // compared raw and never folded, so a change that stops folding companion text, or that copies source bytes
                // with File.ReadAllBytes, fails here. The corpus holds no CR today, so the CRLF case is what proves the fold.
                byte[] expected = Encoding.UTF8.GetBytes(LfNormalized(File.ReadAllText(sourceFile, Encoding.UTF8)));
                byte[] actual = ReadEntryBytes(entry);
                Assert.True(Array.IndexOf(actual, (byte)'\r') < 0, $"'{entryName}' carries a carriage return.");
                Assert.True(
                    expected.SequenceEqual(actual),
                    $"'{entryName}' does not hold the LF-normalized bytes of '{sourceFile}'.");
            }
        }

        string[] squadSkills = SquadSkillNames();
        Assert.Equal(SquadCanonicalSkillCount, squadSkills.Length);
        foreach (string squadSkill in squadSkills)
        {
            string entryName = $"skills/{squadSkill}/SKILL.md";
            Assert.True(
                archive.GetEntry(entryName) is not null,
                $"The archive has no entry '{entryName}'; a Squad skill went missing.");
        }

        Assert.Equal(SquadCanonicalSkillCount + kyberWeaveSkills.Length, archive.Entries.Count(IsSkillPrincipal));
    }

    private static bool IsSkillPrincipal(ZipArchiveEntry entry)
    {
        string[] parts = entry.FullName.Split('/');
        return parts is ["skills", _, "SKILL.md"];
    }

    /// <summary>The entry's stored bytes, exactly as the archive holds them.</summary>
    private static byte[] ReadEntryBytes(ZipArchiveEntry entry)
    {
        using Stream stream = entry.Open();
        using MemoryStream buffer = new();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>
    /// Asserts an archive entry holds exactly the UTF-8 bytes of <paramref name="lfText"/>, which has no CR, so a
    /// carriage return cannot slip in unnoticed.
    /// </summary>
    private static void AssertEntryHoldsLfText(ZipArchive archive, string entryName, string lfText)
    {
        ZipArchiveEntry? entry = archive.GetEntry(entryName);
        Assert.True(entry is not null, $"The archive has no entry '{entryName}'.");
        byte[] actual = ReadEntryBytes(entry);
        Assert.True(
            Array.IndexOf(actual, (byte)'\r') < 0,
            $"'{entryName}' carries a carriage return; companion text must ship with LF line endings.");
        Assert.True(
            Encoding.UTF8.GetBytes(lfText).SequenceEqual(actual),
            $"'{entryName}' does not hold the LF-only bytes of its source.");
    }

    private static string ArchivePath(SquadPackRun run, string fileName)
    {
        string path = Path.Combine(run.OutDirectory, fileName);
        Assert.True(File.Exists(path), $"Pack wrote no archive at '{path}'.{Environment.NewLine}{run.Output}");
        return path;
    }

    private static string[] ArchivesIn(string directory)
    {
        return Directory.Exists(directory)
            ? Directory.GetFiles(directory, "kyber-squad-*.zip")
            : Array.Empty<string>();
    }

    private string ExtractApmArchive()
    {
        string extracted = Path.Combine(_temp.Path, "apm-extracted");
        ZipFile.ExtractToDirectory(ArchivePath(_pack.ApmPack, $"kyber-squad-{PackedVersion}.zip"), extracted);
        return extracted;
    }

    /// <summary>
    /// A temp repository shaped like this one: a solution marker, a copy of the real <c>products/kyber-squad</c>, and one
    /// companion skill under <c>.apm/skills</c>. The real repository is never written.
    /// </summary>
    private static string CreateRepositoryWithCompanionSkill(string root, string skillName)
    {
        CreateRepository(root);

        string skillDirectory = Path.Combine(root, ".apm", "skills", skillName);
        Directory.CreateDirectory(skillDirectory);
        File.WriteAllText(
            Path.Combine(skillDirectory, "SKILL.md"),
            $"""
             ---
             name: {skillName}
             description: Fixture companion skill for the Kyber-Squad shipping contract.
             ---

             # {skillName}

             Fixture body.

             """);
        return root;
    }

    /// <summary>
    /// A temp repository shaped like this one: a solution marker and a copy of the real <c>products/kyber-squad</c>. The
    /// real repository is never written.
    /// </summary>
    private static string CreateRepository(string root)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(
            Path.Combine(root, "KyberWeave.sln"),
            "Microsoft Visual Studio Solution File, Format Version 12.00");
        CopyDirectory(ProductRoot, Path.Combine(root, "products", "kyber-squad"));
        return root;
    }

    /// <summary>Runs <c>squad pack</c> from <paramref name="repository"/> into <paramref name="outDirectory"/>.</summary>
    private static SquadPackRun PackInto(string repository, string format, string outDirectory)
    {
        CapturedConsoleExecution<int> run = ProcessConsoleCapture.Run(() =>
            new SquadPackCommand(workingDirectory: repository).Execute(
                null!,
                new SquadPackSettings { Format = format, Out = outDirectory, Version = PackedVersion }));
        return new SquadPackRun(run.Result, run.Output, outDirectory);
    }

    /// <summary>
    /// Replaces companion skill <paramref name="name"/> with a symbolic link to a valid skill outside the repository, and
    /// returns the link's repository-relative path. A directory link replaces the whole skill; a file link replaces only
    /// its SKILL.md, so the skill directory stays real.
    /// </summary>
    private string LinkCompanionSkill(string repository, string name, bool linkWholeDirectory)
    {
        string outsideSkill = Path.Combine(_temp.Path, "outside", name);
        Directory.CreateDirectory(outsideSkill);
        string outsideSkillFile = Path.Combine(outsideSkill, "SKILL.md");
        File.WriteAllText(
            outsideSkillFile,
            string.Join(
                '\n',
                "---",
                $"name: {name}",
                "description: Outside-tree fixture for the companion symbolic-link refusal.",
                "---",
                string.Empty,
                $"# {name}",
                string.Empty,
                "Fixture body.",
                string.Empty));

        string skillsRoot = Path.Combine(repository, ".apm", "skills");
        Directory.CreateDirectory(skillsRoot);
        if (linkWholeDirectory)
        {
            Directory.CreateSymbolicLink(Path.Combine(skillsRoot, name), outsideSkill);
            return $".apm/skills/{name}";
        }

        string skillDirectory = Path.Combine(skillsRoot, name);
        Directory.CreateDirectory(skillDirectory);
        File.CreateSymbolicLink(Path.Combine(skillDirectory, "SKILL.md"), outsideSkillFile);
        return $".apm/skills/{name}/SKILL.md";
    }

    /// <summary>
    /// Writes a companion skill whose SKILL.md and resource hold CRLF line endings, as a Windows checkout does. Both are
    /// folded to CRLF from LF text here, so the fixture does not depend on this file's own line endings.
    /// </summary>
    private static void WriteCrlfCompanionSkill(string repository, string skillName)
    {
        string skillDirectory = Path.Combine(repository, ".apm", "skills", skillName);
        Directory.CreateDirectory(Path.Combine(skillDirectory, "references"));
        File.WriteAllText(
            Path.Combine(skillDirectory, "SKILL.md"),
            CompanionSkillLfText(skillName).Replace("\n", "\r\n", StringComparison.Ordinal));
        File.WriteAllText(
            Path.Combine(skillDirectory, "references", "notes.md"),
            CompanionNotesLfText.Replace("\n", "\r\n", StringComparison.Ordinal));
    }

    /// <summary>The LF text of a companion SKILL.md. Built from lines, so expected bytes never depend on source line endings.</summary>
    private static string CompanionSkillLfText(string skillName)
    {
        return string.Join(
            '\n',
            "---",
            $"name: {skillName}",
            "description: Fixture companion skill for the line-ending and determinism cases.",
            "---",
            string.Empty,
            $"# {skillName}",
            string.Empty,
            "Read [notes](references/notes.md) before acting.",
            string.Empty);
    }

    private const string CompanionNotesLfText = "# Notes\n\nFirst line.\nSecond line.\n";

    /// <summary>Whether this host lets the test process create a symbolic link, probed in the temporary root.</summary>
    private bool CanCreateSymbolicLinks()
    {
        string probe = Path.Combine(_temp.Path, "symlink-probe");
        try
        {
            File.CreateSymbolicLink(probe, Path.Combine(_temp.Path, "symlink-target"));
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (string sourceFile in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string destinationFile = Path.Combine(destination, Path.GetRelativePath(source, sourceFile));
            string? destinationDirectory = Path.GetDirectoryName(destinationFile);
            if (destinationDirectory is not null)
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            File.Copy(sourceFile, destinationFile, true);
        }
    }

    private static string[] OwnedFingerprint(SquadReceipt receipt)
    {
        return receipt.Files
            .Select(file => $"{file.Target}|{NormalizePath(file.RelativePath)}|{file.Sha256}")
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static void AssertExit(int exitCode, string output, int expected)
    {
        Assert.True(exitCode == expected, $"Expected exit {expected} but got {exitCode}:{Environment.NewLine}{output}");
    }

    private static bool IsPathEnding(string path, string suffix)
    {
        return path == suffix || path.EndsWith("/" + suffix, StringComparison.Ordinal);
    }

    private static string NormalizePath(string path)
    {
        return path.Replace('\\', '/');
    }

    /// <summary>Folds CRLF and lone CR to LF, the line endings the packer writes for text companions.</summary>
    private static string LfNormalized(string text)
    {
        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }

    /// <summary>Folds separators to '/' and drops whitespace, so a console line wrap cannot split an expected path.</summary>
    private static string Compact(string text)
    {
        return new string(NormalizePath(text).Where(static character => !char.IsWhiteSpace(character)).ToArray());
    }

    /// <summary>
    /// Stands in for a downloaded release: extracts a pack archive into the requested destination, as the lifecycle
    /// receives a real release. The archive is the fixture's own pack output, never a copy of the working tree.
    /// </summary>
    private sealed class ArchiveSquadReleaseSource(string archivePath) : ISquadReleaseSource
    {
        public async Task<SquadReleaseResult> DownloadAndExtractAsync(
            SquadReleaseRequest request,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            await ZipFile.ExtractToDirectoryAsync(
                archivePath,
                request.DestinationPath,
                true,
                cancellationToken);
            List<string> extracted = Directory.EnumerateFiles(request.DestinationPath, "*", SearchOption.AllDirectories)
                .Select(path => NormalizePath(Path.GetRelativePath(request.DestinationPath, path)))
                .Order(StringComparer.Ordinal)
                .ToList();

            byte[] archiveBytes = await File.ReadAllBytesAsync(archivePath, cancellationToken);
            string assetName = $"kyber-squad-{request.Version}.zip";
            return new SquadReleaseResult(
                request.Version,
                new SquadReleaseAsset(
                    assetName,
                    new Uri($"https://localhost/{assetName}"),
                    new FileInfo(archivePath).Length),
                new SquadReleaseChecksum(
                    assetName,
                    Convert.ToHexStringLower(SHA256.HashData(archiveBytes))),
                Path.GetFullPath(request.DestinationPath),
                extracted);
        }

        public void Dispose()
        {
            // No unmanaged resources.
        }
    }
}
