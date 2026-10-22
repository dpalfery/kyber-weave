using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using KyberWeave.Core.Processes;
using Xunit;
using Xunit.Sdk;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the SBOM pipeline introduced for issue #383:
///
///   (a) the vendored CycloneDX 1.6 schema matches the hashes recorded in its
///       provenance file, so the copy in the tree is the upstream one,
///   (b) <c>scripts/validate-sbom.py</c> accepts a valid document and rejects the
///       ones the schema rejects, and
///   (c) the release and CI workflows actually run the generator, and the release
///       asset list carries the SBOM.
///
/// The schema-validation tests exist because of a specific near-miss: during
/// development, an SBOM satisfied every hand-written structural check and was still
/// rejected by the CycloneDX schema, because the schema requires the
/// <c>dependencies</c> array to hold unique entries. A structural check alone would
/// have shipped it.
/// </summary>
public sealed class SbomTests
{
    private const string ValidSbom = """
        {
          "bomFormat": "CycloneDX",
          "specVersion": "1.6",
          "serialNumber": "urn:uuid:f5cb1c12-7869-44fd-bb2a-3bea6bff063f",
          "version": 1,
          "metadata": {
            "timestamp": "2026-10-21T17:48:22Z",
            "tools": { "components": [ { "type": "application", "name": "syft", "version": "1.55.0" } ] },
            "component": {
              "bom-ref": "kyber-weave@0.1.7",
              "type": "application",
              "name": "kyber-weave",
              "version": "0.1.7"
            }
          },
          "components": [
            {
              "bom-ref": "pkg:cargo/serde@1.0.229",
              "type": "library",
              "name": "serde",
              "version": "1.0.229",
              "purl": "pkg:cargo/serde@1.0.229"
            },
            {
              "bom-ref": "pkg:npm/react@19.2.7",
              "type": "library",
              "name": "react",
              "version": "19.2.7",
              "purl": "pkg:npm/react@19.2.7"
            },
            {
              "bom-ref": "pkg:nuget/Markdig@1.4.0",
              "type": "library",
              "name": "Markdig",
              "version": "1.4.0",
              "purl": "pkg:nuget/Markdig@1.4.0"
            },
            {
              "bom-ref": "pkg:nuget/anchor@1.0.0",
              "type": "library",
              "name": "anchor",
              "version": "1.0.0",
              "purl": "pkg:nuget/anchor@1.0.0"
            }
          ],
          "dependencies": [
            { "ref": "pkg:nuget/Markdig@1.4.0", "dependsOn": [ "pkg:nuget/anchor@1.0.0" ] }
          ]
        }
        """;

    /// <summary>
    /// The cargo component, with its trailing comma, so a test can remove it and
    /// leave valid JSON behind.
    /// </summary>
    private const string CargoComponent = """
        {
              "bom-ref": "pkg:cargo/serde@1.0.229",
              "type": "library",
              "name": "serde",
              "version": "1.0.229",
              "purl": "pkg:cargo/serde@1.0.229"
            },
        """;

    /// <summary>
    /// The npm component, with its trailing comma, so a test can remove it and
    /// leave valid JSON behind.
    /// </summary>
    private const string NpmComponent = """
        {
              "bom-ref": "pkg:npm/react@19.2.7",
              "type": "library",
              "name": "react",
              "version": "19.2.7",
              "purl": "pkg:npm/react@19.2.7"
            },
        """;

    /// <summary>
    /// An optional top-level <c>signature</c> block with an EC public key, for the
    /// one place the vendored schemas use <c>const</c>: the JSF <c>publicKey</c>
    /// definition picks its branch with <c>const</c> on <c>kty</c>.
    /// </summary>
    /// <remarks>
    /// The algorithm is a URI rather than <c>ES256</c> on purpose. <c>algorithm</c>
    /// is a <c>oneOf</c> of an <c>enum</c> and a <c>format: uri</c> string, and
    /// <c>format</c> is annotation-only here (see PROVENANCE.md for why), so an
    /// enumerated name matches both alternatives and <c>oneOf</c> rejects it —
    /// exactly as the reference implementation does. A URI matches only the second.
    /// </remarks>
    private const string SignatureBlock = """
        "signature": {
          "algorithm": "https://example.test/alg/2026",
          "value": "MEUCIQD...",
          "publicKey": {
            "kty": "EC",
            "crv": "P-256",
            "x": "f83OJ3D2xF1Bg8vub9tLe1gHMzV76e8Tus9uPHvRVEU",
            "y": "x_FEzRu9m36HLN_tue659LNpXW6pCyStikYjKIWI5a0"
          }
        }
        """;

