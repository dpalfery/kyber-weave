using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using KyberWeave.Core.Processes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the distribution manifests for the third binary, <c>kyber-weave-arbiter</c>
/// (Req 22.1): the Homebrew formula declares an <c>arbiter</c> resource for every
/// platform it already serves and installs the binary, and the npm package declares
/// the bin wrapper, maps the arbiter tool to RID-suffixed asset names in
/// <c>lib/platform.js</c>, and downloads it in <c>lib/download.js</c> — each the way
/// <c>kyber-weave-mcp</c> is handled.
///
/// The formula and <c>package.json</c> assertions read the manifests as data and run
/// everywhere. The <c>lib/</c> and bin-wrapper assertions execute the CommonJS modules
/// with <c>node</c> — the runtime the package declares in <c>engines</c> — and skip
/// where that runtime is unavailable, mirroring how <c>ReleaseTests</c> treats the
/// POSIX shell it needs. No test here touches the network: the download module is
/// exercised through its <c>KYBER_WEAVE_BINARY_DIR</c> override, which resolves
/// without fetching.
/// </summary>
public sealed class DistributionManifestTests : IDisposable
{
    private static string RepoRoot => KyberWeaveTestPaths.ToolRoot;

    private static string HomebrewFormulaPath =>
        Path.Combine(RepoRoot, "homebrew", "kyber-weave.rb");

    private static string NpmRoot => Path.Combine(RepoRoot, "npm");

    private static string NpmPackageJsonPath => Path.Combine(NpmRoot, "package.json");

    private static string NpmArbiterWrapperPath =>
        Path.Combine(NpmRoot, "bin", "kyber-weave-arbiter.js");

    private static string NpmPlatformModulePath =>
        Path.Combine(NpmRoot, "lib", "platform.js");

    private static string NpmDownloadModulePath =>
        Path.Combine(NpmRoot, "lib", "download.js");

    // The RIDs the formula serves for the cli and mcp resources; win-x64 has no
    // Homebrew story, and the arbiter must not invent one.
    private static readonly string[] HomebrewRids =
    {
        "osx-arm64",
        "osx-x64",
        "linux-arm64",
        "linux-x64",
    };

    private static readonly Regex ArbiterResourcePattern =
        new(
            "resource \\\"arbiter\\\" do\\r?\\n(?<body>.*?)\\r?\\n\\s*end",
            RegexOptions.Singleline | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(2));

    private static readonly Regex ArbiterArchivePattern =
        new(
            "kyber-weave-arbiter-(?<rid>[a-z0-9]+-[a-z0-9]+)\\.tar\\.gz",
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(2));

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    // ---- Homebrew formula ----

    [Fact]
    public void HomebrewFormulaDeclaresAnArbiterResourcePerPlatformAndInstallsIt()
    {
        string formula = File.ReadAllText(HomebrewFormulaPath);

        List<(string Rid, string Body)> resources = ExtractArbiterResources(formula);

        Assert.True(
            resources.Count == HomebrewRids.Length,
            $"The formula declares {resources.Count} `arbiter` resource(s); expected one per " +
            $"supported platform ({string.Join(", ", HomebrewRids)}), matching the `mcp` resources.");

        HashSet<string> declared = new(
            resources.Select(resource => resource.Rid),
            StringComparer.Ordinal);
        Assert.True(
            declared.SetEquals(HomebrewRids),
            "The formula's `arbiter` resources cover [" + string.Join(", ", declared) +
            "]; expected [" + string.Join(", ", HomebrewRids) + "].");

        foreach ((string Rid, string Body) resource in resources)
        {
            Assert.Matches(
                "(?m)^\\s*sha256\\s+\\S",
                resource.Body);
            Assert.True(
                ArbiterArchivePattern.IsMatch(resource.Body),
                $"The `arbiter` resource for {resource.Rid} names no " +
                $"kyber-weave-arbiter-{resource.Rid}.tar.gz archive.");
        }

        Assert.True(
            formula.Contains("resource(\"arbiter\").stage", StringComparison.Ordinal),
            "The formula never stages the `arbiter` resource in `install`; the mcp resource " +
            "is staged but the arbiter is not installed.");
        Assert.True(
            formula.Contains("bin.install \"kyber-weave-arbiter\"", StringComparison.Ordinal),
            "The formula installs kyber-weave-mcp but not kyber-weave-arbiter.");
    }

    // ---- npm package.json ----

    [Fact]
    public void NpmPackageDeclaresTheArbiterBinWrapper()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(NpmPackageJsonPath));

        Assert.True(
            document.RootElement.TryGetProperty("bin", out JsonElement bins),
            "npm/package.json declares no `bin` map at all.");

        Assert.True(
            bins.TryGetProperty("kyber-weave-arbiter", out JsonElement wrapper),
            "npm/package.json `bin` declares [" +
            string.Join(", ", bins.EnumerateObject().Select(property => property.Name)) +
            "]; `kyber-weave-arbiter` is missing.");

        Assert.True(
            wrapper.GetString() == "bin/kyber-weave-arbiter.js",
            $"npm/package.json maps `kyber-weave-arbiter` to `{wrapper.GetString()}`; " +
            "expected `bin/kyber-weave-arbiter.js`.");

        Assert.True(
            File.Exists(NpmArbiterWrapperPath),
            $"`{NpmArbiterWrapperPath}` does not exist, but package.json's `bin` map points at it.");

        string wrapperSource = File.ReadAllText(NpmArbiterWrapperPath);
        Assert.True(
            wrapperSource.Contains("resolveInstalledBinary(\"arbiter\")", StringComparison.Ordinal),
            "The kyber-weave-arbiter wrapper does not resolve the `arbiter` tool; it would " +
            "launch a different binary than the one its `bin` name promises.");
    }

    // ---- documented release assets ----

    /// <summary>
    /// Every asset name the distribution guide lists must be one the release publishes,
    /// and no name may be listed twice. Both checks matter: the list is prose, so nothing
    /// caught entries that dropped a tool's infix. <c>kyber-weave-osx-arm64.tar.gz</c> was
    /// listed for MCP and again for the arbiter while neither ships under that name, and a
    /// membership check alone cannot see it -- that name is real, it just belongs to the
    /// CLI. The duplicate is the tell.
    /// </summary>
    [Fact]
    public void DocumentedReleaseAssetsAreTheOnesTheReleasePublishes()
    {
        string guide = File.ReadAllText(Path.Combine(RepoRoot, "docs", "distribution.md"));
        HashSet<string> expected = ExpectedAssetsFromVerifier();

        List<string> documented = DocumentedAssetNames(guide);

        Assert.NotEmpty(documented);

        string[] unknown = [.. documented.Where(name => !expected.Contains(name))];
        Assert.True(
            unknown.Length == 0,
            "The distribution guide lists asset names the release does not publish: "
            + string.Join(", ", unknown)
            + ". Compare against EXPECTED_ASSETS in scripts/verify-release-checksums.sh.");

        string[] duplicated = [.. documented
            .GroupBy(name => name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)];
        Assert.True(
            duplicated.Length == 0,
            "The distribution guide lists these asset names more than once: "
            + string.Join(", ", duplicated)
            + ". One tool's infix is almost certainly missing from one of the lists.");
    }

    private static List<string> DocumentedAssetNames(string guide)
    {
        // Only the per-tool bullet lists, not the whole page: an asset is legitimately
        // named again in prose about installing it, and counting those would report a
        // duplicate that is not one. A bullet wraps across lines, so a bullet is read
        // whole -- from "- **" until the next bullet or a blank line.
        List<string> names = [];
        bool inBullet = false;
        foreach (string line in guide.Split('\n'))
        {
            bool startsBullet = line.StartsWith("- **", StringComparison.Ordinal);
            if (startsBullet)
            {
                inBullet = true;
            }
            else if (line.Length == 0 || line.StartsWith("- ", StringComparison.Ordinal))
            {
                inBullet = false;
            }

            if (!inBullet)
            {
                continue;
            }

            names.AddRange(Regex.Matches(line, @"`([A-Za-z0-9._<>-]+\.(?:tar\.gz|zip|exe))`")
                .Select(match => match.Groups[1].Value)
                .Where(name => !name.Contains('<', StringComparison.Ordinal)));
        }

        return names;
    }

    private static HashSet<string> ExpectedAssetsFromVerifier()
    {
        const string marker = "declare -a EXPECTED_ASSETS=(";
        string script = File.ReadAllText(
            Path.Combine(RepoRoot, "scripts", "verify-release-checksums.sh"));
        int start = script.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "EXPECTED_ASSETS is missing from scripts/verify-release-checksums.sh.");
        int end = script.IndexOf(')', start);
        Assert.True(end > start, "EXPECTED_ASSETS is unterminated.");

        return new HashSet<string>(
            script[(start + marker.Length)..end]
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim().Trim('"', '\'', ','))
                .Where(line => line.Length > 0),
            StringComparer.Ordinal);
    }

    // ---- npm lib/platform.js ----

    [Theory]
    [InlineData("arbiter", "win-x64", "kyber-weave-arbiter-win-x64.zip")]
    [InlineData("mcp", "osx-arm64", "kyber-weave-mcp-osx-arm64.tar.gz")]
    [InlineData("cli", "linux-x64", "kyber-weave-linux-x64.tar.gz")]
    public void NpmPlatformModuleResolvesEveryKnownTool(
        string tool,
        string rid,
        string expectedArchive)
    {
        string node = RequireRuntime("node");

        ProcessStartInfo startInfo = CreateNodeStartInfo(
            node,
            "const platform = require(process.argv[1]); " +
            "process.stdout.write(platform.assetArchiveName(process.argv[2], process.argv[3]));");
        startInfo.ArgumentList.Add(NpmPlatformModulePath);
        startInfo.ArgumentList.Add(tool);
        startInfo.ArgumentList.Add(rid);

        ProcessResult result = ProcessRunner.Run(startInfo, string.Empty);

        Assert.True(
            result.ExitCode == 0,
            $"node failed resolving {tool}: {result.StandardError.Trim()}");
        Assert.Equal(expectedArchive, result.StandardOutput.Trim());
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("CLI")]
    [InlineData("kilo")]
    [InlineData("")]
    public void NpmPlatformModuleThrowsOnAnUnknownToolRatherThanResolvingTheCli(string tool)
    {
        // The fallback resolved every typo to the CLI binary, so a caller naming a tool
        // that does not exist quietly downloaded and launched kyber-weave instead.
        string node = RequireRuntime("node");

        ProcessStartInfo startInfo = CreateNodeStartInfo(
            node,
            "const platform = require(process.argv[1]); " +
            "try { process.stdout.write(platform.assetArchiveName(process.argv[2], 'linux-x64')); } " +
            "catch (e) { process.stdout.write('THREW: ' + e.message); }");
        startInfo.ArgumentList.Add(NpmPlatformModulePath);
        startInfo.ArgumentList.Add(tool);

        ProcessResult result = ProcessRunner.Run(startInfo, string.Empty);

        Assert.True(
            result.ExitCode == 0,
            $"node failed evaluating lib/platform.js: {result.StandardError.Trim()}");
        Assert.StartsWith("THREW:", result.StandardOutput.Trim(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("linux-x64", "kyber-weave-arbiter", "kyber-weave-arbiter-linux-x64.tar.gz")]
    [InlineData("linux-arm64", "kyber-weave-arbiter", "kyber-weave-arbiter-linux-arm64.tar.gz")]
    [InlineData("osx-x64", "kyber-weave-arbiter", "kyber-weave-arbiter-osx-x64.tar.gz")]
    [InlineData("osx-arm64", "kyber-weave-arbiter", "kyber-weave-arbiter-osx-arm64.tar.gz")]
    [InlineData("win-x64", "kyber-weave-arbiter.exe", "kyber-weave-arbiter-win-x64.zip")]
    public void NpmPlatformModuleMapsTheArbiterToolToTheRidSuffixedAssetNames(
        string rid,
        string expectedBinary,
        string expectedArchive)
    {
        string node = RequireRuntime("node");

        ProcessStartInfo startInfo = CreateNodeStartInfo(
            node,
            "const platform = require(process.argv[1]); " +
            "process.stdout.write(JSON.stringify({" +
            "binary: platform.binaryFileName('arbiter', process.argv[2]), " +
            "archive: platform.assetArchiveName('arbiter', process.argv[2])}));");
        startInfo.ArgumentList.Add(NpmPlatformModulePath);
        startInfo.ArgumentList.Add(rid);

        ProcessResult result = ProcessRunner.Run(startInfo, string.Empty);

        Assert.True(
            result.ExitCode == 0,
            $"node failed evaluating lib/platform.js for {rid}: {result.StandardError.Trim()}");

        using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
        string? actualBinary = document.RootElement.GetProperty("binary").GetString();
        string? actualArchive = document.RootElement.GetProperty("archive").GetString();

        Assert.True(
            actualBinary == expectedBinary,
            $"lib/platform.js maps the arbiter tool to `{actualBinary}` on {rid}; expected " +
            $"`{expectedBinary}` — the tool must be named the way mcp is.");
        Assert.True(
            actualArchive == expectedArchive,
            $"lib/platform.js maps the arbiter tool to archive `{actualArchive}` on {rid}; " +
            $"expected `{expectedArchive}` — the tool must be named the way mcp is.");
    }

    // ---- npm lib/download.js ----

    [Fact]
    public void NpmDownloadModuleFetchesTheArbiterArchiveInTheSameFlowAsMcp()
    {
        string downloadSource = File.ReadAllText(NpmDownloadModulePath);

        Assert.True(
            downloadSource.Contains("installToolBinary(\"arbiter\"", StringComparison.Ordinal),
            "lib/download.js never passes the `arbiter` tool to installToolBinary; the " +
            "download flow fetches the cli and mcp archives but not the arbiter archive.");
    }

    [Fact]
    public void NpmDownloadModuleEnsuresTheArbiterBinaryAlongsideTheCliAndMcp()
    {
        string node = RequireRuntime("node");

        ProcessStartInfo startInfo = CreateNodeStartInfo(
            node,
            "const download = require(process.argv[1]); " +
            "download.ensureBinaries().then((installed) => { " +
            "process.stdout.write(JSON.stringify({ " +
            "installed: installed, resolved: download.resolveInstalledBinary('arbiter') })); " +
            "}).catch((error) => { " +
            "process.stderr.write(String((error && error.message) || error)); process.exit(1); });");
        startInfo.ArgumentList.Add(NpmDownloadModulePath);
        startInfo.Environment["KYBER_WEAVE_BINARY_DIR"] = _temp.Path;

        ProcessResult result = ProcessRunner.Run(startInfo, string.Empty);

        Assert.True(
            result.ExitCode == 0,
            $"node failed evaluating lib/download.js: {result.StandardError.Trim()}");

        using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
        JsonElement installed = document.RootElement.GetProperty("installed");

        Assert.True(
            installed.TryGetProperty("arbiter", out JsonElement arbiter),
            "ensureBinaries() resolved [" +
            string.Join(", ", installed.EnumerateObject().Select(property => property.Name)) +
            "]; the arbiter binary is not part of the download set.");

        string arbiterFileName = OperatingSystem.IsWindows()
            ? "kyber-weave-arbiter.exe"
            : "kyber-weave-arbiter";
        string expectedPath = Path.Combine(_temp.Path, arbiterFileName);

        Assert.True(
            arbiter.GetString() == expectedPath,
            $"ensureBinaries() resolves the arbiter to `{arbiter.GetString()}`; expected " +
            $"`{expectedPath}`.");
        Assert.True(
            document.RootElement.GetProperty("resolved").GetString() == expectedPath,
            "resolveInstalledBinary('arbiter') does not resolve the arbiter under the " +
            "KYBER_WEAVE_BINARY_DIR override.");
    }

    // ---- npm bin/kyber-weave-arbiter.js ----

    [Fact]
    public void NpmArbiterWrapperLaunchesTheArbiterBinaryFromTheOverrideDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip(
                "The fake arbiter binary is a POSIX shell script; on Windows the wrapper is " +
                "covered by the static dispatch check in NpmPackageDeclaresTheArbiterBinWrapper.");
        }

        string node = RequireRuntime("node");

        string fakeBinary = Path.Combine(_temp.Path, "kyber-weave-arbiter");
        File.WriteAllText(fakeBinary, "#!/bin/sh\necho \"fake-arbiter $@\"\n");
        MakeExecutable(fakeBinary);

        ProcessStartInfo startInfo = new(node)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(NpmArbiterWrapperPath);
        startInfo.ArgumentList.Add("--version");
        startInfo.Environment["KYBER_WEAVE_BINARY_DIR"] = _temp.Path;

        ProcessResult result = ProcessRunner.Run(startInfo, string.Empty);

        Assert.True(
            result.ExitCode == 0,
            $"The kyber-weave-arbiter wrapper failed to run the resolved binary: " +
            $"{result.StandardError.Trim()}");
        Assert.True(
            result.StandardOutput.Contains("fake-arbiter --version", StringComparison.Ordinal),
            $"The wrapper launched something other than the arbiter binary with its " +
            $"arguments; output was: {result.StandardOutput.Trim()}");
    }

    // ---- helpers ----

    private static List<(string Rid, string Body)> ExtractArbiterResources(string formula)
    {
        List<(string Rid, string Body)> resources = [];
        foreach (Match match in ArbiterResourcePattern.Matches(formula))
        {
            string body = match.Groups["body"].Value;
            Match archive = ArbiterArchivePattern.Match(body);
            resources.Add((archive.Success ? archive.Groups["rid"].Value : string.Empty, body));
        }

        return resources;
    }

    private static ProcessStartInfo CreateNodeStartInfo(string node, string script)
    {
        ProcessStartInfo startInfo = new(node)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-e");
        startInfo.ArgumentList.Add(script);
        return startInfo;
    }

    private static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    private static string RequireRuntime(string command)
    {
        string path = FindOnPath(command) ?? string.Empty;
        if (path.Length == 0)
            Assert.Skip($"The '{command}' runtime is not available, so this packaging case is skipped.");
        return path;
    }

    private static string? FindOnPath(string command)
    {
        string pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (string directory in pathVariable.Split(Path.PathSeparator))
        {
            if (directory.Length == 0)
                continue;
            string candidate = Path.Combine(directory, command);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }
}