    private static string ToolRoot => KyberWeaveTestPaths.ToolRoot;

    private static string ValidatorPath => Path.Combine(ToolRoot, "scripts", "validate-sbom.py");

    private static string SchemaDir => Path.Combine(ToolRoot, "scripts", "schemas");

    private static string ProvenancePath => Path.Combine(SchemaDir, "PROVENANCE.md");

    private static string ReleaseWorkflowPath =>
        Path.Combine(ToolRoot, ".github", "workflows", "release.yml");

    private static string CiWorkflowPath =>
        Path.Combine(ToolRoot, ".github", "workflows", "ci.yml");

    /// <summary>The validator shells out to python3, so these assertions are POSIX-only.</summary>
    private static void SkipOnWindows()
    {
        if (OperatingSystem.IsWindows())
        {
            throw SkipException.ForSkip("scripts/validate-sbom.py needs python3; the Windows runner does not run it.");
        }
    }

    private static (int ExitCode, string StandardOutput, string StandardError) RunValidator(
        string sbomPath,
        params string[] extraArguments)
    {
        ProcessStartInfo startInfo = new("python3")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(ValidatorPath);
        startInfo.ArgumentList.Add(sbomPath);
        foreach (string argument in extraArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        ProcessResult result = ProcessRunner.Run(startInfo, string.Empty);
        return (result.ExitCode, result.StandardOutput, result.StandardError);
    }

    private static string WriteTempSbom(string content)
    {
        string path = Path.Combine(Path.GetTempPath(), "kyber-weave-sbom-test-" + Guid.NewGuid().ToString("N") + ".cdx.json");
        File.WriteAllText(path, content);
        return path;
    }

    private static string Sha256Hex(string path)
    {
        using SHA256 sha = SHA256.Create();
        using FileStream stream = File.OpenRead(path);
        byte[] bytes = sha.ComputeHash(stream);
        StringBuilder builder = new(bytes.Length * 2);
        foreach (byte b in bytes)
        {
            builder.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    [Fact]
    public void TheVendoredCycloneDxSchemasMatchTheHashesRecordedInProvenance()
    {
        string provenance = File.ReadAllText(ProvenancePath);

        string[] schemaFiles =
        [
            "bom-1.6.schema.json",
            "spdx.schema.json",
            "jsf-0.82.schema.json"
        ];

        foreach (string schemaFile in schemaFiles)
        {
            string path = Path.Combine(SchemaDir, schemaFile);
            Assert.True(File.Exists(path), $"the vendored schema is missing: {schemaFile}");

            string actual = Sha256Hex(path);

            // The provenance file records one "| `file` | `hash` |" row per schema.
            bool recorded = false;
            foreach (string line in provenance.Split('\n'))
            {
                if (!line.Contains(schemaFile, StringComparison.Ordinal))
                {
                    continue;
                }

                // The row is | `name` | `hash` |, so the hash is the fourth
                // backtick-separated token on the line.
                string[] tokens = line.Split('`');
                if (tokens.Length < 4)
                {
                    // A line that mentions the file but is not the table row —
                    // the refresh command in the same file names all three.
                    continue;
                }

                string recordedHash = tokens[3];
                Assert.False(
                    string.IsNullOrEmpty(recordedHash),
                    $"PROVENANCE.md records no hash for {schemaFile}");
                Assert.Equal(
                    actual,
                    recordedHash,
                    StringComparer.OrdinalIgnoreCase);
                recorded = true;
            }

            Assert.True(recorded, $"PROVENANCE.md records no hash for {schemaFile}");
        }
    }

    [Fact]
    public void AValidCycloneDxSbomPassesSchemaValidation()
    {
        SkipOnWindows();

        string path = WriteTempSbom(ValidSbom);
        try
        {
            (int exitCode, string standardOutput, string standardError) = RunValidator(path);

            Assert.True(
                exitCode == 0,
                $"the validator rejected a valid SBOM: exit {exitCode}\n{standardError}");
            Assert.Contains("CycloneDX 1.6", standardOutput, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AValidSignedSbomPassesSchemaValidation()
    {
        SkipOnWindows();

        // A signature is optional and nothing this pipeline generates carries one,
        // but it is part of the schema, and it is the only place the vendored files
        // use `const`. With `const` unimplemented, every publicKey branch's `then`
        // applied and an EC key was rejected for not also being an OKP and an RSA
        // key, so the validator failed a document the schema permits.
        string withoutClose = ValidSbom[..ValidSbom.LastIndexOf('}')].TrimEnd();
        string signed = withoutClose + ",\n  " + SignatureBlock + "\n}";

        string path = WriteTempSbom(signed);
        try
        {
            (int exitCode, _, string standardError) = RunValidator(path);

            Assert.True(
                exitCode == 0,
                $"the validator rejected a valid signed SBOM: exit {exitCode}\n{standardError}");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(
        "a component that is not an object",
        CargoComponent,
        "\"not an object\",")]
    [InlineData(
        "a dependency entry that is not an object",
        "{ \"ref\": \"pkg:nuget/Markdig@1.4.0\", \"dependsOn\": [ \"pkg:nuget/anchor@1.0.0\" ] }",
        "\"not an object\"")]
    [InlineData(
        "a purl that is not a string",
        "\"purl\": \"pkg:cargo/serde@1.0.229\"",
        "\"purl\": 42")]
    [InlineData(
        "a dependsOn that is not an array",
        "\"dependsOn\": [ \"pkg:nuget/anchor@1.0.0\" ]",
        "\"dependsOn\": \"pkg:nuget/anchor@1.0.0\"")]
    public void AMalformedSbomFailsCleanlyWithoutATraceback(string what, string find, string replaceWith)
    {
        SkipOnWindows();

        string malformed = ValidSbom.Replace(find, replaceWith, StringComparison.Ordinal);
        Assert.NotEqual(ValidSbom, malformed);

        string path = WriteTempSbom(malformed);
        try
        {
            (int exitCode, _, string standardError) = RunValidator(path);

            Assert.True(exitCode != 0, $"{what} must be rejected");

            // The schema check and the structural checks are additive: a shape the
            // schema has already rejected must not become an AttributeError in the
            // checks that run afterwards, and a string where an array belongs must
            // not be walked character by character.
            Assert.DoesNotContain("Traceback", standardError, StringComparison.Ordinal);
            Assert.DoesNotContain("depends on an unknown component", standardError, StringComparison.Ordinal);
            Assert.Contains("::error::", standardError, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ASbomWithDuplicateDependencyEntriesFailsSchemaValidation()
    {
        SkipOnWindows();

        // Two entries for the same ref. The CycloneDX schema requires the
        // dependencies array to hold unique entries, and this is exactly the
        // defect that a structural check alone would have let through.
        string withDuplicate = ValidSbom.Replace(
            "{ \"ref\": \"pkg:nuget/Markdig@1.4.0\", \"dependsOn\": [ \"pkg:nuget/anchor@1.0.0\" ] }",
            "{ \"ref\": \"pkg:nuget/Markdig@1.4.0\", \"dependsOn\": [ \"pkg:nuget/anchor@1.0.0\" ] },\n"
            + "            { \"ref\": \"pkg:nuget/Markdig@1.4.0\", \"dependsOn\": [ \"pkg:nuget/anchor@1.0.0\" ] }",
            StringComparison.Ordinal);

        Assert.True(
            !string.Equals(ValidSbom, withDuplicate, StringComparison.Ordinal),
            "the fixture must actually contain the duplicate entry");

        string path = WriteTempSbom(withDuplicate);
        try
        {
            (int exitCode, _, string standardError) = RunValidator(path);

            Assert.NotEqual(0, exitCode);
            Assert.Contains("duplicate", standardError, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ASbomWithNoComponentsFailsValidation()
    {
        SkipOnWindows();

        string path = WriteTempSbom("""
            {
              "bomFormat": "CycloneDX",
              "specVersion": "1.6",
              "version": 1,
              "components": []
            }
            """);
        try
        {
            (int exitCode, _, string standardError) = RunValidator(path);

            Assert.NotEqual(0, exitCode);
            Assert.Contains("no components", standardError, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AnSbomMissingAnExpectedEcosystemFailsValidation()
    {
        SkipOnWindows();

        // A document with no cargo components. The ecosystem is expected, so this
        // is an error rather than a warning.
        string path = WriteTempSbom(ValidSbom.Replace(
            CargoComponent,
            "",
            StringComparison.Ordinal));

        try
        {
            (int exitCode, _, string standardError) = RunValidator(
                path,
                "--require-ecosystems",
                "npm,cargo,nuget");

            Assert.NotEqual(0, exitCode);
            Assert.Contains("no cargo components", standardError, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AnSbomMissingAnOptionalEcosystemIsAWarningRatherThanAnError()
    {
        SkipOnWindows();

        // A document with npm and cargo but no nuget at all, and nuget marked
        // optional. This is the path the generator takes when there is no .NET
        // SDK: the nuget coverage is knowingly partial, so its absence is
        // reported rather than fatal.
        string path = WriteTempSbom("""
            {
              "bomFormat": "CycloneDX",
              "specVersion": "1.6",
              "version": 1,
              "components": [
                {
                  "bom-ref": "pkg:npm/react@19.2.7",
                  "type": "library",
                  "name": "react",
                  "version": "19.2.7",
                  "purl": "pkg:npm/react@19.2.7"
                },
                {
                  "bom-ref": "pkg:cargo/serde@1.0.229",
                  "type": "library",
                  "name": "serde",
                  "version": "1.0.229",
                  "purl": "pkg:cargo/serde@1.0.229"
                }
              ],
              "dependencies": []
            }
            """);

        try
        {
            (int exitCode, _, string standardError) = RunValidator(
                path,
                "--require-ecosystems",
                "npm,cargo,nuget",
                "--optional-ecosystems",
                "nuget");

            Assert.True(
                exitCode == 0,
                $"partial coverage must be a warning, not an error: exit {exitCode}\n{standardError}");
            Assert.Contains("no nuget components", standardError, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TheReleaseWorkflowGeneratesTheSbomBeforeTheReleaseJob()
    {
        string workflow = File.ReadAllText(ReleaseWorkflowPath);

        int sbomJob = workflow.IndexOf("  sbom:", StringComparison.Ordinal);
        Assert.True(sbomJob >= 0, "release.yml has no sbom job.");

        int generate = workflow.IndexOf("scripts/generate-sbom.sh", StringComparison.Ordinal);
        Assert.True(generate > sbomJob, "the sbom job must call scripts/generate-sbom.sh.");

        int upload = workflow.IndexOf("name: sbom", StringComparison.Ordinal);
        Assert.True(upload > sbomJob, "the sbom job must upload the SBOM as an artifact.");

        // The release job must wait for the SBOM, or it can publish a release
        // whose asset list contains an SBOM that was never generated.
        int releaseJob = workflow.IndexOf("  release:", StringComparison.Ordinal);
        Assert.True(releaseJob > sbomJob, "the release job must come after the sbom job.");

        string releaseJobText = workflow[releaseJob..];
        int needs = releaseJobText.IndexOf("needs:", StringComparison.Ordinal);
        Assert.True(needs >= 0, "the release job has no needs:.");
        Assert.Contains("sbom", releaseJobText[needs..], StringComparison.Ordinal);
    }

    [Fact]
    public void TheCiWorkflowValidatesTheSbomWithoutNeedingARelease()
    {
        string workflow = File.ReadAllText(CiWorkflowPath);

        Assert.Contains("scripts/generate-sbom.sh", workflow, StringComparison.Ordinal);
        Assert.Contains("scripts/validate-sbom.py", workflow, StringComparison.Ordinal);
        Assert.Contains("require-ecosystems", workflow, StringComparison.Ordinal);

        // The SBOM job is a required gate: its result is part of the summary's
        // pass/fail decision, not merely reported.
        Assert.Contains("needs.sbom.result", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingEcosystemThatIsNotOptionalStillFailsValidation()
    {
        SkipOnWindows();

        // Marking one ecosystem optional must not soften the others. nuget is
        // optional here because the generator knows the .NET coverage is partial
        // without an SDK; npm is not optional, because Syft reads the npm lock
        // files directly and their absence means the scan went wrong.
        string path = WriteTempSbom(ValidSbom.Replace(
            NpmComponent,
            "",
            StringComparison.Ordinal));

        try
        {
            (int exitCode, _, string standardError) = RunValidator(
                path,
                "--require-ecosystems",
                "npm,cargo,nuget",
                "--optional-ecosystems",
                "nuget");

            Assert.NotEqual(0, exitCode);
            Assert.Contains("no npm components", standardError, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TheExpectedReleaseAssetListCarriesTheSbom()
    {
        SkipOnWindows();

        ProcessStartInfo startInfo = new("/bin/bash")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(
            Path.Combine(ToolRoot, "scripts", "verify-release-checksums.sh"));
        startInfo.ArgumentList.Add("--list");
        startInfo.ArgumentList.Add(Path.GetTempPath());
        startInfo.ArgumentList.Add("9.9.9");

        ProcessResult result = ProcessRunner.Run(startInfo, string.Empty);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(
            "kyber-weave-sbom-9.9.9.cdx.json",
            result.StandardOutput,
            StringComparison.Ordinal);
    }
}
