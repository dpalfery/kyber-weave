using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KyberWeave.Core.Squad.Deployment;
using Xunit;
using Xunit.Abstractions;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the ownership boundary for Squad deployment. A receipt grants authority over only the
/// byte-identical paths it records; it is never a general overwrite or delete permission.
/// </summary>
public sealed class SquadDeploymentStateTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset InstalledAt =
        new(2026, 8, 14, 12, 34, 56, TimeSpan.Zero);

    [Fact]
    public void SerializeProjectStateIsStablePortableSecretFreeAndRoundTrips()
    {
        using TempDirectory fixture = new TempDirectory();
        string secretApplicationData = Path.Combine(fixture.Path, "TOP-SECRET-USER-PATH");
        SquadStateStore store = Store(secretApplicationData);
        SquadLock squadLock = Lock();
        SquadReceipt receipt = new SquadReceipt(
            "kyber-squad.receipt/v1",
            SquadDeploymentScope.Project,
            ".",
            InstalledAt,
            [new SquadDegradation("warp", "conductor", "role-skill-fallback")],
            [
                new SquadOwnedFile(
                    ".codex/agents/conductor.toml",
                    Digest("conductor"),
                    "codex",
                    false),
                new SquadOwnedFile(
                    ".warp/skills/conductor/SKILL.md",
                    Digest("role skill"),
                    "warp",
                    true)
            ]);

        string lockYaml = store.SerializeLock(squadLock);
        string receiptJson = store.SerializeReceipt(receipt);

        Assert.Equal(
            $$"""
            schema: kyber-squad.lock/v1
            squad-version: 1.2.3
            cli-version: 1.2.3
            mcp-version: 1.2.3
            bundle: full
            targets:
              - codex
              - warp
            exclusions:
              - cursor
            translation: best-effort
            bundle-digest: {{Digest("bundle")}}
            asset-digest: {{Digest("asset")}}
            apm:
              version: 1.2.3
              tag-commit: 0123456789abcdef
              asset-sha256: {{Digest("apm")}}

            """.Replace("\r\n", "\n"),
            lockYaml.Replace("\r\n", "\n"));
        Assert.Equal(
            $$"""
            {
              "schema": "kyber-squad.receipt/v1",
              "scope": "project",
              "targetRoot": ".",
              "installedAtUtc": "2026-08-14T12:34:56.0000000Z",
              "degradations": [
                {
                  "target": "warp",
                  "subject": "conductor",
                  "code": "role-skill-fallback"
                }
              ],
              "files": [
                {
                  "relativePath": ".codex/agents/conductor.toml",
                  "sha256": "{{Digest("conductor")}}",
                  "target": "codex",
                  "adopted": false
                },
                {
                  "relativePath": ".warp/skills/conductor/SKILL.md",
                  "sha256": "{{Digest("role skill")}}",
                  "target": "warp",
                  "adopted": true
                }
              ]
            }

            """.Replace("\r\n", "\n"),
            receiptJson.Replace("\r\n", "\n"));
        Assert.DoesNotContain(fixture.Path, lockYaml, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Path, receiptJson, StringComparison.Ordinal);
        Assert.DoesNotContain("TOP-SECRET", lockYaml, StringComparison.Ordinal);
        Assert.DoesNotContain("TOP-SECRET", receiptJson, StringComparison.Ordinal);
        AssertLockEqual(squadLock, store.DeserializeLock(lockYaml));
        AssertReceiptEqual(receipt, store.DeserializeReceipt(receiptJson));
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("bundle-digest")]
    [InlineData("asset-digest")]
    [InlineData("apm-digest")]
    public void SerializeLockInvalidSchemaOrDigestIsRejected(string invalidField)
    {
        using TempDirectory fixture = new TempDirectory();
        SquadStateStore store = Store(fixture.Path);
        SquadLock valid = Lock();
        SquadLock invalid = invalidField switch
        {
            "schema" => valid with { Schema = "kyber-squad.lock/v999" },
            "bundle-digest" => valid with { BundleDigest = "not-a-sha256" },
            "asset-digest" => valid with { AssetDigest = "ABCDEF" },
            "apm-digest" => valid with
            {
                Apm = valid.Apm with { AssetSha256 = new string('0', 63) }
            },
            _ => throw new ArgumentOutOfRangeException(nameof(invalidField), invalidField, null)
        };

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => store.SerializeLock(invalid));

        Assert.Contains(invalidField.Split('-')[0], exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("bundle-digest")]
    [InlineData("asset-digest")]
    [InlineData("apm-digest")]
    public void DeserializeLockInvalidSchemaOrDigestIsRejected(string invalidField)
    {
        using TempDirectory fixture = new TempDirectory();
        SquadStateStore store = Store(fixture.Path);
        string yaml = store.SerializeLock(Lock());
        string invalidYaml = invalidField switch
        {
            "schema" => yaml.Replace(
                "schema: kyber-squad.lock/v1",
                "schema: kyber-squad.lock/v999",
                StringComparison.Ordinal),
            "bundle-digest" => yaml.Replace(
                $"bundle-digest: {Digest("bundle")}",
                "bundle-digest: not-a-sha256",
                StringComparison.Ordinal),
            "asset-digest" => yaml.Replace(
                $"asset-digest: {Digest("asset")}",
                "asset-digest: ABCDEF",
                StringComparison.Ordinal),
            "apm-digest" => yaml.Replace(
                $"asset-sha256: {Digest("apm")}",
                $"asset-sha256: {new string('0', 63)}",
                StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(invalidField), invalidField, null)
        };

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => store.DeserializeLock(invalidYaml));

        Assert.Contains(invalidField.Split('-')[0], exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("absolute-root")]
    [InlineData("absolute-path")]
    [InlineData("file-digest")]
    public void SerializeReceiptInvalidSchemaRootPathOrDigestIsRejected(string invalidField)
    {
        using TempDirectory fixture = new TempDirectory();
        SquadStateStore store = Store(fixture.Path);
        SquadOwnedFile validFile = new SquadOwnedFile(
            ".codex/agents/conductor.toml",
            Digest("conductor"),
            "codex",
            false);
        SquadReceipt valid = Receipt(validFile);
        SquadReceipt invalid = invalidField switch
        {
            "schema" => valid with { Schema = "kyber-squad.receipt/v999" },
            "absolute-root" => valid with { TargetRoot = "/private/operator/root" },
            "absolute-path" => valid with
            {
                Files = [validFile with { RelativePath = "/private/conductor.toml" }]
            },
            "file-digest" => valid with
            {
                Files = [validFile with { Sha256 = "not-a-sha256" }]
            },
            _ => throw new ArgumentOutOfRangeException(nameof(invalidField), invalidField, null)
        };

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => store.SerializeReceipt(invalid));

        Assert.Contains(invalidField.Split('-')[^1], exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("absolute-root")]
    [InlineData("absolute-path")]
    [InlineData("file-digest")]
    public void DeserializeReceiptInvalidSchemaRootPathOrDigestIsRejected(string invalidField)
    {
        using TempDirectory fixture = new TempDirectory();
        SquadStateStore store = Store(fixture.Path);
        string json = store.SerializeReceipt(Receipt(new SquadOwnedFile(
            ".codex/agents/conductor.toml",
            Digest("conductor"),
            "codex",
            false)));
        string invalidJson = invalidField switch
        {
            "schema" => json.Replace(
                "kyber-squad.receipt/v1",
                "kyber-squad.receipt/v999",
                StringComparison.Ordinal),
            "absolute-root" => json.Replace(
                "\"targetRoot\": \".\"",
                "\"targetRoot\": \"/private/operator/root\"",
                StringComparison.Ordinal),
            "absolute-path" => json.Replace(
                ".codex/agents/conductor.toml",
                "/private/conductor.toml",
                StringComparison.Ordinal),
            "file-digest" => json.Replace(
                Digest("conductor"),
                "not-a-sha256",
                StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(invalidField), invalidField, null)
        };

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => store.DeserializeReceipt(invalidJson));

        Assert.Contains(invalidField.Split('-')[^1], exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveStateGlobalScopeUsesInjectedUserPathAndClock()
    {
        using TempDirectory fixture = new TempDirectory();
        string applicationData = Path.Combine(fixture.Path, "application-data");
        SquadStateStore store = Store(applicationData);
        SquadDeploymentPlan plan = SquadDeploymentPlan.CreateInstall(
            fixture.Path,
            SquadDeploymentScope.Global,
            Lock(),
            [Rendered(".codex/agents/conductor.toml", "body")],
            [],
            adopt: false,
            new FixedTimeProvider(InstalledAt));

        string stateDirectory = store.ResolveStateDirectory(
            fixture.Path,
            SquadDeploymentScope.Global);

        Assert.Equal(
            Path.Combine(applicationData, "KyberWeave", "squad"),
            stateDirectory);
        Assert.Equal(InstalledAt, plan.Receipt.InstalledAtUtc);
        Assert.Equal(".", plan.Receipt.TargetRoot);
        string receiptJson = store.SerializeReceipt(plan.Receipt);
        Assert.Contains("\"scope\": \"global\"", receiptJson, StringComparison.Ordinal);
        Assert.DoesNotContain(applicationData, receiptJson, StringComparison.Ordinal);
    }

    [Fact]
    public void GlobalStateTwoTargetRootsSharingOneStateDirectoryRemainPrivatelyBoundToTheirOwnReceipts()
    {
        using TempDirectory fixture = new TempDirectory();
        string applicationData = Path.Combine(fixture.Path, "application-data");
        string firstRoot = Path.Combine(fixture.Path, "customer-alpha");
        string secondRoot = Path.Combine(fixture.Path, "customer-beta");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        SquadStateStore store = Store(applicationData);
        SquadDeploymentPlan firstPlan = SquadDeploymentPlan.CreateInstall(
            firstRoot,
            SquadDeploymentScope.Global,
            Lock(),
            [Rendered(".codex/agents/first.toml", "first")],
            [],
            adopt: false,
            new FixedTimeProvider(InstalledAt));
        SquadDeploymentPlan secondPlan = SquadDeploymentPlan.CreateInstall(
            secondRoot,
            SquadDeploymentScope.Global,
            Lock(),
            [Rendered(".codex/agents/second.toml", "second")],
            [],
            adopt: false,
            new FixedTimeProvider(InstalledAt.AddMinutes(1)));

        new SquadTransaction(store).Execute(firstPlan);
        new SquadTransaction(store).Execute(secondPlan);

        SquadReceipt firstReceipt = Assert.IsType<SquadReceipt>(store.ReadReceipt(
            firstRoot,
            SquadDeploymentScope.Global));
        SquadReceipt secondReceipt = Assert.IsType<SquadReceipt>(store.ReadReceipt(
            secondRoot,
            SquadDeploymentScope.Global));
        Assert.Equal(".codex/agents/first.toml", Assert.Single(firstReceipt.Files).RelativePath);
        Assert.Equal(".codex/agents/second.toml", Assert.Single(secondReceipt.Files).RelativePath);
        Assert.NotEqual(firstReceipt.InstalledAtUtc, secondReceipt.InstalledAtUtc);

        string stateDirectory = store.ResolveStateDirectory(firstRoot, SquadDeploymentScope.Global);
        string[] statePaths = Directory.EnumerateFileSystemEntries(
                stateDirectory,
                "*",
                SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(stateDirectory, path).Replace(
                Path.DirectorySeparatorChar,
                '/'))
            .ToArray();
        Assert.NotEmpty(statePaths);
        Assert.All(statePaths, relativePath =>
        {
            Assert.DoesNotContain("customer-alpha", relativePath, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("customer-beta", relativePath, StringComparison.OrdinalIgnoreCase);
        });
        Assert.DoesNotContain(firstRoot, Store(applicationData).SerializeReceipt(firstReceipt), StringComparison.Ordinal);
        Assert.DoesNotContain(secondRoot, Store(applicationData).SerializeReceipt(secondReceipt), StringComparison.Ordinal);
    }

    [Fact]
    public void ExecuteGlobalScopeKeepsDurableIntentInStateAndStagesAndBackupsOnDestinationFilesystem()
    {
        using TempDirectory fixture = new TempDirectory();
        string applicationData = Path.Combine(fixture.Path, "application-data");
        string targetRoot = Path.Combine(fixture.Path, "global-target");
        Directory.CreateDirectory(targetRoot);
        Write(targetRoot, ".codex/agents/conductor.toml", "installed body");
        SquadReceipt receipt = new SquadReceipt(
            "kyber-squad.receipt/v1",
            SquadDeploymentScope.Global,
            ".",
            InstalledAt,
            [],
            [new SquadOwnedFile(
                ".codex/agents/conductor.toml",
                Digest("installed body"),
                "codex",
                false)]);
        SquadDeploymentPlan plan = SquadDeploymentPlan.CreateUpdate(
            targetRoot,
            SquadDeploymentScope.Global,
            Lock("1.2.4"),
            [Rendered(".codex/agents/conductor.toml", "updated body")],
            receipt,
            [],
            replaceManaged: false,
            new FixedTimeProvider(InstalledAt.AddDays(1)));
        SquadStateStore store = Store(applicationData);
        GlobalTransactionLocationObserver observer = new GlobalTransactionLocationObserver(
            targetRoot,
            store.ResolveStateDirectory(targetRoot, SquadDeploymentScope.Global));

        new SquadTransaction(store, observer).Execute(plan);

        Assert.True(observer.SawDurableIntentInState);
        Assert.True(observer.SawStagingOnTargetFilesystem);
        Assert.True(observer.SawBackupOnTargetFilesystem);
        Assert.False(observer.SawStagingOrBackupInState);
        Assert.Equal("updated body", Read(targetRoot, ".codex/agents/conductor.toml"));
    }

    [Fact]
    public void CreateInstallIdenticalUnmanagedFileWithAdoptRecordsOwnershipWithoutRewriting()
    {
        using TempDirectory fixture = new TempDirectory();
        Write(fixture.Path, ".codex/agents/conductor.toml", "same bytes");
        DateTime before = File.GetLastWriteTimeUtc(
            Path.Combine(fixture.Path, ".codex/agents/conductor.toml"));
        SquadDeploymentPlan plan = SquadDeploymentPlan.CreateInstall(
            fixture.Path,
            SquadDeploymentScope.Project,
            Lock(),
            [Rendered(".codex/agents/conductor.toml", "same bytes")],
            [],
            adopt: true,
            new FixedTimeProvider(InstalledAt));

        Transaction(fixture.Path).Execute(plan);

        SquadOwnedFile owned = Assert.Single(plan.Receipt.Files);
        Assert.True(owned.Adopted);
        Assert.Equal(Digest("same bytes"), owned.Sha256);
        Assert.Equal(before, File.GetLastWriteTimeUtc(
            Path.Combine(fixture.Path, owned.RelativePath)));
    }

    [Fact]
    public void ExecuteInstallPathCreatedAfterPreflightRefusesToOverwriteRacedUnmanagedFile()
    {
        using TempDirectory fixture = new TempDirectory();
        const string relativePath = ".codex/agents/conductor.toml";
        SquadDeploymentPlan plan = SquadDeploymentPlan.CreateInstall(
            fixture.Path,
            SquadDeploymentScope.Project,
            Lock(),
            [Rendered(relativePath, "generated body")],
            [],
            adopt: false,
            new FixedTimeProvider(InstalledAt));
        Write(fixture.Path, relativePath, "raced unmanaged body");
        SortedDictionary<string, TreeEntry> before = CaptureTree(fixture.Path);

        SquadDeploymentConflictException exception = Assert.Throws<SquadDeploymentConflictException>(() =>
            Transaction(fixture.Path).Execute(plan));

        Assert.Contains(relativePath, exception.Message, StringComparison.Ordinal);
        Assert.Equal("raced unmanaged body", Read(fixture.Path, relativePath));
        AssertTreesEqual(before, CaptureTree(fixture.Path));
    }

    [Theory]
    [InlineData("same bytes", false)]
    [InlineData("different bytes", true)]
    [InlineData("different bytes", false)]
    public void CreateInstallUnmanagedCollisionRefusesWithoutChangingTree(
        string existingContent,
        bool adopt)
    {
        using TempDirectory fixture = new TempDirectory();
        Write(fixture.Path, ".codex/agents/conductor.toml", existingContent);
        SortedDictionary<string, byte[]> before = Snapshot(fixture.Path);

        SquadDeploymentConflictException exception = Assert.Throws<SquadDeploymentConflictException>(() =>
            SquadDeploymentPlan.CreateInstall(
                fixture.Path,
                SquadDeploymentScope.Project,
                Lock(),
                [Rendered(".codex/agents/conductor.toml", "same bytes")],
                [],
                adopt,
                new FixedTimeProvider(InstalledAt)));

        Assert.Contains(
            ".codex/agents/conductor.toml",
            exception.Message,
            StringComparison.Ordinal);
        AssertSnapshotsEqual(before, Snapshot(fixture.Path));
    }

    [Theory]
    [InlineData(".codex/agents/Reviewer.toml", ".codex/agents/reviewer.toml")]
    [InlineData(".codex/agents/reviewer.toml", ".codex/agents/reviewer.toml.")]
    [InlineData(".codex/agents/reviewer.toml", ".codex/agents/reviewer.toml ")]
    [InlineData(".codex/agents/reviewer.toml", ".codex/agents./reviewer.toml")]
    public void CreateInstallPortablePathAliasesAreRejectedBeforeWrites(
        string firstPath,
        string aliasPath)
    {
        using TempDirectory fixture = new TempDirectory();

        SquadDeploymentConflictException exception = Assert.Throws<SquadDeploymentConflictException>(() =>
            SquadDeploymentPlan.CreateInstall(
                fixture.Path,
                SquadDeploymentScope.Project,
                Lock(),
                [Rendered(firstPath, "first"), Rendered(aliasPath, "second")],
                [],
                adopt: false,
                new FixedTimeProvider(InstalledAt)));

        Assert.Contains("portable", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(aliasPath, exception.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Path));
    }

    [Theory]
    [InlineData(".codex/agents/Reviewer.toml", ".codex/agents/reviewer.toml")]
    [InlineData(".codex/agents/reviewer.toml", ".codex/agents/reviewer.toml.")]
    [InlineData(".codex/agents/reviewer.toml", ".codex/agents/reviewer.toml ")]
    [InlineData(".codex/agents/reviewer.toml", ".codex/agents./reviewer.toml")]
    public void CreateUninstallReceiptPortablePathAliasesAreRejected(
        string firstPath,
        string aliasPath)
    {
        using TempDirectory fixture = new TempDirectory();
        SquadReceipt receipt = Receipt(
            new SquadOwnedFile(firstPath, Digest("first"), "codex", false),
            new SquadOwnedFile(aliasPath, Digest("second"), "codex", false));

        SquadDeploymentConflictException exception = Assert.Throws<SquadDeploymentConflictException>(() =>
            SquadDeploymentPlan.CreateUninstall(
                fixture.Path,
                SquadDeploymentScope.Project,
                receipt));

        Assert.Contains("portable", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(aliasPath, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateUpdateLocallyEditedManagedFileWithoutReplacePreservesFileAndReceiptHash()
    {
        using TempDirectory fixture = new TempDirectory();
        SquadReceipt oldReceipt = Receipt(
            new SquadOwnedFile(
                ".codex/agents/conductor.toml",
                Digest("installed body"),
                "codex",
                false));
        Write(fixture.Path, ".codex/agents/conductor.toml", "operator edit");
        SquadDeploymentPlan plan = SquadDeploymentPlan.CreateUpdate(
            fixture.Path,
            SquadDeploymentScope.Project,
            Lock("1.2.4"),
            [Rendered(".codex/agents/conductor.toml", "updated body")],
            oldReceipt,
            [],
            replaceManaged: false,
            new FixedTimeProvider(InstalledAt.AddDays(1)));

        Transaction(fixture.Path).Execute(plan);

        Assert.Equal(
            "operator edit",
            Read(fixture.Path, ".codex/agents/conductor.toml"));
        SquadOwnedFile retained = Assert.Single(plan.Receipt.Files);
        Assert.Equal(Digest("installed body"), retained.Sha256);
        Assert.False(retained.Adopted);
    }

    [Fact]
    public void CreateUpdateLocallyEditedManagedFileWithReplaceReplacesOnlyReceiptOwnedPath()
    {
        using TempDirectory fixture = new TempDirectory();
        SquadReceipt oldReceipt = Receipt(
            new SquadOwnedFile(
                ".codex/agents/conductor.toml",
                Digest("installed body"),
                "codex",
                false));
        Write(fixture.Path, ".codex/agents/conductor.toml", "operator edit");
        SquadDeploymentPlan plan = SquadDeploymentPlan.CreateUpdate(
            fixture.Path,
            SquadDeploymentScope.Project,
            Lock("1.2.4"),
            [Rendered(".codex/agents/conductor.toml", "updated body")],
            oldReceipt,
            [],
            replaceManaged: true,
            new FixedTimeProvider(InstalledAt.AddDays(1)));

        Transaction(fixture.Path).Execute(plan);

        Assert.Equal(
            "updated body",
            Read(fixture.Path, ".codex/agents/conductor.toml"));
        SquadOwnedFile replaced = Assert.Single(plan.Receipt.Files);
        Assert.Equal(Digest("updated body"), replaced.Sha256);
    }

    [Fact]
    public void ExecuteUpdateFileChangedAfterExactHashPreflightRefusesToOverwriteRacedEdit()
    {
        using TempDirectory fixture = new TempDirectory();
        const string relativePath = ".codex/agents/conductor.toml";
        SquadReceipt receipt = Receipt(new SquadOwnedFile(
            relativePath,
            Digest("installed body"),
            "codex",
            false));
        Write(fixture.Path, relativePath, "installed body");
        WriteState(fixture.Path, Lock(), receipt);
        SquadDeploymentPlan plan = SquadDeploymentPlan.CreateUpdate(
            fixture.Path,
            SquadDeploymentScope.Project,
            Lock("1.2.4"),
            [Rendered(relativePath, "updated body")],
            receipt,
            [],
            replaceManaged: false,
            new FixedTimeProvider(InstalledAt.AddDays(1)));
        Write(fixture.Path, relativePath, "raced operator edit");
        SortedDictionary<string, TreeEntry> before = CaptureTree(fixture.Path);

        SquadDeploymentConflictException exception = Assert.Throws<SquadDeploymentConflictException>(() =>
            Transaction(fixture.Path).Execute(plan));

        Assert.Contains(relativePath, exception.Message, StringComparison.Ordinal);
        Assert.Equal("raced operator edit", Read(fixture.Path, relativePath));
        AssertTreesEqual(before, CaptureTree(fixture.Path));
    }

    [Fact]
    public void CreateUpdateReplaceManagedDoesNotAuthorizeUnmanagedCollision()
    {
        using TempDirectory fixture = new TempDirectory();
        SquadReceipt oldReceipt = Receipt(
            new SquadOwnedFile(
                ".codex/agents/conductor.toml",
                Digest("installed body"),
                "codex",
                false));
        Write(fixture.Path, ".codex/agents/conductor.toml", "installed body");
        Write(fixture.Path, ".codex/agents/reviewer.toml", "unmanaged file");
        SortedDictionary<string, byte[]> before = Snapshot(fixture.Path);

        SquadDeploymentConflictException exception = Assert.Throws<SquadDeploymentConflictException>(() =>
            SquadDeploymentPlan.CreateUpdate(
                fixture.Path,
                SquadDeploymentScope.Project,
                Lock("1.2.4"),
                [
                    Rendered(".codex/agents/conductor.toml", "updated body"),
                    Rendered(".codex/agents/reviewer.toml", "generated body")
                ],
                oldReceipt,
                [],
                replaceManaged: true,
                new FixedTimeProvider(InstalledAt.AddDays(1))));

        Assert.Contains(
            ".codex/agents/reviewer.toml",
            exception.Message,
            StringComparison.Ordinal);
        AssertSnapshotsEqual(before, Snapshot(fixture.Path));
    }

    [Fact]
    public void CreateUninstallUnchangedOwnedFileIsRemovedAndEditedOwnedFileIsRetained()
    {
        using TempDirectory fixture = new TempDirectory();
        SquadOwnedFile unchanged = new SquadOwnedFile(
            ".codex/agents/conductor.toml",
            Digest("installed conductor"),
            "codex",
            false);
        SquadOwnedFile edited = new SquadOwnedFile(
            ".codex/agents/reviewer.toml",
            Digest("installed reviewer"),
            "codex",
            false);
        SquadReceipt receipt = Receipt(unchanged, edited);
        Write(fixture.Path, unchanged.RelativePath, "installed conductor");
        Write(fixture.Path, edited.RelativePath, "operator reviewer edit");
        WriteState(fixture.Path, Lock(), receipt);
        SquadDeploymentPlan plan = SquadDeploymentPlan.CreateUninstall(
            fixture.Path,
            SquadDeploymentScope.Project,
            receipt);

        Transaction(fixture.Path).Execute(plan);

        Assert.False(File.Exists(Path.Combine(fixture.Path, unchanged.RelativePath)));
        Assert.Equal("operator reviewer edit", Read(fixture.Path, edited.RelativePath));
        SquadReceipt? retainedReceipt = Store(fixture.Path).ReadReceipt(
            fixture.Path,
            SquadDeploymentScope.Project);
        SquadOwnedFile retained = Assert.Single(Assert.IsType<SquadReceipt>(retainedReceipt).Files);
        Assert.Equal(edited, retained);
    }

    [Fact]
    public void ExecuteUninstallFileChangedAfterExactHashPreflightRefusesToDeleteRacedEdit()
    {
        using TempDirectory fixture = new TempDirectory();
        const string relativePath = ".codex/agents/conductor.toml";
        SquadReceipt receipt = Receipt(new SquadOwnedFile(
            relativePath,
            Digest("installed body"),
            "codex",
            false));
        Write(fixture.Path, relativePath, "installed body");
        WriteState(fixture.Path, Lock(), receipt);
        SquadDeploymentPlan plan = SquadDeploymentPlan.CreateUninstall(
            fixture.Path,
            SquadDeploymentScope.Project,
            receipt);
        Write(fixture.Path, relativePath, "raced operator edit");
        SortedDictionary<string, TreeEntry> before = CaptureTree(fixture.Path);

        SquadDeploymentConflictException exception = Assert.Throws<SquadDeploymentConflictException>(() =>
            Transaction(fixture.Path).Execute(plan));

        Assert.Contains(relativePath, exception.Message, StringComparison.Ordinal);
        Assert.Equal("raced operator edit", Read(fixture.Path, relativePath));
        AssertTreesEqual(before, CaptureTree(fixture.Path));
    }

    [Theory]
    [InlineData("../outside.md")]
    [InlineData(".codex/../../outside.md")]
    public void CreateInstallLexicalPathEscapeIsRejectedBeforeWrites(string relativePath)
    {
        using TempDirectory fixture = new TempDirectory();

        SquadPathContainmentException exception = Assert.Throws<SquadPathContainmentException>(() =>
            SquadDeploymentPlan.CreateInstall(
                fixture.Path,
                SquadDeploymentScope.Project,
                Lock(),
                [Rendered(relativePath, "escape")],
                [],
                adopt: false,
                new FixedTimeProvider(InstalledAt)));

        Assert.Contains(relativePath, exception.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.Path));
    }

    [Fact]
    public void CreateInstallPathThatResolvesThroughSymlinkOutsideRootIsRejected()
    {
        using TempDirectory fixture = new TempDirectory();
        using TempDirectory outside = new TempDirectory();
        string codex = Path.Combine(fixture.Path, ".codex");
        Directory.CreateDirectory(codex);
        Directory.CreateSymbolicLink(Path.Combine(codex, "agents"), outside.Path);

        SquadPathContainmentException exception = Assert.Throws<SquadPathContainmentException>(() =>
            SquadDeploymentPlan.CreateInstall(
                fixture.Path,
                SquadDeploymentScope.Project,
                Lock(),
                [Rendered(".codex/agents/conductor.toml", "escape")],
                [],
                adopt: false,
                new FixedTimeProvider(InstalledAt)));

        Assert.Contains(
            ".codex/agents/conductor.toml",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside.Path));
    }

    [Fact]
    public void CreateUninstallReceiptPathThatResolvesOutsideRootIsRejected()
    {
        using TempDirectory fixture = new TempDirectory();
        using TempDirectory outside = new TempDirectory();
        string codex = Path.Combine(fixture.Path, ".codex");
        Directory.CreateDirectory(codex);
        Directory.CreateSymbolicLink(Path.Combine(codex, "agents"), outside.Path);
        SquadReceipt receipt = Receipt(
            new SquadOwnedFile(
                ".codex/agents/conductor.toml",
                Digest("outside"),
                "codex",
                false));

        SquadPathContainmentException exception = Assert.Throws<SquadPathContainmentException>(() =>
            SquadDeploymentPlan.CreateUninstall(
                fixture.Path,
                SquadDeploymentScope.Project,
                receipt));

        Assert.Contains(
            ".codex/agents/conductor.toml",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteActiveTransactionHoldsAtomicExclusiveLease()
    {
        using TransactionFixture fixture = TransactionFixture.Create();
        using BlockingObserver blocker = new BlockingObserver(SquadTransactionStepKind.IntentWritten);
        SquadTransaction transaction = Transaction(fixture.Path, blocker);
        SquadDeploymentPlan plan = fixture.CreateUpdatePlan();
        Task execution = Task.Run(() => transaction.Execute(plan));
        Assert.True(
            blocker.Reached.Wait(TimeSpan.FromSeconds(5)),
            "The transaction did not reach its durable intent checkpoint.");

        bool heldExclusiveLease;
        try
        {
            string stateDirectory = Store(fixture.Path).ResolveStateDirectory(
                fixture.Path,
                SquadDeploymentScope.Project);
            heldExclusiveLease = Directory.EnumerateFiles(
                    stateDirectory,
                    "*",
                    SearchOption.AllDirectories)
                .Any(IsHeldWithExclusiveFileShare);
        }
        finally
        {
            blocker.Release.Set();
        }

        await execution;
        Assert.True(
            heldExclusiveLease,
            "An active transaction must own an atomically acquired exclusive lease, not rely on a check-then-create directory race.");
    }

    [Fact]
    public async Task RecoverWhileExecuteOwnsLeaseRefusesToInterleaveWithActiveTransaction()
    {
        using TransactionFixture fixture = TransactionFixture.Create();
        string fixturePath = fixture.Path;
        using BlockingObserver blocker = new BlockingObserver(SquadTransactionStepKind.IntentWritten);
        SquadTransaction activeTransaction = Transaction(fixturePath, blocker);
        SquadDeploymentPlan plan = fixture.CreateUpdatePlan();
        Task execution = Task.Run(() => activeTransaction.Execute(plan));
        Assert.True(
            blocker.Reached.Wait(TimeSpan.FromSeconds(5)),
            "The transaction did not reach its durable intent checkpoint.");

        Exception? recoveryException;
        try
        {
            SquadTransaction recoveryTransaction = Transaction(fixturePath);
            recoveryException = Record.Exception(() =>
                recoveryTransaction.Recover(
                    fixturePath,
                    SquadDeploymentScope.Project));
        }
        finally
        {
            blocker.Release.Set();
        }

        await execution;
        InvalidOperationException conflict = Assert.IsAssignableFrom<InvalidOperationException>(recoveryException);
        Assert.Contains("active", conflict.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("updated body", Read(fixturePath, fixture.RelativePath));
    }

    [Fact]
    public void ExecuteStagesBacksUpAndJournalsBeforeApplyingStateLast()
    {
        using TransactionFixture fixture = TransactionFixture.Create();
        RecordingObserver observer = new RecordingObserver(fixture.Path);
        SquadTransaction transaction = Transaction(fixture.Path, observer);

        transaction.Execute(fixture.CreateUpdatePlan());

        Assert.Equal(
            [
                SquadTransactionStepKind.IntentWritten,
                SquadTransactionStepKind.FileStaged,
                SquadTransactionStepKind.FileBackedUp,
                SquadTransactionStepKind.FileApplied,
                SquadTransactionStepKind.LockApplied,
                SquadTransactionStepKind.ReceiptApplied
            ],
            observer.Steps.Select(step => step.Kind));
        Assert.True(observer.SawIntentJournal);
        Assert.True(observer.SawStagedFile);
        Assert.True(observer.SawBackupFile);
        Assert.Equal("updated body", Read(fixture.Path, fixture.RelativePath));
        Assert.False(Directory.Exists(Path.Combine(
            fixture.Path,
            ".kyber-weave",
            ".squad-transaction")));
    }

    [Fact]
    public void ExecutePlainObserverReceivesOnlyLegacyLifecycleSteps()
    {
        using TransactionFixture fixture = TransactionFixture.Create();
        RecordingObserver observer = new RecordingObserver(fixture.Path);

        Transaction(fixture.Path, observer).Execute(fixture.CreateUpdatePlan());

        SquadTransactionStepKind[] expected =
        [
            SquadTransactionStepKind.IntentWritten,
            SquadTransactionStepKind.FileStaged,
            SquadTransactionStepKind.FileBackedUp,
            SquadTransactionStepKind.FileApplied,
            SquadTransactionStepKind.LockApplied,
            SquadTransactionStepKind.ReceiptApplied
        ];
        Assert.Equal(expected, Enum.GetValues<SquadTransactionStepKind>());
        Assert.Equal(expected, observer.Steps.Select(step => step.Kind));
        Assert.Equal(
            Enumerable.Range(1, expected.Length),
            observer.Steps.Select(step => step.Sequence));
    }

    [Fact]
    public void ExecuteCheckpointObserverOptsIntoPreparedAndTransitionEvents()
    {
        using TransactionFixture fixture = TransactionFixture.Create();
        CheckpointRecordingObserver observer = new CheckpointRecordingObserver();

        Transaction(fixture.Path, observer).Execute(fixture.CreateUpdatePlan());

        Assert.Equal(
            [
                SquadTransactionStepKind.IntentWritten,
                SquadTransactionStepKind.FileStaged,
                SquadTransactionStepKind.FileBackedUp,
                SquadTransactionStepKind.FileApplied,
                SquadTransactionStepKind.LockApplied,
                SquadTransactionStepKind.ReceiptApplied
            ],
            observer.Steps.Select(step => step.Kind));
        Assert.Equal(
            Enumerable.Range(1, 6),
            observer.Steps.Select(step => step.Sequence));
        Assert.Equal(
            [
                SquadTransactionCheckpointKind.Prepared,
                SquadTransactionCheckpointKind.ActiveTransitionWritten,
                SquadTransactionCheckpointKind.OriginalClaimed,
                SquadTransactionCheckpointKind.AfterImagePublished
            ],
            Enum.GetValues<SquadTransactionCheckpointKind>());
        Assert.Equal(
            Enumerable.Range(1, observer.Checkpoints.Count),
            observer.Checkpoints.Select(checkpoint => checkpoint.Sequence));
        Assert.Equal(
            new SquadTransactionCheckpoint(
                1,
                SquadTransactionCheckpointKind.Prepared),
            observer.Checkpoints[0]);
        Assert.Equal(
            [
                SquadTransactionCheckpointKind.ActiveTransitionWritten,
                SquadTransactionCheckpointKind.OriginalClaimed,
                SquadTransactionCheckpointKind.AfterImagePublished
            ],
            observer.Checkpoints
                .Where(checkpoint => string.Equals(
                    checkpoint.RelativePath,
                    fixture.RelativePath,
                    StringComparison.Ordinal))
                .Select(checkpoint => checkpoint.Kind));
        Assert.Equal(
            [
                SquadTransactionCheckpointKind.ActiveTransitionWritten,
                SquadTransactionCheckpointKind.OriginalClaimed,
                SquadTransactionCheckpointKind.AfterImagePublished
            ],
            observer.Checkpoints
                .Where(checkpoint => string.Equals(
                    checkpoint.RelativePath,
                    "lock",
                    StringComparison.Ordinal))
                .Select(checkpoint => checkpoint.Kind));
        Assert.Equal(
            [
                SquadTransactionCheckpointKind.ActiveTransitionWritten,
                SquadTransactionCheckpointKind.OriginalClaimed,
                SquadTransactionCheckpointKind.AfterImagePublished
            ],
            observer.Checkpoints
                .Where(checkpoint => string.Equals(
                    checkpoint.RelativePath,
                    "receipt",
                    StringComparison.Ordinal))
                .Select(checkpoint => checkpoint.Kind));
    }

    [Fact]
    public void ExecuteFailureAfterEveryFilesystemStepRestoresOriginalTreeAndRecoveryIsIdempotent()
    {
        using TransactionFixture probeFixture = TransactionFixture.Create();
        RecordingObserver probe = new RecordingObserver(probeFixture.Path);
        Transaction(probeFixture.Path, probe).Execute(probeFixture.CreateUpdatePlan());
        int stepCount = probe.Steps.Count;

        Assert.True(stepCount > 0);
        for (int failureSequence = 1; failureSequence <= stepCount; failureSequence++)
        {
            using TransactionFixture fixture = TransactionFixture.Create();
            SortedDictionary<string, byte[]> before = Snapshot(fixture.Path);
            FailingObserver failure = new FailingObserver(fixture.Path, failureSequence);
            SquadTransaction transaction = Transaction(
                fixture.Path,
                failure);

            InjectedSquadTransactionFailure exception = Assert.Throws<InjectedSquadTransactionFailure>(() =>
                transaction.Execute(fixture.CreateUpdatePlan()));

            Assert.Equal(failureSequence, exception.Sequence);
            AssertSnapshotsEqual(before, Snapshot(fixture.Path));

            // Rehydrate the exact tree visible at the injection boundary. This models a process
            // terminating before its catch block can run, while the first assertion above still
            // independently proves that an ordinary caught failure rolls back immediately.
            RestoreSnapshot(fixture.Path, Assert.IsType<SortedDictionary<string, byte[]>>(
                failure.InterruptedSnapshot));
            transaction.Recover(fixture.Path, SquadDeploymentScope.Project);
            SortedDictionary<string, byte[]> afterFirstRecovery = Snapshot(fixture.Path);
            transaction.Recover(fixture.Path, SquadDeploymentScope.Project);

            AssertSnapshotsEqual(before, afterFirstRecovery);
            AssertSnapshotsEqual(afterFirstRecovery, Snapshot(fixture.Path));
        }
    }

    [Fact]
    public void ExecuteFailureAfterEachFileMutationRestoresFilesDirectoriesSymlinksAndFreshParents()
    {
        using RichTransactionFixture probeFixture = RichTransactionFixture.Create();
        RecordingObserver probe = new RecordingObserver(probeFixture.Path);
        Transaction(probeFixture.Path, probe).Execute(probeFixture.CreateUpdatePlan());
        int stepCount = probe.Steps.Count;

        Assert.True(stepCount >= 10, "The fixture must exercise multiple stage, backup, apply, and state mutations.");
        for (int failureSequence = 1; failureSequence <= stepCount; failureSequence++)
        {
            using RichTransactionFixture fixture = RichTransactionFixture.Create();
            SortedDictionary<string, TreeEntry> before = CaptureTree(fixture.Path);
            FailingObserver failure = new FailingObserver(fixture.Path, failureSequence);
            SquadTransaction transaction = Transaction(fixture.Path, failure);

            InjectedSquadTransactionFailure exception = Assert.Throws<InjectedSquadTransactionFailure>(() =>
                transaction.Execute(fixture.CreateUpdatePlan()));

            Assert.Equal(failureSequence, exception.Sequence);
            AssertTreesEqual(before, CaptureTree(fixture.Path));

            RestoreTree(
                fixture.Path,
                Assert.IsType<SortedDictionary<string, TreeEntry>>(
                    failure.InterruptedTreeSnapshot));
            transaction.Recover(fixture.Path, SquadDeploymentScope.Project);
            SortedDictionary<string, TreeEntry> afterFirstRecovery = CaptureTree(fixture.Path);
            transaction.Recover(fixture.Path, SquadDeploymentScope.Project);

            AssertTreesEqual(before, afterFirstRecovery);
            AssertTreesEqual(afterFirstRecovery, CaptureTree(fixture.Path));
        }
    }

    [Theory]
    [InlineData(SquadTransactionStepKind.FileApplied)]
    [InlineData(SquadTransactionStepKind.LockApplied)]
    [InlineData(SquadTransactionStepKind.ReceiptApplied)]
    public void ExecutePostIntentExternalReplacementPreservesRaceAndRetainsRecoverableConflict(
        SquadTransactionStepKind replacementCheckpoint)
    {
        using TransactionFixture fixture = TransactionFixture.Create();
        string fixturePath = fixture.Path;
        SquadStateStore store = Store(fixturePath);
        PostIntentReplacementObserver observer = new PostIntentReplacementObserver(
            fixturePath,
            store,
            SquadDeploymentScope.Project,
            fixture.RelativePath,
            replacementCheckpoint);
        SquadTransaction transaction = new SquadTransaction(store, observer);
        SquadDeploymentPlan updatePlan = fixture.CreateUpdatePlan();

        Exception? exception = Record.Exception(() => transaction.Execute(updatePlan));

        SquadDeploymentConflictException conflict = Assert.IsAssignableFrom<SquadDeploymentConflictException>(exception);
        Assert.Contains("changed", conflict.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(observer.ExternalBytes, File.ReadAllBytes(observer.ReplacedPath));
        if (replacementCheckpoint != SquadTransactionStepKind.FileApplied)
            Assert.Equal("installed body", Read(fixturePath, fixture.RelativePath));
        if (replacementCheckpoint != SquadTransactionStepKind.LockApplied)
            AssertLockEqual(Lock(), Assert.IsType<SquadLock>(store.ReadLock(
                fixturePath,
                SquadDeploymentScope.Project)));
        if (replacementCheckpoint != SquadTransactionStepKind.ReceiptApplied)
            AssertReceiptEqual(fixture.Receipt, Assert.IsType<SquadReceipt>(store.ReadReceipt(
                fixturePath,
                SquadDeploymentScope.Project)));
        Assert.True(
            Directory.Exists(store.ResolveTransactionDirectory(
                fixturePath,
                SquadDeploymentScope.Project)),
            "A compare-and-restore conflict must retain its verified journal and backup authority.");

        SortedDictionary<string, TreeEntry> beforeRecovery = CaptureTree(fixturePath);
        Exception? recoveryException = Record.Exception(() => transaction.Recover(
            fixturePath,
            SquadDeploymentScope.Project));

        Assert.IsAssignableFrom<SquadDeploymentConflictException>(recoveryException);
        AssertTreesEqual(beforeRecovery, CaptureTree(fixturePath));
        Assert.Equal(observer.ExternalBytes, File.ReadAllBytes(observer.ReplacedPath));

        Exception? repeatedRecoveryException = Record.Exception(() => transaction.Recover(
            fixturePath,
            SquadDeploymentScope.Project));
        Assert.IsAssignableFrom<SquadDeploymentConflictException>(repeatedRecoveryException);
        AssertTreesEqual(beforeRecovery, CaptureTree(fixturePath));
    }

    [Fact]
    public void ExecutePostIntentExternalChildInTransactionCreatedParentPreservesParentAndReportsConflict()
    {
        using TempDirectory fixture = new TempDirectory();
        string fixturePath = fixture.Path;
        const string generatedPath = ".warp/roles/reviewer.md";
        const string externalPath = ".warp/roles/operator-note.md";
        SquadDeploymentPlan plan = SquadDeploymentPlan.CreateInstall(
            fixturePath,
            SquadDeploymentScope.Project,
            Lock(),
            [Rendered(generatedPath, "generated reviewer", "warp")],
            [],
            adopt: false,
            new FixedTimeProvider(InstalledAt));
        ExternalChildObserver observer = new ExternalChildObserver(fixturePath, generatedPath, externalPath);
        SquadTransaction transaction = Transaction(fixturePath, observer);

        Exception? exception = Record.Exception(() =>
            transaction.Execute(plan));

        Assert.IsAssignableFrom<SquadDeploymentConflictException>(exception);
        Assert.False(File.Exists(ToPlatformPath(fixturePath, generatedPath)));
        Assert.Equal("external operator note", Read(fixturePath, externalPath));
        Assert.True(Directory.Exists(ToPlatformPath(fixturePath, ".warp/roles")));
        Assert.True(Directory.Exists(Path.Combine(
            fixturePath,
            ".kyber-weave",
            ".squad-transaction")));
    }

    [Fact]
    public void ExecutePreparedArtifactsAreAtomicAndDurableBeforeFirstTargetMutation()
    {
        using TransactionFixture fixture = TransactionFixture.Create();
        PreparedArtifactsObserver observer = new PreparedArtifactsObserver(
            fixture.Path,
            fixture.RelativePath);

        Transaction(fixture.Path, observer).Execute(fixture.CreateUpdatePlan());

        Assert.True(observer.ObservedPreparedCheckpoint);
    }

    [Theory]
    [InlineData("truncated-journal")]
    [InlineData("duplicate-journal-field")]
    [InlineData("partial-backup")]
    [InlineData("wrong-backup-digest")]
    public void RecoverTruncatedOrDigestMismatchedPreparedArtifactRefusesAuthority(
        string corruption)
    {
        using TransactionFixture fixture = TransactionFixture.Create();
        string fixturePath = fixture.Path;
        SortedDictionary<string, TreeEntry> interrupted = InterruptTransactionAfterCheckpoint(
            fixture,
            SquadTransactionCheckpointKind.Prepared);
        RestoreTree(fixturePath, interrupted);
        CorruptPreparedArtifact(fixturePath, fixture.RelativePath, corruption);
        SortedDictionary<string, TreeEntry> beforeRecovery = CaptureTree(fixturePath);
        SquadTransaction transaction = Transaction(fixturePath);

        Exception? exception = Record.Exception(() => transaction.Recover(
            fixturePath,
            SquadDeploymentScope.Project));

        InvalidDataException invalid = Assert.IsType<InvalidDataException>(exception);
        Assert.Contains("Squad", invalid.Message, StringComparison.OrdinalIgnoreCase);
        AssertTreesEqual(beforeRecovery, CaptureTree(fixturePath));
        Assert.True(File.Exists(Path.Combine(
            fixturePath,
            ".kyber-weave",
            ".squad-transaction",
            "intent.json")));
    }

    [Theory]
    [InlineData("journal")]
    [InlineData("staging")]
    [InlineData("backup")]
    public void RecoverInterruptedInsideArtifactWriteRefusesPartialAuthority(string artifact)
    {
        using TransactionFixture fixture = TransactionFixture.Create();
        string fixturePath = fixture.Path;
        SortedDictionary<string, TreeEntry> interrupted = InterruptTransactionAfter(
            fixture,
            SquadTransactionStepKind.FileBackedUp);
        RestoreTree(fixturePath, interrupted);
        ReplacePublishedArtifactWithPartialTemporaryFile(
            fixturePath,
            fixture.RelativePath,
            artifact);
        SortedDictionary<string, TreeEntry> beforeRecovery = CaptureTree(fixturePath);
        SquadTransaction transaction = Transaction(fixturePath);

        Exception? exception = Record.Exception(() => transaction.Recover(
            fixturePath,
            SquadDeploymentScope.Project));

        InvalidDataException invalid = Assert.IsType<InvalidDataException>(exception);
        Assert.Contains("Squad", invalid.Message, StringComparison.OrdinalIgnoreCase);
        AssertTreesEqual(beforeRecovery, CaptureTree(fixturePath));
    }

    [Theory]
    [InlineData(SquadDeploymentScope.Project, SquadDeploymentScope.Global, false)]
    [InlineData(SquadDeploymentScope.Global, SquadDeploymentScope.Project, false)]
    [InlineData(SquadDeploymentScope.Project, SquadDeploymentScope.Global, true)]
    [InlineData(SquadDeploymentScope.Global, SquadDeploymentScope.Project, true)]
    public async Task ExecuteProjectAndGlobalSamePhysicalRootContendForOneLease(
        SquadDeploymentScope activeScope,
        SquadDeploymentScope contenderScope,
        bool useSymlinkAlias)
    {
        using TempDirectory fixture = new TempDirectory();
        string targetRoot = Path.Combine(fixture.Path, "target");
        string aliasRoot = Path.Combine(fixture.Path, "target-alias");
        string applicationData = Path.Combine(fixture.Path, "application-data");
        Directory.CreateDirectory(targetRoot);
        Directory.CreateSymbolicLink(aliasRoot, targetRoot);
        SquadStateStore store = Store(applicationData);
        SquadDeploymentPlan activePlan = SquadDeploymentPlan.CreateInstall(
            targetRoot,
            activeScope,
            Lock(),
            [Rendered(".codex/agents/active.toml", "active")],
            [],
            adopt: false,
            new FixedTimeProvider(InstalledAt));
        string contenderRoot = useSymlinkAlias ? aliasRoot : targetRoot;
        SquadDeploymentPlan contenderPlan = SquadDeploymentPlan.CreateInstall(
            contenderRoot,
            contenderScope,
            Lock(),
            [Rendered(".warp/roles/contender.md", "contender", "warp")],
            [],
            adopt: false,
            new FixedTimeProvider(InstalledAt));
        using BlockingObserver blocker = new BlockingObserver(SquadTransactionStepKind.IntentWritten);
        SquadTransaction activeTransaction = new SquadTransaction(store, blocker);
        Task activeExecution = Task.Run(() =>
            activeTransaction.Execute(activePlan));
        Assert.True(
            blocker.Reached.Wait(TimeSpan.FromSeconds(5)),
            "The active transaction did not acquire its lease before contention was tested.");
        SortedDictionary<string, TreeEntry> beforeContention = CaptureTree(fixture.Path);

        Exception? recoveryException;
        Exception? executeException;
        IReadOnlyDictionary<string, TreeEntry>? afterContention;
        try
        {
            SquadTransaction storeTransaction = new SquadTransaction(store);
            recoveryException = Record.Exception(() => storeTransaction.Recover(
                contenderRoot,
                contenderScope));
            executeException = Record.Exception(() =>
                storeTransaction.Execute(contenderPlan));
            afterContention = CaptureTree(fixture.Path);
        }
        finally
        {
            blocker.Release.Set();
        }

        await activeExecution;
        AssertActiveLeaseConflict(recoveryException);
        AssertActiveLeaseConflict(executeException);
        AssertTreesEqual(
            beforeContention,
            Assert.IsAssignableFrom<IReadOnlyDictionary<string, TreeEntry>>(afterContention));
        Assert.False(File.Exists(ToPlatformPath(
            targetRoot,
            ".warp/roles/contender.md")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecoverFailedFirstGlobalInstallRestoresApplicationDataTopology(
        bool preserveSharedApplicationData)
    {
        int probeStepCount = ProbeGlobalInstallStepCount();

        for (int failureSequence = 1; failureSequence <= probeStepCount; failureSequence++)
        {
            using TempDirectory fixture = new TempDirectory();
            string targetRoot = Path.Combine(fixture.Path, "target");
            string applicationData = Path.Combine(fixture.Path, "application-data");
            Directory.CreateDirectory(targetRoot);
            if (preserveSharedApplicationData)
            {
                Write(applicationData, "KyberWeave/shared.keep", "shared parent");
                Write(applicationData, "KyberWeave/squad/shared.keep", "shared squad");
            }

            SquadStateStore store = Store(applicationData);
            SquadDeploymentPlan plan = CreateGlobalInstallPlan(targetRoot);
            SortedDictionary<string, TreeEntry> before = CaptureTree(fixture.Path);
            FailingObserver failure = new FailingObserver(fixture.Path, failureSequence);
            SquadTransaction transaction = new SquadTransaction(store, failure);

            Assert.Throws<InjectedSquadTransactionFailure>(() => transaction.Execute(plan));
            AssertTreesEqual(before, CaptureTree(fixture.Path));

            RestoreTree(
                fixture.Path,
                Assert.IsType<SortedDictionary<string, TreeEntry>>(
                    failure.InterruptedTreeSnapshot));
            transaction.Recover(targetRoot, SquadDeploymentScope.Global);
            SortedDictionary<string, TreeEntry> recovered = CaptureTree(fixture.Path);
            transaction.Recover(targetRoot, SquadDeploymentScope.Global);

            AssertTreesEqual(before, recovered);
            AssertTreesEqual(recovered, CaptureTree(fixture.Path));
        }
    }

    [Fact]
    public void ResolveGlobalStateLexicalAliasesSharePhysicalRootBinding()
    {
        using TempDirectory fixture = new TempDirectory();
        string targetRoot = Path.Combine(fixture.Path, "target");
        string aliasRoot = Path.Combine(fixture.Path, "target-alias");
        Directory.CreateDirectory(targetRoot);
        Directory.CreateSymbolicLink(aliasRoot, targetRoot);
        SquadStateStore store = Store(Path.Combine(fixture.Path, "application-data"));

        Assert.Equal(
            store.ResolveLockPath(targetRoot, SquadDeploymentScope.Global),
            store.ResolveLockPath(aliasRoot, SquadDeploymentScope.Global));
        Assert.Equal(
            store.ResolveReceiptPath(targetRoot, SquadDeploymentScope.Global),
            store.ResolveReceiptPath(Path.Combine(targetRoot, "."), SquadDeploymentScope.Global));
        Assert.Equal(
            store.ResolveTransactionDirectory(targetRoot, SquadDeploymentScope.Global),
            store.ResolveTransactionDirectory(aliasRoot, SquadDeploymentScope.Global));
    }

    [Fact]
    public void ResolveGlobalStateCaseAliasesShareBindingOnCaseInsensitiveFilesystem()
    {
        using TempDirectory fixture = new TempDirectory();
        string targetRoot = Path.Combine(fixture.Path, "CaseSensitiveProbe");
        string caseAlias = Path.Combine(fixture.Path, "casesensitiveprobe");
        Directory.CreateDirectory(targetRoot);
        if (!Directory.Exists(caseAlias))
            return;

        SquadStateStore store = Store(Path.Combine(fixture.Path, "application-data"));

        Assert.Equal(
            store.ResolveReceiptPath(targetRoot, SquadDeploymentScope.Global),
            store.ResolveReceiptPath(caseAlias, SquadDeploymentScope.Global));
    }

    [Fact]
    public async Task ResolvePhysicalRootCaseDistinctSiblingsRemainDistinctWhenSupported()
    {
        using TempDirectory fixture = new TempDirectory();
        string upperRoot = Path.Combine(fixture.Path, "Root");
        string lowerRoot = Path.Combine(fixture.Path, "root");
        string symlinkRoot = Path.Combine(fixture.Path, "root-link");
        string applicationData = Path.Combine(fixture.Path, "application-data");
        Directory.CreateDirectory(upperRoot);
        if (Directory.Exists(lowerRoot))
        {
            Assert.Equal(
                SquadPhysicalRootIdentity.Resolve(upperRoot),
                SquadPhysicalRootIdentity.Resolve(lowerRoot));
            return;
        }

        Directory.CreateDirectory(lowerRoot);
        Directory.CreateSymbolicLink(symlinkRoot, upperRoot);
        SquadPhysicalRootIdentity upperIdentity = SquadPhysicalRootIdentity.Resolve(upperRoot);
        SquadPhysicalRootIdentity lowerIdentity = SquadPhysicalRootIdentity.Resolve(lowerRoot);
        SquadPhysicalRootIdentity symlinkIdentity = SquadPhysicalRootIdentity.Resolve(symlinkRoot);
        SquadStateStore store = Store(applicationData);

        Assert.NotEqual(upperIdentity.PhysicalPath, lowerIdentity.PhysicalPath);
        Assert.NotEqual(upperIdentity.Key, lowerIdentity.Key);
        Assert.NotEqual(
            store.ResolveTransactionDirectory(upperRoot, SquadDeploymentScope.Global),
            store.ResolveTransactionDirectory(lowerRoot, SquadDeploymentScope.Global));
        Assert.Equal(upperIdentity, symlinkIdentity);

        using BlockingObserver blocker = new BlockingObserver(SquadTransactionStepKind.IntentWritten);
        SquadDeploymentPlan activePlan = SquadDeploymentPlan.CreateInstall(
            upperRoot,
            SquadDeploymentScope.Global,
            Lock(),
            [Rendered(".codex/agents/upper.toml", "upper")],
            [],
            adopt: false,
            new FixedTimeProvider(InstalledAt));
        SquadDeploymentPlan siblingPlan = SquadDeploymentPlan.CreateInstall(
            lowerRoot,
            SquadDeploymentScope.Global,
            Lock(),
            [Rendered(".codex/agents/lower.toml", "lower")],
            [],
            adopt: false,
            new FixedTimeProvider(InstalledAt));
        SquadTransaction activeTransaction = new SquadTransaction(store, blocker);
        Task activeExecution = Task.Run(() => activeTransaction.Execute(activePlan));
        Assert.True(
            blocker.Reached.Wait(TimeSpan.FromSeconds(5)),
            "The first case-distinct root did not acquire its physical-root lease.");

        Exception? siblingException;
        try
        {
            SquadTransaction siblingTransaction = new SquadTransaction(store);
            siblingException = Record.Exception(() => siblingTransaction.Execute(siblingPlan));
        }
        finally
        {
            blocker.Release.Set();
        }

        await activeExecution;
        Assert.Null(siblingException);
        Assert.Equal("upper", Read(upperRoot, ".codex/agents/upper.toml"));
        Assert.Equal("lower", Read(lowerRoot, ".codex/agents/lower.toml"));
    }

    [Fact]
    public async Task ResolvePhysicalRootCaseAliasesConvergeWhenFilesystemIsInsensitive()
    {
        using TempDirectory fixture = new TempDirectory();
        string actualRoot = Path.Combine(fixture.Path, "CaseProbe");
        string caseAlias = Path.Combine(fixture.Path, "caseprobe");
        string symlinkAlias = Path.Combine(fixture.Path, "case-probe-link");
        string applicationData = Path.Combine(fixture.Path, "application-data");
        Directory.CreateDirectory(actualRoot);
        Directory.CreateSymbolicLink(symlinkAlias, actualRoot);
        SquadPhysicalRootIdentity actualIdentity = SquadPhysicalRootIdentity.Resolve(actualRoot);

        Assert.Equal(actualIdentity, SquadPhysicalRootIdentity.Resolve(symlinkAlias));
        if (!Directory.Exists(caseAlias))
        {
            Assert.NotEqual(
                Path.GetFullPath(actualRoot),
                Path.GetFullPath(caseAlias));
            return;
        }

        SquadPhysicalRootIdentity aliasIdentity = SquadPhysicalRootIdentity.Resolve(caseAlias);
        SquadStateStore store = Store(applicationData);
        Assert.Equal(actualIdentity.PhysicalPath, aliasIdentity.PhysicalPath);
        Assert.Equal(actualIdentity.Key, aliasIdentity.Key);
        Assert.Equal(
            store.ResolveReceiptPath(actualRoot, SquadDeploymentScope.Global),
            store.ResolveReceiptPath(caseAlias, SquadDeploymentScope.Global));

        using BlockingObserver blocker = new BlockingObserver(SquadTransactionStepKind.IntentWritten);
        SquadDeploymentPlan activePlan = SquadDeploymentPlan.CreateInstall(
            actualRoot,
            SquadDeploymentScope.Project,
            Lock(),
            [Rendered(".codex/agents/actual.toml", "actual")],
            [],
            adopt: false,
            new FixedTimeProvider(InstalledAt));
        SquadDeploymentPlan aliasPlan = SquadDeploymentPlan.CreateInstall(
            caseAlias,
            SquadDeploymentScope.Global,
            Lock(),
            [Rendered(".warp/roles/alias.md", "alias", "warp")],
            [],
            adopt: false,
            new FixedTimeProvider(InstalledAt));
        SquadTransaction activeTransaction = new SquadTransaction(store, blocker);
        Task activeExecution = Task.Run(() => activeTransaction.Execute(activePlan));
        Assert.True(
            blocker.Reached.Wait(TimeSpan.FromSeconds(5)),
            "The physical-root alias did not acquire its shared lease.");

        Exception? aliasException;
        try
        {
            SquadTransaction aliasTransaction = new SquadTransaction(store);
            aliasException = Record.Exception(() => aliasTransaction.Execute(aliasPlan));
        }
        finally
        {
            blocker.Release.Set();
        }

        await activeExecution;
        AssertActiveLeaseConflict(aliasException);
    }

    [Fact]
    public void ExecuteRetargetedRootAliasFailsBeforeWrites()
    {
        using TempDirectory fixture = new TempDirectory();
        string firstRoot = Path.Combine(fixture.Path, "first");
        string secondRoot = Path.Combine(fixture.Path, "second");
        string aliasRoot = Path.Combine(fixture.Path, "root-alias");
        string applicationData = Path.Combine(fixture.Path, "application-data");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        Directory.CreateSymbolicLink(aliasRoot, firstRoot);
        SquadDeploymentPlan plan = SquadDeploymentPlan.CreateInstall(
            aliasRoot,
            SquadDeploymentScope.Global,
            Lock(),
            [Rendered(".codex/agents/conductor.toml", "generated")],
            [],
            adopt: false,
            new FixedTimeProvider(InstalledAt));
        Directory.Delete(aliasRoot);
        Directory.CreateSymbolicLink(aliasRoot, secondRoot);
        SortedDictionary<string, TreeEntry> before = CaptureTree(fixture.Path);

        Exception? exception = Record.Exception(() =>
            new SquadTransaction(Store(applicationData)).Execute(plan));

        SquadDeploymentConflictException conflict = Assert.IsAssignableFrom<SquadDeploymentConflictException>(exception);
        Assert.Contains("root", conflict.Message, StringComparison.OrdinalIgnoreCase);
        AssertTreesEqual(before, CaptureTree(fixture.Path));
    }

    [Theory]
    [InlineData("lock", "unknown")]
    [InlineData("lock", "duplicate")]
    [InlineData("lock", "missing")]
    [InlineData("lock", "null")]
    [InlineData("lock", "misspelled")]
    [InlineData("lock", "wrong-case")]
    [InlineData("lock", "wrong-type")]
    [InlineData("lock", "yaml-alias")]
    [InlineData("lock", "yaml-merge")]
    [InlineData("lock", "yaml-tag")]
    [InlineData("receipt", "unknown")]
    [InlineData("receipt", "duplicate")]
    [InlineData("receipt", "missing")]
    [InlineData("receipt", "null")]
    [InlineData("receipt", "misspelled")]
    [InlineData("receipt", "wrong-case")]
    [InlineData("receipt", "wrong-type")]
    [InlineData("receipt", "wrong-enum")]
    [InlineData("receipt", "invalid-timestamp")]
    [InlineData("receipt", "invalid-digest")]
    [InlineData("receipt", "portable-duplicate")]
    public void DeserializeStateUnknownDuplicateMissingNullOrMisspelledFieldsAreRejected(
        string documentKind,
        string corruption)
    {
        using TempDirectory fixture = new TempDirectory();
        SquadStateStore store = Store(fixture.Path);
        string document = documentKind == "lock"
            ? CorruptLockDocument(store.SerializeLock(Lock()), corruption)
            : CorruptReceiptDocument(store.SerializeReceipt(Receipt(
                new SquadOwnedFile(
                    ".codex/agents/conductor.toml",
                    Digest("conductor"),
                    "codex",
                    false))), corruption);

        Exception? exception = Record.Exception(() =>
        {
            if (documentKind == "lock")
                _ = store.DeserializeLock(document);
            else
                _ = store.DeserializeReceipt(document);
        });

        InvalidDataException invalid = Assert.IsType<InvalidDataException>(exception);
        Assert.Contains("Squad", invalid.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(invalid.Message));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("missing")]
    [InlineData("null")]
    public void RecoverInvalidPreparedJournalIsNonAuthoritative(string corruption)
    {
        using TransactionFixture fixture = TransactionFixture.Create();
        string fixturePath = fixture.Path;
        SortedDictionary<string, TreeEntry> interrupted = InterruptTransactionAfter(
            fixture,
            SquadTransactionStepKind.FileBackedUp);
        RestoreTree(fixturePath, interrupted);
        string intentPath = Path.Combine(
            fixturePath,
            ".kyber-weave",
            ".squad-transaction",
            "intent.json");
        string journal = File.ReadAllText(intentPath, Encoding.UTF8);
        File.WriteAllText(
            intentPath,
            CorruptJournalDocument(journal, corruption),
            new UTF8Encoding(false));
        SortedDictionary<string, TreeEntry> beforeRecovery = CaptureTree(fixturePath);
        SquadTransaction transaction = Transaction(fixturePath);

        Exception? exception = Record.Exception(() => transaction.Recover(
            fixturePath,
            SquadDeploymentScope.Project));

        InvalidDataException invalid = Assert.IsType<InvalidDataException>(exception);
        Assert.Contains("Squad", invalid.Message, StringComparison.OrdinalIgnoreCase);
        AssertTreesEqual(beforeRecovery, CaptureTree(fixturePath));
    }

    [Theory]
    [MemberData(nameof(PortableInvalidPaths))]
    public void CreateLifecyclePlanWindowsForbiddenDeviceControlAndUnicodeAliasesAreRejected(
        string invalidPath)
    {
        using TempDirectory fixture = new TempDirectory();
        string fixturePath = fixture.Path;
        SquadOwnedFile owned = new SquadOwnedFile(invalidPath, Digest("owned"), "codex", false);
        SquadReceipt receipt = Receipt(owned);

        Assert.IsAssignableFrom<InvalidOperationException>(Record.Exception(() =>
            SquadDeploymentPlan.CreateInstall(
                fixturePath,
                SquadDeploymentScope.Project,
                Lock(),
                [Rendered(invalidPath, "generated")],
                [],
                adopt: false,
                new FixedTimeProvider(InstalledAt))));
        Assert.IsAssignableFrom<InvalidOperationException>(Record.Exception(() =>
            SquadDeploymentPlan.CreateUpdate(
                fixturePath,
                SquadDeploymentScope.Project,
                Lock("1.2.4"),
                [Rendered(invalidPath, "updated")],
                receipt,
                [],
                replaceManaged: false,
                new FixedTimeProvider(InstalledAt.AddDays(1)))));
        Assert.IsAssignableFrom<InvalidOperationException>(Record.Exception(() =>
            SquadDeploymentPlan.CreateUninstall(
                fixturePath,
                SquadDeploymentScope.Project,
                receipt)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixturePath));
    }

    [Theory]
    [InlineData(".codex/agents/\u00e9.md", ".codex/agents/e\u0301.md")]
    [InlineData(".codex/agents/Reviewer.md", ".codex/agents/reviewer.md")]
    public void CreateLifecyclePlanUnicodeAndCaseOnlyPortablePairsAreRejected(
        string firstPath,
        string aliasPath)
    {
        using TempDirectory fixture = new TempDirectory();
        string fixturePath = fixture.Path;
        SquadReceipt receipt = Receipt(
            new SquadOwnedFile(firstPath, Digest("first"), "codex", false),
            new SquadOwnedFile(aliasPath, Digest("alias"), "codex", false));

        Assert.IsAssignableFrom<InvalidOperationException>(Record.Exception(() =>
            SquadDeploymentPlan.CreateInstall(
                fixturePath,
                SquadDeploymentScope.Project,
                Lock(),
                [Rendered(firstPath, "first"), Rendered(aliasPath, "alias")],
                [],
                adopt: false,
                new FixedTimeProvider(InstalledAt))));
        Assert.IsAssignableFrom<InvalidOperationException>(Record.Exception(() =>
            SquadDeploymentPlan.CreateUpdate(
                fixturePath,
                SquadDeploymentScope.Project,
                Lock("1.2.4"),
                [Rendered(firstPath, "first"), Rendered(aliasPath, "alias")],
                receipt,
                [],
                replaceManaged: false,
                new FixedTimeProvider(InstalledAt.AddDays(1)))));
        Assert.IsAssignableFrom<InvalidOperationException>(Record.Exception(() =>
            SquadDeploymentPlan.CreateUninstall(
                fixturePath,
                SquadDeploymentScope.Project,
                receipt)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixturePath));
    }

    [Theory]
    [InlineData("target-file")]
    [InlineData("lock-state")]
    [InlineData("receipt-state")]
    public void ExecuteStagedArtifactChangedAfterPreparedBeforeApplyIsRejected(string artifact)
    {
        using TempDirectory fixture = new TempDirectory();
        const string relativePath = ".codex/agents/conductor.toml";
        SquadStateStore store = Store(fixture.Path);
        SquadDeploymentPlan plan = SquadDeploymentPlan.CreateInstall(
            fixture.Path,
            SquadDeploymentScope.Project,
            Lock(),
            [Rendered(relativePath, "generated conductor")],
            [],
            adopt: false,
            new FixedTimeProvider(InstalledAt));
        StagedArtifactTamperingObserver observer = new StagedArtifactTamperingObserver(
            fixture.Path,
            relativePath,
            artifact);

        Exception? exception = Record.Exception(() => new SquadTransaction(store, observer).Execute(plan));

        InvalidDataException invalid = Assert.IsType<InvalidDataException>(exception);
        string reportedArtifact = artifact == "target-file"
            ? relativePath
            : artifact.Split('-')[0];
        Assert.Contains(reportedArtifact, invalid.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(ToPlatformPath(fixture.Path, relativePath)));
        Assert.False(File.Exists(store.ResolveLockPath(fixture.Path, SquadDeploymentScope.Project)));
        Assert.False(File.Exists(store.ResolveReceiptPath(fixture.Path, SquadDeploymentScope.Project)));
        Assert.False(observer.SawAffectedApplyCheckpoint);
        Assert.True(
            Directory.Exists(store.ResolveTransactionDirectory(
                fixture.Path,
                SquadDeploymentScope.Project)),
            "A changed prepared stage is non-authoritative evidence and must be retained.");
    }

    [Theory]
    [InlineData("missing-write", "file")]
    [InlineData("missing-write", "directory")]
    [InlineData("missing-write", "link")]
    [InlineData("existing-write", "file")]
    [InlineData("existing-write", "directory")]
    [InlineData("existing-write", "link")]
    [InlineData("existing-delete", "file")]
    [InlineData("existing-delete", "directory")]
    [InlineData("existing-delete", "link")]
    public void LeafClaimChangedPreconditionIsNeverOverwrittenOrDeleted(
        string operation,
        string racedNodeKind)
    {
        using TempDirectory fixture = new TempDirectory();
        string fixturePath = fixture.Path;
        const string relativePath = ".codex/agents/conductor.toml";
        SquadDeploymentPlan plan;
        if (operation == "missing-write")
        {
            plan = SquadDeploymentPlan.CreateInstall(
                fixturePath,
                SquadDeploymentScope.Project,
                Lock(),
                [Rendered(relativePath, "generated")],
                [],
                adopt: false,
                new FixedTimeProvider(InstalledAt));
        }
        else
        {
            Write(fixturePath, relativePath, "installed");
            SquadReceipt receipt = Receipt(new SquadOwnedFile(
                relativePath,
                Digest("installed"),
                "codex",
                false));
            WriteState(fixturePath, Lock(), receipt);
            plan = operation == "existing-write"
                ? SquadDeploymentPlan.CreateUpdate(
                    fixturePath,
                    SquadDeploymentScope.Project,
                    Lock("1.2.4"),
                    [Rendered(relativePath, "updated")],
                    receipt,
                    [],
                    replaceManaged: false,
                    new FixedTimeProvider(InstalledAt.AddDays(1)))
                : SquadDeploymentPlan.CreateUninstall(
                    fixturePath,
                    SquadDeploymentScope.Project,
                    receipt);
        }

        LeafRaceObserver observer = new LeafRaceObserver(fixturePath, relativePath, racedNodeKind);
        SquadTransaction transaction = Transaction(fixturePath, observer);

        Exception? exception = Record.Exception(() => transaction.Execute(plan));

        InvalidOperationException conflict = Assert.IsAssignableFrom<InvalidOperationException>(exception);
        Assert.Contains(Path.GetFileName(relativePath), conflict.Message, StringComparison.Ordinal);
        AssertTreeEntryEqual(
            Assert.IsType<TreeEntry>(observer.RacedEntry),
            CaptureTree(fixturePath)[relativePath]);
        Assert.True(File.Exists(observer.ExternalCanaryPath));
        Assert.True(Directory.Exists(Path.Combine(
            fixturePath,
            ".kyber-weave",
            ".squad-transaction")));
    }

    [Fact]
    public void ExecuteClaimPublicationUsesNoOverwrite()
    {
        string sourcePath = Path.Combine(
            KyberWeaveTestPaths.ToolRoot,
            "src",
            "KyberWeave.Core",
            "Squad",
            "Deployment",
            "SquadTransaction.cs");
        string source = File.ReadAllText(sourcePath, Encoding.UTF8);
        int applyStart = source.IndexOf("private void ApplyFiles", StringComparison.Ordinal);
        int restoreStart = source.IndexOf("private static IReadOnlyList<string> RestoreIntent", StringComparison.Ordinal);
        Assert.True(applyStart >= 0 && restoreStart > applyStart);
        string liveApplySource = source[applyStart..restoreStart];

        Assert.Contains("ClaimExistingNoOverwrite", liveApplySource, StringComparison.Ordinal);
        Assert.Contains("PublishStageNoOverwrite", liveApplySource, StringComparison.Ordinal);
        Assert.Contains("VerifyAfterImage", liveApplySource, StringComparison.Ordinal);
        Assert.DoesNotContain("overwrite: true", liveApplySource, StringComparison.Ordinal);
        Assert.DoesNotContain("DeleteEntry(targetPath", liveApplySource, StringComparison.Ordinal);
        Assert.DoesNotContain("Notify(", ExtractMethod(source, "ClaimExistingNoOverwrite"), StringComparison.Ordinal);
        Assert.DoesNotContain("Notify(", ExtractMethod(source, "PublishStageNoOverwrite"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing-required")]
    [InlineData("undeclared-file")]
    [InlineData("undeclared-link")]
    [InlineData("undeclared-directory")]
    [InlineData("duplicate-identity")]
    [InlineData("swapped-area")]
    [InlineData("swapped-role")]
    [InlineData("swapped-lifecycle")]
    [InlineData("invalid-transition")]
    [InlineData("well-digested-unexpected")]
    public void RecoverPreparedArtifactAuthorityMustExactlyDescribeTransactionTree(
        string corruption)
    {
        using TransactionFixture fixture = TransactionFixture.Create();
        string fixturePath = fixture.Path;
        SortedDictionary<string, TreeEntry> interrupted = InterruptTransactionAfterCheckpoint(
            fixture,
            SquadTransactionCheckpointKind.Prepared);
        RestoreTree(fixturePath, interrupted);
        string transactionDirectory = Path.Combine(
            fixturePath,
            ".kyber-weave",
            ".squad-transaction");
        string intentPath = Path.Combine(transactionDirectory, "intent.json");
        JsonObject intent = ReadJsonObject(intentPath);
        JsonArray artifacts = Assert.IsType<JsonArray>(intent["artifacts"]);
        Assert.NotEmpty(artifacts);
        AssertPreparedArtifactAuthority(artifacts);
        CorruptPreparedAuthority(intent, transactionDirectory, corruption);
        WriteJsonObject(intentPath, intent);
        SortedDictionary<string, TreeEntry> beforeRecovery = CaptureTree(fixturePath);
        SquadTransaction transaction = Transaction(fixturePath);

        Exception? exception = Record.Exception(() => transaction.Recover(
            fixturePath,
            SquadDeploymentScope.Project));

        InvalidDataException invalid = Assert.IsType<InvalidDataException>(exception);
        Assert.Contains("Squad", invalid.Message, StringComparison.OrdinalIgnoreCase);
        AssertTreesEqual(beforeRecovery, CaptureTree(fixturePath));
    }

    [Theory]
    [InlineData("wrong-area")]
    [InlineData("non-ancestor")]
    [InlineData("duplicate")]
    [InlineData("unordered")]
    [InlineData("absolute")]
    [InlineData("traversal")]
    [InlineData("sibling")]
    [InlineData("outside-root")]
    [InlineData("legacy-count")]
    [InlineData("negative-count")]
    [InlineData("maximum-count")]
    public void RecoverCorruptCreatedDirectoryAuthorityCannotEscapeBoundRoots(string corruption)
    {
        using TempDirectory fixture = new TempDirectory();
        string targetRoot = Path.Combine(fixture.Path, "target");
        string applicationData = Path.Combine(fixture.Path, "application-data");
        Directory.CreateDirectory(targetRoot);
        Write(fixture.Path, "target-canary.txt", "target boundary canary");
        Write(fixture.Path, "application-canary.txt", "application boundary canary");
        Write(fixture.Path, "beside-target/canary.txt", "sibling canary");
        SquadStateStore store = Store(applicationData);
        SquadDeploymentPlan plan = CreateGlobalInstallPlan(targetRoot);
        CheckpointFailingObserver observer = new CheckpointFailingObserver(
            fixture.Path,
            SquadTransactionCheckpointKind.Prepared);
        Assert.Throws<InjectedSquadTransactionFailure>(() =>
            new SquadTransaction(store, observer).Execute(plan));
        RestoreTree(
            fixture.Path,
            Assert.IsType<SortedDictionary<string, TreeEntry>>(
                observer.InterruptedTreeSnapshot));
        string intentPath = Path.Combine(
            store.ResolveTransactionDirectory(targetRoot, SquadDeploymentScope.Global),
            "intent.json");
        JsonObject intent = ReadJsonObject(intentPath);
        Assert.False(intent.ContainsKey("missingJournalDirectoryCount"));
        Assert.False(intent.ContainsKey("missingTransactionDirectories"));
        JsonArray createdDirectories = Assert.IsType<JsonArray>(intent["createdDirectories"]);
        Assert.NotEmpty(createdDirectories);
        AssertCreatedDirectoryAuthorityIsCanonical(createdDirectories);
        CorruptCreatedDirectoryAuthority(intent, createdDirectories, fixture.Path, corruption);
        WriteJsonObject(intentPath, intent);
        SortedDictionary<string, TreeEntry> beforeRecovery = CaptureTree(fixture.Path);

        Exception? exception = Record.Exception(() => new SquadTransaction(store).Recover(
            targetRoot,
            SquadDeploymentScope.Global));

        InvalidDataException invalid = Assert.IsType<InvalidDataException>(exception);
        Assert.Contains("Squad", invalid.Message, StringComparison.OrdinalIgnoreCase);
        AssertTreesEqual(beforeRecovery, CaptureTree(fixture.Path));
        Assert.Equal("target boundary canary", Read(fixture.Path, "target-canary.txt"));
        Assert.Equal("application boundary canary", Read(fixture.Path, "application-canary.txt"));
        Assert.Equal("sibling canary", Read(fixture.Path, "beside-target/canary.txt"));
    }

    [Fact]
    public void RecoverValidCreatedDirectoryAuthorityRemovesOnlyDeclaredEmptyDirectoriesDeepestFirst()
    {
        using TempDirectory fixture = new TempDirectory();
        string targetRoot = Path.Combine(fixture.Path, "target");
        string applicationData = Path.Combine(fixture.Path, "application-data");
        Directory.CreateDirectory(targetRoot);
        Write(fixture.Path, "beside-target/canary.txt", "sibling canary");
        Write(applicationData, "shared/canary.txt", "application canary");
        SortedDictionary<string, TreeEntry> before = CaptureTree(fixture.Path);
        SquadStateStore store = Store(applicationData);
        CheckpointFailingObserver observer = new CheckpointFailingObserver(
            fixture.Path,
            SquadTransactionCheckpointKind.Prepared);
        Assert.Throws<InjectedSquadTransactionFailure>(() =>
            new SquadTransaction(store, observer).Execute(CreateGlobalInstallPlan(targetRoot)));
        RestoreTree(
            fixture.Path,
            Assert.IsType<SortedDictionary<string, TreeEntry>>(
                observer.InterruptedTreeSnapshot));
        string intentPath = Path.Combine(
            store.ResolveTransactionDirectory(targetRoot, SquadDeploymentScope.Global),
            "intent.json");
        JsonObject intent = ReadJsonObject(intentPath);
        Assert.False(intent.ContainsKey("missingJournalDirectoryCount"));
        JsonArray createdDirectories = Assert.IsType<JsonArray>(intent["createdDirectories"]);
        AssertCreatedDirectoryAuthorityIsCanonical(createdDirectories);

        new SquadTransaction(store).Recover(targetRoot, SquadDeploymentScope.Global);

        AssertTreesEqual(before, CaptureTree(fixture.Path));
    }

    [Fact]
    public void DeserializeAuthorityIntegerEnumTokensAreRejected()
    {
        using TransactionFixture fixture = TransactionFixture.Create();
        SquadStateStore store = Store(fixture.Path);
        List<string> failures = new List<string>();
        string receiptJson = store.SerializeReceipt(fixture.Receipt);
        foreach (int numericValue in new[] { 0, 1, int.MaxValue })
        {
            string numericReceipt = ReplaceJsonStringPropertyWithNumber(
                receiptJson,
                "scope",
                numericValue,
                out int replacements);
            Assert.Equal(1, replacements);
            if (Record.Exception(() => store.DeserializeReceipt(numericReceipt)) is not InvalidDataException)
                failures.Add($"receipt.scope={numericValue}");
        }

        SortedDictionary<string, TreeEntry> interrupted = InterruptTransactionAfterCheckpoint(
            fixture,
            SquadTransactionCheckpointKind.Prepared);
        string fixturePath = fixture.Path;
        SquadTransaction transaction = Transaction(fixturePath);
        Dictionary<string, IReadOnlyList<int>> journalCases = new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal)
        {
            ["phase"] = [0, 1, int.MaxValue],
            ["originalKind"] = [0, 1, 2, 3, 4, int.MaxValue],
            ["afterKind"] = [0, 1, 2, 3, 4, int.MaxValue],
            ["lockKind"] = [0, 1, 2, 3, 4, int.MaxValue],
            ["lockAfterKind"] = [0, 1, 2, 3, 4, int.MaxValue],
            ["receiptKind"] = [0, 1, 2, 3, 4, int.MaxValue],
            ["receiptAfterKind"] = [0, 1, 2, 3, 4, int.MaxValue],
            ["area"] = [0, 1, 2, int.MaxValue],
            ["role"] = [0, 1, 2, 3, 4, 5, 6, 7, int.MaxValue],
            ["nodeKind"] = [0, 1, 2, 3, 4, int.MaxValue],
            ["lifecycleState"] = [0, 1, 2, int.MaxValue]
        };
        foreach ((string? property, IReadOnlyList<int>? numericValues) in journalCases)
        {
            foreach (int numericValue in numericValues)
            {
                RestoreTree(fixturePath, interrupted);
                string intentPath = Path.Combine(
                    fixturePath,
                    ".kyber-weave",
                    ".squad-transaction",
                    "intent.json");
                string numericJournal = ReplaceJsonStringPropertyWithNumber(
                    File.ReadAllText(intentPath, Encoding.UTF8),
                    property,
                    numericValue,
                    out int replacements);
                if (replacements == 0)
                {
                    failures.Add($"journal.{property}=<missing>");
                    continue;
                }

                File.WriteAllText(intentPath, numericJournal, new UTF8Encoding(false));
                SortedDictionary<string, TreeEntry> beforeRecovery = CaptureTree(fixturePath);
                Exception? exception = Record.Exception(() => transaction.Recover(
                    fixturePath,
                    SquadDeploymentScope.Project));
                if (exception is not InvalidDataException)
                    failures.Add($"journal.{property}={numericValue}");
                if (!TreesEqual(beforeRecovery, CaptureTree(fixturePath)))
                    failures.Add($"journal.{property}={numericValue}:filesystem-access");
            }
        }

        Assert.True(
            failures.Count == 0,
            $"Integer enum tokens were accepted or reached filesystem recovery: {string.Join(", ", failures)}");
    }

    [Theory]
    [InlineData("preparing")]
    [InlineData("prepared")]
    public void SerializeAuthorityStrictReaderRoundTripsEverySupportedShape(string journalPhase)
    {
        using TransactionFixture fixture = TransactionFixture.Create();
        SquadStateStore store = Store(fixture.Path);
        SquadLock[] locks =
        [
            Lock(),
            Lock() with { Targets = [], Exclusions = [] }
        ];
        foreach (SquadLock squadLock in locks)
            AssertLockEqual(squadLock, store.DeserializeLock(store.SerializeLock(squadLock)));

        foreach (SquadDeploymentScope scope in Enum.GetValues<SquadDeploymentScope>())
        {
            SquadReceipt[] receipts =
            [
                fixture.Receipt with { Scope = scope },
                fixture.Receipt with
                {
                    Scope = scope,
                    Degradations = [],
                    Files = []
                }
            ];
            foreach (SquadReceipt receipt in receipts)
            {
                string serialized = store.SerializeReceipt(receipt);
                Assert.Contains($"\"scope\": \"{scope.ToString().ToLowerInvariant()}\"", serialized, StringComparison.Ordinal);
                AssertReceiptEqual(receipt, store.DeserializeReceipt(serialized));
            }
        }

        SortedDictionary<string, TreeEntry> original = CaptureTree(fixture.Path);
        SortedDictionary<string, TreeEntry> interrupted = journalPhase == "preparing"
            ? InterruptTransactionAfter(
                fixture,
                SquadTransactionStepKind.IntentWritten)
            : InterruptTransactionAfterCheckpoint(
                fixture,
                SquadTransactionCheckpointKind.Prepared);
        RestoreTree(fixture.Path, interrupted);
        string intentPath = Path.Combine(
            fixture.Path,
            ".kyber-weave",
            ".squad-transaction",
            "intent.json");
        JsonObject intent = ReadJsonObject(intentPath);
        Assert.Equal(journalPhase, intent["phase"]?.GetValue<string>());
        Assert.False(intent.ContainsKey("missingJournalDirectoryCount"));
        JsonArray createdDirectories = Assert.IsType<JsonArray>(intent["createdDirectories"]);
        AssertCreatedDirectoryAuthorityIsCanonical(createdDirectories);
        AssertJsonEnumTokensAreStrings(intent);
        if (journalPhase == "prepared")
        {
            JsonArray artifacts = Assert.IsType<JsonArray>(intent["artifacts"]);
            AssertPreparedArtifactAuthority(artifacts);
        }

        Transaction(fixture.Path).Recover(fixture.Path, SquadDeploymentScope.Project);

        AssertTreesEqual(original, CaptureTree(fixture.Path));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("mutated")]
    [InlineData("extra")]
    public void ExecuteClosedArtifactVerificationOccursBeforeFirstLeafMutation(
        string corruption)
    {
        using TransactionFixture fixture = TransactionFixture.Create();
        string fixturePath = fixture.Path;
        SortedDictionary<string, TreeEntry> before = CaptureTree(fixturePath);
        PreparedBoundaryCorruptionObserver observer = new PreparedBoundaryCorruptionObserver(
            fixturePath,
            fixture.RelativePath,
            corruption);
        SquadDeploymentPlan updatePlan = fixture.CreateUpdatePlan();
        SquadTransaction transaction = Transaction(fixturePath, observer);

        Exception? exception = Record.Exception(() =>
            transaction.Execute(updatePlan));

        InvalidDataException invalid = Assert.IsType<InvalidDataException>(exception);
        Assert.Contains("Squad", invalid.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(observer.CorruptedPreparedArtifacts);
        Assert.False(
            observer.SawLeafOrStateMutation,
            "Closed artifact verification must run before an active transition, leaf claim, or state mutation.");
        AssertTreesEqual(before, CaptureTreeWithoutTransactionEvidence(fixturePath));
        Assert.True(
            Directory.Exists(Path.Combine(
                fixturePath,
                ".kyber-weave",
                ".squad-transaction")),
            "A rejected prepared generation must be retained as diagnostic evidence.");
    }

    [Theory]
    [InlineData("remove", "target-stage")]
    [InlineData("remove", "target-backup")]
    [InlineData("remove", "target-link-metadata")]
    [InlineData("remove", "state-stage")]
    [InlineData("remove", "state-original")]
    [InlineData("add", "target-stage")]
    [InlineData("add", "target-backup")]
    [InlineData("add", "target-link-metadata")]
    [InlineData("add", "state-stage")]
    [InlineData("add", "state-original")]
    [InlineData("add", "claimed-original")]
    [InlineData("add", "discarded-after-image")]
    public void ExecuteSemanticArtifactSetCannotBeChangedByEditingManifestAndTreeTogether(
        string edit,
        string semanticRole)
    {
        using RichTransactionFixture fixture = RichTransactionFixture.Create();
        string fixturePath = fixture.Path;
        CheckpointFailingObserver observer = new CheckpointFailingObserver(
            fixturePath,
            SquadTransactionCheckpointKind.Prepared);
        SquadDeploymentPlan updatePlan = fixture.CreateUpdatePlan();
        SquadTransaction transaction = Transaction(fixturePath, observer);
        Assert.Throws<InjectedSquadTransactionFailure>(() =>
            transaction.Execute(updatePlan));
        RestoreTree(
            fixturePath,
            Assert.IsType<SortedDictionary<string, TreeEntry>>(
                observer.InterruptedTreeSnapshot));
        string transactionDirectory = Path.Combine(
            fixturePath,
            ".kyber-weave",
            ".squad-transaction");
        string intentPath = Path.Combine(transactionDirectory, "intent.json");
        JsonObject intent = ReadJsonObject(intentPath);
        EditSemanticArtifactSet(
            intent,
            transactionDirectory,
            edit,
            semanticRole);
        WriteJsonObject(intentPath, intent);
        SortedDictionary<string, TreeEntry> beforeRecovery = CaptureTree(fixturePath);
        SquadTransaction recoveryTransaction = Transaction(fixturePath);

        Exception? exception = Record.Exception(() => recoveryTransaction.Recover(
            fixturePath,
            SquadDeploymentScope.Project));

        InvalidDataException invalid = Assert.IsType<InvalidDataException>(exception);
        Assert.Contains("Squad", invalid.Message, StringComparison.OrdinalIgnoreCase);
        AssertTreesEqual(beforeRecovery, CaptureTree(fixturePath));
    }

    [Theory]
    [InlineData("file-write", "ActiveTransitionWritten")]
    [InlineData("file-write", "OriginalClaimed")]
    [InlineData("file-write", "AfterImagePublished")]
    [InlineData("link-write", "ActiveTransitionWritten")]
    [InlineData("link-write", "OriginalClaimed")]
    [InlineData("link-write", "AfterImagePublished")]
    [InlineData("file-delete", "ActiveTransitionWritten")]
    [InlineData("file-delete", "OriginalClaimed")]
    [InlineData("file-delete", "AfterImagePublished")]
    [InlineData("link-delete", "ActiveTransitionWritten")]
    [InlineData("link-delete", "OriginalClaimed")]
    [InlineData("link-delete", "AfterImagePublished")]
    [InlineData("lock-write", "ActiveTransitionWritten")]
    [InlineData("lock-write", "OriginalClaimed")]
    [InlineData("lock-write", "AfterImagePublished")]
    [InlineData("lock-delete", "ActiveTransitionWritten")]
    [InlineData("lock-delete", "OriginalClaimed")]
    [InlineData("lock-delete", "AfterImagePublished")]
    [InlineData("receipt-write", "ActiveTransitionWritten")]
    [InlineData("receipt-write", "OriginalClaimed")]
    [InlineData("receipt-write", "AfterImagePublished")]
    [InlineData("receipt-delete", "ActiveTransitionWritten")]
    [InlineData("receipt-delete", "OriginalClaimed")]
    [InlineData("receipt-delete", "AfterImagePublished")]
    public void RecoverActiveClaimTransitionRestoresOriginalWithoutConflict(
        string scenario,
        string crashPoint)
    {
        Assert.True(
            Enum.TryParse(crashPoint, out SquadTransactionCheckpointKind checkpoint),
            $"K4q requires the opt-in '{crashPoint}' crash boundary.");
        using TempDirectory fixture = new TempDirectory();
        SquadStateStore store = Store(fixture.Path);
        ActiveTransitionScenario recoveryScenario = CreateActiveTransitionScenario(
            fixture.Path,
            scenario);
        SortedDictionary<string, TreeEntry> before = CaptureTree(fixture.Path);
        ActiveTransitionCrashObserver observer = new ActiveTransitionCrashObserver(
            fixture.Path,
            checkpoint,
            recoveryScenario.Subject);
        SquadTransaction transaction = new SquadTransaction(store, observer);

        Assert.Throws<InjectedSquadTransactionFailure>(() =>
            transaction.Execute(recoveryScenario.Plan));
        RestoreTree(
            fixture.Path,
            Assert.IsType<SortedDictionary<string, TreeEntry>>(
                observer.InterruptedTreeSnapshot));

        transaction.Recover(fixture.Path, SquadDeploymentScope.Project);
        SortedDictionary<string, TreeEntry> afterFirstRecovery = CaptureTree(fixture.Path);
        transaction.Recover(fixture.Path, SquadDeploymentScope.Project);

        AssertTreesEqual(before, afterFirstRecovery);
        AssertTreesEqual(afterFirstRecovery, CaptureTree(fixture.Path));
    }

    [Fact]
    public void ResolvePhysicalRootUsesActualOnDiskSpellingAndDirectoryCaseSemantics()
    {
        using TempDirectory fixture = new TempDirectory();
        string actualRoot = Path.Combine(fixture.Path, "ActualSpelling");
        string caseVariant = Path.Combine(fixture.Path, "actualspelling");
        Directory.CreateDirectory(actualRoot);
        DirectoryInfo actualEntry = new DirectoryInfo(fixture.Path).EnumerateDirectories()
            .Single(directory => string.Equals(
                directory.Name,
                "ActualSpelling",
                StringComparison.Ordinal));
        SquadPhysicalRootIdentity actualIdentity = SquadPhysicalRootIdentity.Resolve(actualRoot);

        Assert.Equal(
            actualEntry.Name,
            Path.GetFileName(actualIdentity.PhysicalPath));
        if (Directory.Exists(caseVariant))
        {
            Assert.Equal(
                actualIdentity,
                SquadPhysicalRootIdentity.Resolve(caseVariant));
            return;
        }

        Directory.CreateDirectory(caseVariant);
        SquadPhysicalRootIdentity distinctIdentity = SquadPhysicalRootIdentity.Resolve(caseVariant);
        Assert.NotEqual(actualIdentity.PhysicalPath, distinctIdentity.PhysicalPath);
        Assert.NotEqual(actualIdentity.Key, distinctIdentity.Key);
    }

    [Fact]
    public void ResolvePhysicalRootComparisonPolicyIsNotOperatingSystemWideCaseFolding()
    {
        string identitySource = File.ReadAllText(Path.Combine(
            KyberWeaveTestPaths.ToolRoot,
            "src",
            "KyberWeave.Core",
            "Squad",
            "Deployment",
            "SquadPhysicalRootIdentity.cs"), Encoding.UTF8);
        string transactionSource = File.ReadAllText(Path.Combine(
            KyberWeaveTestPaths.ToolRoot,
            "src",
            "KyberWeave.Core",
            "Squad",
            "Deployment",
            "SquadTransaction.cs"), Encoding.UTF8);

        Assert.DoesNotContain("physicalPath.ToUpperInvariant()", identitySource, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "OperatingSystem.IsWindows()\n            ? StringComparison.OrdinalIgnoreCase",
            transactionSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolvePhysicalRootWindowsCaseSensitiveSiblingsRemainDistinct()
    {
        if (!OperatingSystem.IsWindows())
        {
            output.WriteLine(
                "Windows per-directory case-sensitivity branch is exercised only on Windows; " +
                "the host volume semantics remain covered by the paired cross-platform probes.");
            return;
        }

        using TempDirectory fixture = new TempDirectory();
        string fixturePath = fixture.Path;
        string caseSensitiveParent = Path.Combine(fixturePath, "case-sensitive-parent");
        Directory.CreateDirectory(caseSensitiveParent);
        if (!TryEnableWindowsDirectoryCaseSensitivity(caseSensitiveParent, out string diagnostic))
        {
            output.WriteLine(
                $"Windows per-directory case sensitivity is unavailable: {diagnostic}");
            return;
        }

        string upperRoot = Path.Combine(caseSensitiveParent, "Root");
        string lowerRoot = Path.Combine(caseSensitiveParent, "root");
        Directory.CreateDirectory(upperRoot);
        Directory.CreateDirectory(lowerRoot);
        SquadPhysicalRootIdentity upperIdentity = SquadPhysicalRootIdentity.Resolve(upperRoot);
        SquadPhysicalRootIdentity lowerIdentity = SquadPhysicalRootIdentity.Resolve(lowerRoot);
        SquadStateStore store = Store(Path.Combine(fixturePath, "application-data"));

        Assert.NotEqual(upperIdentity.PhysicalPath, lowerIdentity.PhysicalPath);
        Assert.NotEqual(upperIdentity.Key, lowerIdentity.Key);
        Assert.NotEqual(
            store.ResolveReceiptPath(upperRoot, SquadDeploymentScope.Global),
            store.ResolveReceiptPath(lowerRoot, SquadDeploymentScope.Global));

        using BlockingObserver blocker = new BlockingObserver(SquadTransactionStepKind.IntentWritten);
        SquadTransaction activeTransaction = new SquadTransaction(store, blocker);
        SquadDeploymentPlan upperPlan = SquadDeploymentPlan.CreateInstall(
            upperRoot,
            SquadDeploymentScope.Project,
            Lock(),
            [Rendered(".codex/agents/upper.toml", "upper")],
            [],
            adopt: false,
            new FixedTimeProvider(InstalledAt));
        Task active = Task.Run(() => activeTransaction.Execute(upperPlan));
        Assert.True(
            blocker.Reached.Wait(TimeSpan.FromSeconds(5)),
            "The first Windows case-sensitive sibling did not acquire its lease.");
        Exception? siblingFailure;
        try
        {
            SquadTransaction siblingTransaction = new SquadTransaction(store);
            SquadDeploymentPlan lowerPlan = SquadDeploymentPlan.CreateInstall(
                lowerRoot,
                SquadDeploymentScope.Project,
                Lock(),
                [Rendered(".codex/agents/lower.toml", "lower")],
                [],
                adopt: false,
                new FixedTimeProvider(InstalledAt));
            siblingFailure = Record.Exception(() => siblingTransaction.Execute(lowerPlan));
        }
        finally
        {
            blocker.Release.Set();
        }

        await active;
        Assert.Null(siblingFailure);
    }

    [Theory]
    [InlineData("schema", "")]
    [InlineData("schema", " \t\r\n")]
    [InlineData("schema", "\u00a0\u2003")]
    [InlineData("squad-version", "")]
    [InlineData("squad-version", " \t\r\n")]
    [InlineData("squad-version", "\u00a0\u2003")]
    [InlineData("cli-version", "")]
    [InlineData("cli-version", " \t\r\n")]
    [InlineData("cli-version", "\u00a0\u2003")]
    [InlineData("mcp-version", "")]
    [InlineData("mcp-version", " \t\r\n")]
    [InlineData("mcp-version", "\u00a0\u2003")]
    [InlineData("bundle", "")]
    [InlineData("bundle", " \t\r\n")]
    [InlineData("bundle", "\u00a0\u2003")]
    [InlineData("translation", "")]
    [InlineData("translation", " \t\r\n")]
    [InlineData("translation", "\u00a0\u2003")]
    [InlineData("apm-version", "")]
    [InlineData("apm-version", " \t\r\n")]
    [InlineData("apm-version", "\u00a0\u2003")]
    [InlineData("apm-tag-commit", "")]
    [InlineData("apm-tag-commit", " \t\r\n")]
    [InlineData("apm-tag-commit", "\u00a0\u2003")]
    [InlineData("target", "")]
    [InlineData("target", " \t\r\n")]
    [InlineData("target", "\u00a0\u2003")]
    [InlineData("exclusion", "")]
    [InlineData("exclusion", " \t\r\n")]
    [InlineData("exclusion", "\u00a0\u2003")]
    public void SerializeLockBlankRequiredValuesAreRejected(
        string field,
        string blankValue)
    {
        using TempDirectory fixture = new TempDirectory();
        SquadStateStore store = Store(fixture.Path);
        SquadLock invalid = ReplaceRequiredLockValue(Lock(), field, blankValue);
        string? emitted = null;

        Exception? exception = Record.Exception(() => emitted = store.SerializeLock(invalid));

        Assert.IsType<InvalidDataException>(exception);
        Assert.Null(emitted);
    }

    [Fact]
    public void DeserializeAuthorityNoncanonicalEnumCaseIsRejected()
    {
        using RichTransactionFixture fixture = RichTransactionFixture.Create();
        string fixturePath = fixture.Path;
        SquadStateStore store = Store(fixturePath);
        List<string> failures = new List<string>();
        foreach (SquadDeploymentScope scope in Enum.GetValues<SquadDeploymentScope>())
        {
            string canonical = store.SerializeReceipt(fixture.Receipt with { Scope = scope });
            string token = scope.ToString().ToLowerInvariant();
            foreach (string variant in NoncanonicalCaseVariants(token))
            {
                string changed = canonical.Replace(
                    $"\"scope\": \"{token}\"",
                    $"\"scope\": \"{variant}\"",
                    StringComparison.Ordinal);
                if (Record.Exception(() => store.DeserializeReceipt(changed)) is not InvalidDataException)
                    failures.Add($"receipt.scope={variant}");
            }
        }

        CheckpointFailingObserver observer = new CheckpointFailingObserver(
            fixturePath,
            SquadTransactionCheckpointKind.Prepared);
        SquadDeploymentPlan updatePlan = fixture.CreateUpdatePlan();
        SquadTransaction transaction = Transaction(fixturePath, observer);
        Assert.Throws<InjectedSquadTransactionFailure>(() =>
            transaction.Execute(updatePlan));
        SortedDictionary<string, TreeEntry> interrupted = Assert.IsType<SortedDictionary<string, TreeEntry>>(
            observer.InterruptedTreeSnapshot);
        RestoreTree(fixturePath, interrupted);
        string intentPath = Path.Combine(
            fixturePath,
            ".kyber-weave",
            ".squad-transaction",
            "intent.json");
        JsonObject canonicalIntent = ReadJsonObject(intentPath);
        (string Property, string Value, int Occurrence)[] enumTokens = EnumerateAuthorityEnumTokens(canonicalIntent).ToArray();
        Assert.NotEmpty(enumTokens);
        foreach ((string Property, string Value, int Occurrence) token in enumTokens)
        {
            foreach (string variant in NoncanonicalCaseVariants(token.Value))
            {
                RestoreTree(fixturePath, interrupted);
                JsonObject changed = Assert.IsType<JsonObject>(canonicalIntent.DeepClone());
                Assert.True(
                    ReplaceAuthorityEnumOccurrence(
                        changed,
                        token.Property,
                        token.Value,
                        token.Occurrence,
                        variant),
                    $"Could not replace {token.Property}[{token.Occurrence}].");
                WriteJsonObject(intentPath, changed);
                SortedDictionary<string, TreeEntry> beforeRecovery = CaptureTree(fixturePath);
                SquadTransaction recoveryTransaction = Transaction(fixturePath);
                Exception? exception = Record.Exception(() => recoveryTransaction.Recover(
                    fixturePath,
                    SquadDeploymentScope.Project));
                if (exception is not InvalidDataException)
                    failures.Add($"journal.{token.Property}={variant}");
                if (!TreesEqual(beforeRecovery, CaptureTree(fixturePath)))
                    failures.Add($"journal.{token.Property}={variant}:filesystem-access");
            }
        }

        Assert.True(
            failures.Count == 0,
            "Noncanonical authority enum spellings were accepted or reached filesystem " +
            $"recovery: {string.Join(", ", failures)}");
    }

    [Fact]
    public void ResolveFileWindowsCaseSensitiveSiblingSymlinkEscapeIsRejected()
    {
        using TempDirectory fixture = new TempDirectory();
        string caseSensitiveParent = Path.Combine(fixture.Path, "case-sensitive-parent");
        Directory.CreateDirectory(caseSensitiveParent);
        if (OperatingSystem.IsWindows() &&
            !TryEnableWindowsDirectoryCaseSensitivity(caseSensitiveParent, out string diagnostic))
        {
            output.WriteLine(
                $"Windows per-directory case sensitivity is unavailable: {diagnostic}");
            return;
        }

        string upperRoot = Path.Combine(caseSensitiveParent, "Root");
        string lowerRoot = Path.Combine(caseSensitiveParent, "root");
        Directory.CreateDirectory(upperRoot);
        if (Directory.Exists(lowerRoot))
        {
            output.WriteLine(
                "The temporary volume is case-insensitive; the case-distinct sibling branch " +
                "is covered by the Linux and capable Windows filesystem-contract runners.");
            return;
        }

        Directory.CreateDirectory(lowerRoot);
        const string canaryRelativePath = "outside-canary.txt";
        Write(lowerRoot, canaryRelativePath, "outside canary");
        SortedDictionary<string, TreeEntry> outsideBefore = CaptureTree(lowerRoot);

        string directAlias = Path.Combine(upperRoot, "escape");
        Directory.CreateSymbolicLink(directAlias, lowerRoot);
        Assert.Throws<SquadPathContainmentException>(() =>
            SquadPathPolicy.ResolveFile(upperRoot, $"escape/{canaryRelativePath}"));

        Assert.Throws<SquadPathContainmentException>(() =>
            SquadDeploymentPlan.CreateInstall(
                upperRoot,
                SquadDeploymentScope.Project,
                Lock(),
                [Rendered("escape/generated.md", "must remain contained")],
                [],
                adopt: false,
                new FixedTimeProvider(InstalledAt)));

        Directory.Delete(directAlias);
        Directory.CreateDirectory(Path.Combine(upperRoot, ".kyber-weave"));
        SquadDeploymentPlan plan = SquadDeploymentPlan.CreateInstall(
            upperRoot,
            SquadDeploymentScope.Project,
            Lock(),
            [Rendered(".codex/agents/conductor.toml", "contained")],
            [],
            adopt: false,
            new FixedTimeProvider(InstalledAt));
        Directory.Delete(Path.Combine(upperRoot, ".kyber-weave"));
        Directory.CreateSymbolicLink(Path.Combine(upperRoot, ".kyber-weave"), lowerRoot);

        Assert.Throws<SquadPathContainmentException>(() =>
            Transaction(upperRoot).Execute(plan));
        Assert.Throws<SquadPathContainmentException>(() =>
            Store(Path.Combine(fixture.Path, "application-data")).ResolveReceiptPath(
                upperRoot,
                SquadDeploymentScope.Project));
        Assert.Throws<SquadPathContainmentException>(() =>
            Transaction(upperRoot).Recover(upperRoot, SquadDeploymentScope.Project));
        AssertTreesEqual(outsideBefore, CaptureTree(lowerRoot));
    }

    [Fact]
    public void AllContainmentHelpersUseCanonicalFileSystemSemantics()
    {
        string deploymentRoot = Path.Combine(
            KyberWeaveTestPaths.ToolRoot,
            "src",
            "KyberWeave.Core",
            "Squad",
            "Deployment");
        string semanticsPath = Path.Combine(
            deploymentRoot,
            "SquadFileSystemPathSemantics.cs");

        Assert.True(
            File.Exists(semanticsPath),
            "Containment must be centralized in SquadFileSystemPathSemantics.");

        List<string> offenders = new List<string>();
        foreach (string sourcePath in Directory.EnumerateFiles(deploymentRoot, "*.cs"))
        {
            if (string.Equals(sourcePath, semanticsPath, StringComparison.Ordinal))
                continue;

            string source = File.ReadAllText(sourcePath, Encoding.UTF8);
            if (source.Contains("private static StringComparison PathComparison", StringComparison.Ordinal) ||
                source.Contains("private static bool IsWithin(", StringComparison.Ordinal) ||
                source.Contains("private static bool PathsEqual(", StringComparison.Ordinal) ||
                source.Contains(
                    "OperatingSystem.IsWindows()\n            ? StringComparison.OrdinalIgnoreCase",
                    StringComparison.Ordinal))
            {
                offenders.Add(Path.GetFileName(sourcePath));
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Deployment containment helpers must delegate to " +
            $"SquadFileSystemPathSemantics: {string.Join(", ", offenders)}");
    }

    [Theory]
    [InlineData("target-file")]
    [InlineData("target-link-metadata")]
    [InlineData("lock-original")]
    [InlineData("receipt-original")]
    public void RestoreIntentBackupChangedAfterObserverBoundaryIsNeverConsumed(
        string artifactRole)
    {
        using TempDirectory fixture = new TempDirectory();
        string fixturePath = fixture.Path;
        RestoreArtifactScenario scenario = CreateRestoreArtifactScenario(fixturePath, artifactRole);
        RestoreArtifactTamperingObserver observer = new RestoreArtifactTamperingObserver(
            scenario.ArtifactPath,
            scenario.MutationBoundary);
        SquadTransaction transaction = Transaction(fixturePath, observer);

        Exception? exception = Record.Exception(() =>
            transaction.Execute(scenario.Plan));

        InvalidDataException invalid = Assert.IsType<InvalidDataException>(exception);
        Assert.Contains("artifact", invalid.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(observer.MutatedArtifact);
        Assert.True(File.Exists(scenario.ArtifactPath));
        Assert.Equal(observer.CorruptBytes, File.ReadAllBytes(scenario.ArtifactPath));
        Assert.True(File.Exists(scenario.IntentPath));
        AssertTreeEntryEqual(
            scenario.ExpectedLiveAfterImage,
            CaptureTree(fixturePath)[scenario.LiveRelativePath]);

        SortedDictionary<string, TreeEntry> retainedEvidence = CaptureTree(fixturePath);
        SquadTransaction recoveryTransaction = Transaction(fixturePath);
        Exception? firstRecovery = Record.Exception(() => recoveryTransaction.Recover(
            fixturePath,
            SquadDeploymentScope.Project));
        Assert.IsType<InvalidDataException>(firstRecovery);
        AssertTreesEqual(retainedEvidence, CaptureTree(fixturePath));
        Exception? secondRecovery = Record.Exception(() => recoveryTransaction.Recover(
            fixturePath,
            SquadDeploymentScope.Project));
        Assert.IsType<InvalidDataException>(secondRecovery);
        AssertTreesEqual(retainedEvidence, CaptureTree(fixturePath));
    }

    [Theory]
    [InlineData("target-file")]
    [InlineData("target-link-metadata")]
    [InlineData("lock-original")]
    [InlineData("receipt-original")]
    public void RecoverRestoreArtifactChangedBeforeRehydratedConsumptionIsNonAuthoritative(
        string artifactRole)
    {
        using TempDirectory fixture = new TempDirectory();
        string fixturePath = fixture.Path;
        RestoreArtifactScenario scenario = CreateRestoreArtifactScenario(fixturePath, artifactRole);
        SortedDictionary<string, TreeEntry> before = CaptureTree(fixturePath);
        LifecycleStepFailingObserver observer = new LifecycleStepFailingObserver(
            fixturePath,
            scenario.MutationBoundary);
        SquadTransaction transaction = Transaction(fixturePath, observer);
        Assert.Throws<InjectedSquadTransactionFailure>(() =>
            transaction.Execute(scenario.Plan));
        RestoreTree(
            fixturePath,
            Assert.IsType<SortedDictionary<string, TreeEntry>>(
                observer.InterruptedTreeSnapshot));
        File.WriteAllBytes(scenario.ArtifactPath, "corrupt restore evidence"u8.ToArray());
        SortedDictionary<string, TreeEntry> beforeRecovery = CaptureTree(fixturePath);
        SquadTransaction recoveryTransaction = Transaction(fixturePath);

        Exception? firstRecovery = Record.Exception(() => recoveryTransaction.Recover(
            fixturePath,
            SquadDeploymentScope.Project));
        Exception? secondRecovery = Record.Exception(() => recoveryTransaction.Recover(
            fixturePath,
            SquadDeploymentScope.Project));

        Assert.IsType<InvalidDataException>(firstRecovery);
        Assert.IsType<InvalidDataException>(secondRecovery);
        AssertTreesEqual(beforeRecovery, CaptureTree(fixturePath));
        Assert.False(TreesEqual(before, CaptureTree(fixturePath)));
    }

    [Theory]
    [InlineData("target-file")]
    [InlineData("target-link-metadata")]
    [InlineData("lock-original")]
    [InlineData("receipt-original")]
    public void RecoverUnchangedRestoreArtifactRestoresExactly(string artifactRole)
    {
        using TempDirectory fixture = new TempDirectory();
        string fixturePath = fixture.Path;
        RestoreArtifactScenario scenario = CreateRestoreArtifactScenario(fixturePath, artifactRole);
        SortedDictionary<string, TreeEntry> before = CaptureTree(fixturePath);
        LifecycleStepFailingObserver observer = new LifecycleStepFailingObserver(
            fixturePath,
            scenario.MutationBoundary);
        SquadTransaction transaction = Transaction(fixturePath, observer);
        Assert.Throws<InjectedSquadTransactionFailure>(() =>
            transaction.Execute(scenario.Plan));
        RestoreTree(
            fixturePath,
            Assert.IsType<SortedDictionary<string, TreeEntry>>(
                observer.InterruptedTreeSnapshot));

        Transaction(fixturePath).Recover(fixturePath, SquadDeploymentScope.Project);
        SortedDictionary<string, TreeEntry> afterFirstRecovery = CaptureTree(fixturePath);
        Transaction(fixturePath).Recover(fixturePath, SquadDeploymentScope.Project);

        AssertTreesEqual(before, afterFirstRecovery);
        AssertTreesEqual(afterFirstRecovery, CaptureTree(fixturePath));
    }

    [Fact]
    public void RestoreIntentRestoreSourcesAreReverifiedAtAdjacentUseBoundary()
    {
        string transactionSource = File.ReadAllText(Path.Combine(
            KyberWeaveTestPaths.ToolRoot,
            "src",
            "KyberWeave.Core",
            "Squad",
            "Deployment",
            "SquadTransaction.cs"), Encoding.UTF8);
        string restoreIntent = ExtractMethod(transactionSource, "RestoreIntent");
        string restoreEntry = ExtractMethod(transactionSource, "RestoreEntry");

        Assert.Contains("VerifyRestore", restoreIntent, StringComparison.Ordinal);
        Assert.Contains("VerifyRestore", restoreEntry, StringComparison.Ordinal);
    }

    [Fact]
    public void SerializeReceiptNullBlankOrNoncanonicalNestedValuesAreRejected()
    {
        using TempDirectory fixture = new TempDirectory();
        SquadStateStore store = Store(fixture.Path);
        string canonicalToken = SquadTargetCatalog.GetToken(SquadTarget.Codex);
        SquadOwnedFile validFile = new SquadOwnedFile(
            ".codex/agents/conductor.toml",
            Digest("conductor"),
            canonicalToken,
            false);
        SquadDegradation validDegradation = new SquadDegradation(
            canonicalToken,
            "conductor",
            "role-skill-fallback");
        SquadReceipt valid = Receipt(validFile) with { Degradations = [validDegradation] };
        List<(string Name, SquadReceipt Receipt)> invalidReceipts =
        [
            ("null file entry", valid with
            {
                Files = [null!]
            }),
            ("null degradation entry", valid with
            {
                Degradations = [null!]
            })
        ];
        string?[] blankValues = [null, string.Empty, " \t\r\n", "\u00a0\u2003"];
        foreach (string? blank in blankValues)
        {
            invalidReceipts.Add(($"degradation target '{blank}'", valid with
            {
                Degradations = [validDegradation with { Target = blank! }]
            }));
            invalidReceipts.Add(($"degradation subject '{blank}'", valid with
            {
                Degradations = [validDegradation with { Subject = blank! }]
            }));
            invalidReceipts.Add(($"degradation code '{blank}'", valid with
            {
                Degradations = [validDegradation with { Code = blank! }]
            }));
            invalidReceipts.Add(($"owned-file target '{blank}'", valid with
            {
                Files = [validFile with { Target = blank! }]
            }));
        }

        string[] noncanonicalTargets = SquadTargetCatalog.All
            .Select(SquadTargetCatalog.GetToken)
            .SelectMany(NoncanonicalCaseVariants)
            .Concat(["github-copilot", "factory-droids", "unknown-target"])
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        foreach (string target in noncanonicalTargets)
        {
            invalidReceipts.Add(($"degradation target '{target}'", valid with
            {
                Degradations = [validDegradation with { Target = target }]
            }));
            invalidReceipts.Add(($"owned-file target '{target}'", valid with
            {
                Files = [validFile with { Target = target }]
            }));
        }

        List<string> accepted = new List<string>();
        foreach ((string? name, SquadReceipt? invalidReceipt) in invalidReceipts)
        {
            string? emitted = null;
            Exception? exception = Record.Exception(() => emitted = store.SerializeReceipt(invalidReceipt));
            if (exception is not InvalidDataException || emitted is not null)
                accepted.Add(name);
        }

        Assert.True(
            accepted.Count == 0,
            "Receipt writer accepted invalid nested values: " + string.Join(", ", accepted));
    }

    [Fact]
    public void SerializeReceiptStrictReaderAcceptsEveryWriterOutput()
    {
        using TempDirectory fixture = new TempDirectory();
        SquadStateStore store = Store(fixture.Path);
        foreach (SquadTarget target in SquadTargetCatalog.All)
        {
            string token = SquadTargetCatalog.GetToken(target);
            SquadReceipt receipt = Receipt(new SquadOwnedFile(
                $".agents/{token}/conductor.md",
                Digest(token),
                token,
                false)) with
            {
                Degradations = [new SquadDegradation(token, "conductor", "fallback")]
            };

            string emitted = store.SerializeReceipt(receipt);
            SquadReceipt roundTripped = store.DeserializeReceipt(emitted);

            AssertReceiptEqual(receipt, roundTripped);
            Assert.Contains($"\"target\": \"{token}\"", emitted, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("target", "edit-bytes")]
    [InlineData("target", "replacement-link")]
    [InlineData("target", "replacement-directory")]
    [InlineData("target", "unchanged")]
    [InlineData("lock", "edit-bytes")]
    [InlineData("lock", "replacement-link")]
    [InlineData("lock", "replacement-directory")]
    [InlineData("lock", "unchanged")]
    [InlineData("receipt", "edit-bytes")]
    [InlineData("receipt", "replacement-link")]
    [InlineData("receipt", "replacement-directory")]
    [InlineData("receipt", "unchanged")]
    public void ExecuteTargetAndStateStageChangedAtOriginalClaimedCheckpointIsRejectedBeforePublish(
        string subject,
        string mutation)
    {
        using TransactionFixture fixture = TransactionFixture.Create();
        SquadStateStore store = Store(fixture.Path);
        string liveRelativePath = subject switch
        {
            "target" => fixture.RelativePath,
            "lock" => ".kyber-weave/squad.lock.yml",
            "receipt" => ".kyber-weave/squad.receipt.json",
            _ => throw new ArgumentOutOfRangeException(nameof(subject), subject, null)
        };
        TreeEntry originalLiveEntry = CaptureTree(fixture.Path)[liveRelativePath];
        OriginalClaimStageTamperingObserver observer = new OriginalClaimStageTamperingObserver(
            fixture.Path,
            fixture.RelativePath,
            subject,
            mutation);
        SquadDeploymentPlan updatePlan = fixture.CreateUpdatePlan();
        SquadTransaction transaction = new SquadTransaction(store, observer);

        Exception? exception = Record.Exception(() =>
            transaction.Execute(updatePlan));

        Assert.True(observer.SawOriginalClaimed);
        if (string.Equals(mutation, "unchanged", StringComparison.Ordinal))
        {
            Assert.Null(exception);
            Assert.True(observer.SawAffectedAfterImage);
            Assert.True(observer.SawAffectedPublicApply);
            Assert.False(Directory.Exists(store.ResolveTransactionDirectory(
                fixture.Path,
                SquadDeploymentScope.Project)));
            return;
        }

        InvalidDataException invalid = Assert.IsType<InvalidDataException>(exception);
        Assert.Contains(
            subject == "target" ? Path.GetFileName(fixture.RelativePath) : subject,
            invalid.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.False(
            observer.SawAffectedAfterImage,
            "A changed stage must be rejected before its after-image checkpoint.");
        Assert.False(
            observer.SawAffectedPublicApply,
            "A changed stage must be rejected before its public apply event.");
        AssertTreeEntryEqual(originalLiveEntry, CaptureTree(fixture.Path)[liveRelativePath]);
        Assert.True(File.Exists(observer.IntentPath));
        AssertTreeEntryEqual(
            Assert.IsType<TreeEntry>(observer.MutatedStageEntry),
            CaptureTree(fixture.Path)[observer.StageRelativePath]);
    }

    [Fact]
    public void PublishStageNoOverwriteVerifiesAuthorityAtAdjacentMoveBoundary()
    {
        string source = File.ReadAllText(Path.Combine(
            KyberWeaveTestPaths.ToolRoot,
            "src",
            "KyberWeave.Core",
            "Squad",
            "Deployment",
            "SquadTransaction.cs"), Encoding.UTF8);
        string declaration = ExtractMethodDeclaration(source, "PublishStageNoOverwrite");
        string method = ExtractMethod(source, "PublishStageNoOverwrite");

        Assert.Contains("IntentDocument", declaration, StringComparison.Ordinal);
        Assert.Contains("ArtifactArea", declaration, StringComparison.Ordinal);
        Assert.Contains("ArtifactRole", declaration, StringComparison.Ordinal);
        Assert.Contains("ArtifactFingerprintMatches", method, StringComparison.Ordinal);
        Assert.Contains("MoveEntryNoOverwrite", method, StringComparison.Ordinal);

        int fingerprint = method.LastIndexOf("ArtifactFingerprintMatches", StringComparison.Ordinal);
        int move = method.IndexOf("MoveEntryNoOverwrite", fingerprint, StringComparison.Ordinal);
        Assert.True(fingerprint >= 0 && move > fingerprint);
        string adjacentBoundary = method[fingerprint..move];
        Assert.DoesNotContain("Notify", adjacentBoundary, StringComparison.Ordinal);
        Assert.DoesNotContain("await", adjacentBoundary, StringComparison.Ordinal);
        Assert.DoesNotContain("PublishIntent", adjacentBoundary, StringComparison.Ordinal);
        Assert.DoesNotContain("File.", adjacentBoundary, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.", adjacentBoundary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("target", "file", "edit-bytes")]
    [InlineData("target", "file-link", "retarget-file-link")]
    [InlineData("target", "file", "replace-directory")]
    [InlineData("target", "file", "unchanged")]
    [InlineData("lock", "file", "edit-bytes")]
    [InlineData("lock", "directory-link", "retarget-directory-link")]
    [InlineData("lock", "directory", "replace-file")]
    [InlineData("lock", "file", "unchanged")]
    [InlineData("receipt", "file", "edit-bytes")]
    [InlineData("receipt", "file-link", "retarget-file-link")]
    [InlineData("receipt", "directory", "replace-file")]
    [InlineData("receipt", "file", "unchanged")]
    public void RecoverClaimChangedAtOriginalClaimedCheckpointIsNeverMovedLive(
        string subject,
        string originalKind,
        string mutation)
    {
        AssertCaughtClaimMutation(subject, originalKind, mutation);
        AssertRehydratedClaimMutation(subject, originalKind, mutation);
    }

    [Theory]
    [InlineData("target-backup")]
    [InlineData("target-link-metadata")]
    [InlineData("lock-original")]
    [InlineData("lock-link-metadata")]
    [InlineData("receipt-original")]
    [InlineData("receipt-link-metadata")]
    public void RestoreIntentAfterImagePublishedFailureRestoresFilesAndLinksFromVerifiedPayload(
        string artifactRole)
    {
        using TempDirectory caughtFixture = new TempDirectory();
        AfterImageRestoreScenario caughtScenario = CreateAfterImageRestoreScenario(caughtFixture.Path, artifactRole);
        SortedDictionary<string, TreeEntry> caughtBefore = CaptureTree(caughtFixture.Path);
        AfterImageCrashObserver caughtObserver = new AfterImageCrashObserver(
            caughtFixture.Path,
            caughtScenario.Subject);

        Assert.Throws<InjectedSquadTransactionFailure>(() =>
            Transaction(caughtFixture.Path, caughtObserver).Execute(caughtScenario.Plan));

        AssertTreesEqual(caughtBefore, CaptureTree(caughtFixture.Path));

        using TempDirectory recoveryFixture = new TempDirectory();
        AfterImageRestoreScenario recoveryScenario = CreateAfterImageRestoreScenario(recoveryFixture.Path, artifactRole);
        SortedDictionary<string, TreeEntry> recoveryBefore = CaptureTree(recoveryFixture.Path);
        AfterImageCrashObserver recoveryObserver = new AfterImageCrashObserver(
            recoveryFixture.Path,
            recoveryScenario.Subject);
        Assert.Throws<InjectedSquadTransactionFailure>(() =>
            Transaction(recoveryFixture.Path, recoveryObserver).Execute(recoveryScenario.Plan));
        RestoreTree(
            recoveryFixture.Path,
            Assert.IsType<SortedDictionary<string, TreeEntry>>(
                recoveryObserver.InterruptedTreeSnapshot));

        Transaction(recoveryFixture.Path).Recover(
            recoveryFixture.Path,
            SquadDeploymentScope.Project);
        SortedDictionary<string, TreeEntry> afterFirstRecovery = CaptureTree(recoveryFixture.Path);
        Transaction(recoveryFixture.Path).Recover(
            recoveryFixture.Path,
            SquadDeploymentScope.Project);

        AssertTreesEqual(recoveryBefore, afterFirstRecovery);
        AssertTreesEqual(afterFirstRecovery, CaptureTree(recoveryFixture.Path));
    }

    [Fact]
    public void RestoreIntentUsesSingleVerifiedImmutablePayload()
    {
        string source = File.ReadAllText(Path.Combine(
            KyberWeaveTestPaths.ToolRoot,
            "src",
            "KyberWeave.Core",
            "Squad",
            "Deployment",
            "SquadTransaction.cs"), Encoding.UTF8);
        string capture = ExtractMethod(source, "CaptureVerifiedRestorePayload");
        string compare = ExtractMethod(source, "CompareAndRestoreEntry");
        string restore = ExtractMethod(source, "RestoreEntry");

        Assert.Contains("ReadOnlyMemory<byte>", source, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(compare, "CaptureVerifiedRestorePayload("));
        Assert.DoesNotContain("File.ReadAll", compare, StringComparison.Ordinal);
        Assert.DoesNotContain("File.ReadAll", restore, StringComparison.Ordinal);
        Assert.DoesNotContain("VerifyRestoreArtifact", restore, StringComparison.Ordinal);
        Assert.Contains("payload", restore, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GetEntryKind", capture, StringComparison.Ordinal);
        Assert.True(
            CountOccurrences(capture, "GetEntryKind") >= 2,
            "Restore payload capture must verify the source node kind before and after its single read.");
        Assert.Equal(1, CountOccurrences(capture, "File.ReadAllBytes(backupPath)"));
        Assert.Equal(1, CountOccurrences(capture, "File.ReadAllBytes(linkMetadataPath)"));
        Assert.Contains("FileMode.CreateNew", restore, StringComparison.Ordinal);
        Assert.DoesNotContain("FileMode.Create,", restore, StringComparison.Ordinal);
        Assert.DoesNotContain("FileMode.Create)", restore, StringComparison.Ordinal);
        Assert.Contains("CreateSymbolicLink", restore, StringComparison.Ordinal);
        Assert.Contains("GetEntryKind(targetPath)", restore, StringComparison.Ordinal);
        int liveRecheck = restore.LastIndexOf(
            "GetEntryKind(targetPath)",
            StringComparison.Ordinal);
        int liveRemoval = restore.IndexOf(
            "DeleteEntry(targetPath",
            liveRecheck,
            StringComparison.Ordinal);
        Assert.True(
            liveRecheck >= 0 && liveRemoval > liveRecheck,
            "The live after-image must be revalidated immediately before removal.");
        string adjacentRemoval = restore[liveRecheck..liveRemoval];
        Assert.DoesNotContain("File.ReadAll", adjacentRemoval, StringComparison.Ordinal);
        Assert.DoesNotContain("Notify", adjacentRemoval, StringComparison.Ordinal);
        Assert.DoesNotContain("await", adjacentRemoval, StringComparison.Ordinal);
        Assert.DoesNotContain("PublishIntent", adjacentRemoval, StringComparison.Ordinal);
        Assert.DoesNotContain("Notify", restore, StringComparison.Ordinal);
        Assert.DoesNotContain("await", restore, StringComparison.Ordinal);
    }

    private static void AssertCaughtClaimMutation(
        string subject,
        string originalKind,
        string mutation)
    {
        using TempDirectory fixture = new TempDirectory();
        string fixturePath = fixture.Path;
        ClaimMutationScenario scenario = CreateClaimMutationScenario(
            fixturePath,
            subject,
            originalKind);
        SortedDictionary<string, TreeEntry> original = CaptureTree(fixturePath);
        OriginalClaimTamperingObserver observer = new OriginalClaimTamperingObserver(
            fixturePath,
            scenario.Subject,
            mutation);
        SquadTransaction transaction = Transaction(fixturePath, observer);

        Exception? exception = Record.Exception(() =>
            transaction.Execute(scenario.Plan));

        Assert.True(observer.SawOriginalClaimed);
        Assert.False(observer.SawAffectedAfterImage);
        Assert.False(observer.SawAffectedPublicApply);
        if (string.Equals(mutation, "unchanged", StringComparison.Ordinal))
        {
            Assert.IsType<InjectedSquadTransactionFailure>(exception);
            AssertTreesEqual(original, CaptureTree(fixturePath));
            Assert.False(Directory.Exists(Path.Combine(
                fixturePath,
                ".kyber-weave",
                ".squad-transaction")));
            return;
        }

        InvalidDataException invalid = Assert.IsType<InvalidDataException>(exception);
        Assert.Contains("claim", invalid.Message, StringComparison.OrdinalIgnoreCase);
        SortedDictionary<string, TreeEntry> caughtTree = CaptureTree(fixturePath);
        Assert.False(caughtTree.ContainsKey(scenario.LiveRelativePath));
        AssertTreeEntryEqual(
            Assert.IsType<TreeEntry>(observer.MutatedClaimEntry),
            caughtTree[observer.ClaimRelativePath]);
        Assert.True(File.Exists(observer.IntentPath));

        SquadTransaction recoveryTransaction = Transaction(fixturePath);
        Exception? firstRecovery = Record.Exception(() => recoveryTransaction.Recover(
            fixturePath,
            SquadDeploymentScope.Project));
        SortedDictionary<string, TreeEntry> afterFirstRecovery = CaptureTree(fixturePath);
        Exception? secondRecovery = Record.Exception(() => recoveryTransaction.Recover(
            fixturePath,
            SquadDeploymentScope.Project));

        Assert.IsType<InvalidDataException>(firstRecovery);
        Assert.IsType<InvalidDataException>(secondRecovery);
        AssertTreesEqual(caughtTree, afterFirstRecovery);
        AssertTreesEqual(afterFirstRecovery, CaptureTree(fixturePath));
    }

    private static void AssertRehydratedClaimMutation(
        string subject,
        string originalKind,
        string mutation)
    {
        using TempDirectory fixture = new TempDirectory();
        string fixturePath = fixture.Path;
        ClaimMutationScenario scenario = CreateClaimMutationScenario(
            fixturePath,
            subject,
            originalKind);
        SortedDictionary<string, TreeEntry> original = CaptureTree(fixturePath);
        OriginalClaimTamperingObserver observer = new OriginalClaimTamperingObserver(
            fixturePath,
            scenario.Subject,
            mutation);
        SquadTransaction transaction = Transaction(fixturePath, observer);
        _ = Record.Exception(() =>
            transaction.Execute(scenario.Plan));
        RestoreTree(
            fixturePath,
            Assert.IsType<SortedDictionary<string, TreeEntry>>(
                observer.InterruptedTreeSnapshot));

        if (string.Equals(mutation, "unchanged", StringComparison.Ordinal))
        {
            Transaction(fixturePath).Recover(
                fixturePath,
                SquadDeploymentScope.Project);
            SortedDictionary<string, TreeEntry> controlAfterFirstRecovery = CaptureTree(fixturePath);
            Transaction(fixturePath).Recover(
                fixturePath,
                SquadDeploymentScope.Project);

            AssertTreesEqual(original, controlAfterFirstRecovery);
            AssertTreesEqual(controlAfterFirstRecovery, CaptureTree(fixturePath));
            return;
        }

        SortedDictionary<string, TreeEntry> interrupted = CaptureTree(fixturePath);
        Assert.False(interrupted.ContainsKey(scenario.LiveRelativePath));
        AssertTreeEntryEqual(
            Assert.IsType<TreeEntry>(observer.MutatedClaimEntry),
            interrupted[observer.ClaimRelativePath]);

        SquadTransaction recoveryTransaction = Transaction(fixturePath);
        Exception? firstRecovery = Record.Exception(() => recoveryTransaction.Recover(
            fixturePath,
            SquadDeploymentScope.Project));
        SortedDictionary<string, TreeEntry> afterFirstRecovery = CaptureTree(fixturePath);
        Exception? secondRecovery = Record.Exception(() => recoveryTransaction.Recover(
            fixturePath,
            SquadDeploymentScope.Project));

        Assert.IsType<InvalidDataException>(firstRecovery);
        Assert.IsType<InvalidDataException>(secondRecovery);
        AssertTreesEqual(interrupted, afterFirstRecovery);
        AssertTreesEqual(afterFirstRecovery, CaptureTree(fixturePath));
    }

    private static ClaimMutationScenario CreateClaimMutationScenario(
        string root,
        string subject,
        string originalKind)
    {
        const string targetRelativePath = ".codex/agents/conductor.toml";
        const string installedBody = "installed claim body";
        Write(root, targetRelativePath, installedBody);
        SquadReceipt previousReceipt = Receipt(new SquadOwnedFile(
            targetRelativePath,
            Digest(installedBody),
            "codex",
            false));
        WriteState(root, Lock(), previousReceipt);
        SquadStateStore store = Store(root);
        string livePath = subject switch
        {
            "target" => ToPlatformPath(root, targetRelativePath),
            "lock" => store.ResolveLockPath(root, SquadDeploymentScope.Project),
            "receipt" => store.ResolveReceiptPath(root, SquadDeploymentScope.Project),
            _ => throw new ArgumentOutOfRangeException(nameof(subject), subject, null)
        };
        ConfigureOriginalNode(root, livePath, subject, originalKind);
        string renderedBody = string.Equals(subject, "target", StringComparison.Ordinal)
            ? "updated claim body"
            : installedBody;
        SquadDeploymentPlan plan = SquadDeploymentPlan.CreateUpdate(
            root,
            SquadDeploymentScope.Project,
            Lock("1.2.4"),
            [Rendered(targetRelativePath, renderedBody)],
            previousReceipt,
            [new SquadDegradation("warp", "conductor", "role-skill-fallback")],
            replaceManaged: false,
            new FixedTimeProvider(InstalledAt.AddDays(1)));
        return new ClaimMutationScenario(
            plan,
            string.Equals(subject, "target", StringComparison.Ordinal)
                ? targetRelativePath
                : subject,
            Path.GetRelativePath(root, livePath).Replace(Path.DirectorySeparatorChar, '/'));
    }

    private static AfterImageRestoreScenario CreateAfterImageRestoreScenario(
        string root,
        string artifactRole)
    {
        (string subject, string originalKind) = artifactRole switch
        {
            "target-backup" => ("target", "file"),
            "target-link-metadata" => ("target", "file-link"),
            "lock-original" => ("lock", "file"),
            "lock-link-metadata" => ("lock", "file-link"),
            "receipt-original" => ("receipt", "file"),
            "receipt-link-metadata" => ("receipt", "file-link"),
            _ => throw new ArgumentOutOfRangeException(
                nameof(artifactRole), artifactRole, null)
        };
        ClaimMutationScenario scenario = CreateClaimMutationScenario(root, subject, originalKind);
        return new AfterImageRestoreScenario(scenario.Plan, scenario.Subject);
    }

    private static void ConfigureOriginalNode(
        string root,
        string livePath,
        string subject,
        string originalKind)
    {
        if (string.Equals(originalKind, "file", StringComparison.Ordinal))
            return;

        DeleteLeafEntry(livePath);
        string fixtureDirectory = string.Equals(subject, "target", StringComparison.Ordinal)
            ? Path.Combine(root, ".claim-fixtures")
            : Path.Combine(Path.GetDirectoryName(livePath)!, ".claim-fixtures");
        Directory.CreateDirectory(fixtureDirectory);
        switch (originalKind)
        {
            case "file-link":
                {
                    string linkTargetPath = Path.Combine(fixtureDirectory, $"{subject}-file.txt");
                    string content = string.Equals(subject, "target", StringComparison.Ordinal)
                        ? "installed claim body"
                        : $"original {subject} state";
                    File.WriteAllText(linkTargetPath, content, new UTF8Encoding(false));
                    Directory.CreateDirectory(Path.GetDirectoryName(livePath)!);
                    File.CreateSymbolicLink(
                        livePath,
                        Path.GetRelativePath(Path.GetDirectoryName(livePath)!, linkTargetPath));
                    break;
                }
            case "directory-link":
                {
                    string linkTargetPath = Path.Combine(fixtureDirectory, $"{subject}-directory");
                    Directory.CreateDirectory(linkTargetPath);
                    Directory.CreateDirectory(Path.GetDirectoryName(livePath)!);
                    Directory.CreateSymbolicLink(
                        livePath,
                        Path.GetRelativePath(Path.GetDirectoryName(livePath)!, linkTargetPath));
                    break;
                }
            case "directory":
                Directory.CreateDirectory(livePath);
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(originalKind), originalKind, null);
        }
    }

    private static void DeleteLeafEntry(string path)
    {
        DirectoryInfo directory = new DirectoryInfo(path);
        if (directory.LinkTarget is not null)
        {
            Directory.Delete(path);
            return;
        }

        FileInfo file = new FileInfo(path);
        if (file.LinkTarget is not null || file.Exists)
        {
            File.Delete(path);
            return;
        }

        if (directory.Exists)
            Directory.Delete(path, recursive: true);
    }

    private static string ExtractMethodDeclaration(string source, string methodName)
    {
        int searchFrom = 0;
        while (true)
        {
            int methodNameIndex = source.IndexOf(
                $"{methodName}(",
                searchFrom,
                StringComparison.Ordinal);
            Assert.True(methodNameIndex >= 0, $"Method '{methodName}' was not found.");
            int declarationStart = source.LastIndexOf(
                "    private ",
                methodNameIndex,
                StringComparison.Ordinal);
            int bodyStart = source.IndexOf('{', methodNameIndex);
            Assert.True(declarationStart >= 0 && bodyStart > methodNameIndex);
            string declaration = source[declarationStart..bodyStart];
            if (declaration.Contains(methodName, StringComparison.Ordinal))
                return declaration;

            searchFrom = methodNameIndex + methodName.Length;
        }
    }

    private static int CountOccurrences(string value, string search)
    {
        int count = 0;
        int index = 0;
        while ((index = value.IndexOf(search, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += search.Length;
        }

        return count;
    }

    private static RestoreArtifactScenario CreateRestoreArtifactScenario(
        string root,
        string artifactRole)
    {
        const string targetRelativePath = ".codex/agents/conductor.toml";
        const string installedBody = "installed body";
        string targetPath = ToPlatformPath(root, targetRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        if (string.Equals(artifactRole, "target-link-metadata", StringComparison.Ordinal))
        {
            const string canonicalRelativePath = "canonical/conductor.toml";
            string canonicalPath = Path.Combine(root, "canonical", "conductor.toml");
            Write(root, canonicalRelativePath, installedBody);
            File.CreateSymbolicLink(
                targetPath,
                Path.GetRelativePath(Path.GetDirectoryName(targetPath)!, canonicalPath));
        }
        else
        {
            File.WriteAllText(targetPath, installedBody, new UTF8Encoding(false));
        }

        SquadReceipt previousReceipt = Receipt(new SquadOwnedFile(
            targetRelativePath,
            Digest(installedBody),
            "codex",
            false));
        WriteState(root, Lock(), previousReceipt);
        SquadDeploymentPlan plan = SquadDeploymentPlan.CreateUpdate(
            root,
            SquadDeploymentScope.Project,
            Lock("1.2.4"),
            [Rendered(targetRelativePath, "updated body")],
            previousReceipt,
            [new SquadDegradation("codex", "conductor", "model-profile-narrowed")],
            replaceManaged: false,
            new FixedTimeProvider(InstalledAt.AddDays(1)));
        string transactionDirectory = Path.Combine(
            root,
            ".kyber-weave",
            ".squad-transaction");
        SquadStateStore store = Store(root);
        (SquadTransactionStepKind boundary, string artifactPath, string liveRelativePath, byte[] expectedBytes) = artifactRole switch
        {
            "target-file" => (
                SquadTransactionStepKind.FileApplied,
                ToPlatformPath(
                    Path.Combine(transactionDirectory, "backups"),
                    targetRelativePath),
                targetRelativePath,
                "updated body"u8.ToArray()),
            "target-link-metadata" => (
                SquadTransactionStepKind.FileApplied,
                ToPlatformPath(
                    Path.Combine(transactionDirectory, "links"),
                    targetRelativePath),
                targetRelativePath,
                "updated body"u8.ToArray()),
            "lock-original" => (
                SquadTransactionStepKind.LockApplied,
                Path.Combine(transactionDirectory, "state-originals", "lock"),
                ".kyber-weave/squad.lock.yml",
                Encoding.UTF8.GetBytes(store.SerializeLock(
                    plan.Lock ?? throw new InvalidOperationException("The update plan has no lock.")))),
            "receipt-original" => (
                SquadTransactionStepKind.ReceiptApplied,
                Path.Combine(transactionDirectory, "state-originals", "receipt"),
                ".kyber-weave/squad.receipt.json",
                Encoding.UTF8.GetBytes(store.SerializeReceipt(plan.Receipt))),
            _ => throw new ArgumentOutOfRangeException(
                nameof(artifactRole), artifactRole, "Unknown restore artifact role.")
        };
        return new RestoreArtifactScenario(
            plan,
            boundary,
            artifactPath,
            Path.Combine(transactionDirectory, "intent.json"),
            liveRelativePath,
            new TreeEntry(TreeEntryKind.File, expectedBytes, null));
    }

    private static string ExtractMethod(string source, string methodName)
    {
        int searchFrom = 0;
        while (true)
        {
            int methodNameIndex = source.IndexOf(
                $"{methodName}(",
                searchFrom,
                StringComparison.Ordinal);
            Assert.True(methodNameIndex >= 0, $"Method '{methodName}' was not found.");
            int lineStart = source.LastIndexOf('\n', methodNameIndex) + 1;
            string declarationPrefix = source[lineStart..methodNameIndex];
            if (declarationPrefix.Contains("private", StringComparison.Ordinal))
            {
                int bodyStart = source.IndexOf('{', methodNameIndex);
                Assert.True(bodyStart >= 0);
                int depth = 0;
                for (int index = bodyStart; index < source.Length; index++)
                {
                    depth += source[index] switch
                    {
                        '{' => 1,
                        '}' => -1,
                        _ => 0
                    };
                    if (depth == 0)
                        return source[bodyStart..(index + 1)];
                }

                throw new Xunit.Sdk.XunitException(
                    $"Method '{methodName}' has no complete body.");
            }

            searchFrom = methodNameIndex + methodName.Length;
        }
    }

    private static SortedDictionary<string, TreeEntry> CaptureTreeWithoutTransactionEvidence(
        string root)
    {
        SortedDictionary<string, TreeEntry> result = CaptureTree(root);
        foreach (string path in result.Keys.Where(path =>
                     path.Equals(
                         ".kyber-weave/.squad-transaction",
                         StringComparison.Ordinal) ||
                     path.StartsWith(
                         ".kyber-weave/.squad-transaction/",
                         StringComparison.Ordinal)).ToArray())
        {
            result.Remove(path);
        }

        return result;
    }

    private static void EditSemanticArtifactSet(
        JsonObject intent,
        string transactionDirectory,
        string edit,
        string semanticRole)
    {
        JsonArray artifacts = Assert.IsType<JsonArray>(intent["artifacts"]);
        if (edit == "remove")
        {
            (JsonObject Artifact, int Index) match = artifacts
                .Select((node, index) => (
                    Artifact: Assert.IsType<JsonObject>(node),
                    Index: index))
                .First(pair => string.Equals(
                    pair.Artifact["role"]?.GetValue<string>(),
                    semanticRole,
                    StringComparison.Ordinal));
            string removedRelativePath = Assert.IsType<string>(
                match.Artifact["path"]?.GetValue<string>());
            DeleteArtifactNode(ToPlatformPath(transactionDirectory, removedRelativePath));
            artifacts.RemoveAt(match.Index);
            RemoveEmptyTestArtifactParents(
                ToPlatformPath(transactionDirectory, removedRelativePath),
                transactionDirectory);
            return;
        }

        if (edit != "add")
            throw new ArgumentOutOfRangeException(nameof(edit), edit, null);

        string transactionId = Assert.IsType<string>(intent["transactionId"]?.GetValue<string>());
        string suffix = Digest($"{transactionId}:{semanticRole}");
        (string area, string relativePath) = semanticRole switch
        {
            "target-stage" => ("work", $"staging/semantic-extra-{suffix}.bin"),
            "target-backup" => ("work", $"backups/semantic-extra-{suffix}.bin"),
            "target-link-metadata" => ("work", $"links/semantic-extra-{suffix}.txt"),
            "state-stage" => ("journal", $"state-staging/semantic-extra-{suffix}"),
            "state-original" => ("work", $"state-originals/semantic-extra-{suffix}"),
            "claimed-original" => ("work", $"claimed-{transactionId}-{suffix}"),
            "discarded-after-image" => ("work", $"discarded-{transactionId}-{suffix}"),
            _ => throw new ArgumentOutOfRangeException(
                nameof(semanticRole),
                semanticRole,
                null)
        };
        byte[] bytes = Encoding.UTF8.GetBytes($"role-valid semantic extra {semanticRole}");
        string fullPath = ToPlatformPath(transactionDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, bytes);
        artifacts.Add(new JsonObject
        {
            ["transactionId"] = transactionId,
            ["area"] = area,
            ["role"] = semanticRole,
            ["path"] = relativePath,
            ["nodeKind"] = "file",
            ["byteLength"] = bytes.LongLength,
            ["sha256"] = Digest(bytes),
            ["linkTarget"] = string.Empty,
            ["lifecycleState"] = "pre-operation"
        });
    }

    private static void DeleteArtifactNode(string path)
    {
        FileInfo file = new FileInfo(path);
        if (file.LinkTarget is not null || file.Exists)
        {
            file.Delete();
            return;
        }

        DirectoryInfo directory = new DirectoryInfo(path);
        if (directory.LinkTarget is not null || directory.Exists)
            directory.Delete(recursive: false);
    }

    private static void RemoveEmptyTestArtifactParents(string artifactPath, string root)
    {
        string? current = Path.GetDirectoryName(artifactPath);
        while (current is not null &&
               !string.Equals(current, root, StringComparison.Ordinal) &&
               Directory.Exists(current) &&
               !Directory.EnumerateFileSystemEntries(current).Any())
        {
            Directory.Delete(current);
            current = Path.GetDirectoryName(current);
        }
    }

    private static ActiveTransitionScenario CreateActiveTransitionScenario(
        string root,
        string scenario)
    {
        const string relativePath = ".codex/agents/conductor.toml";
        if (scenario is "file-write" or "file-delete")
        {
            const string installed = "installed file body";
            Write(root, relativePath, installed);
            SquadReceipt receipt = Receipt(new SquadOwnedFile(
                relativePath,
                Digest(installed),
                "codex",
                false));
            WriteState(root, Lock(), receipt);
            SquadDeploymentFile[] rendered = scenario == "file-write"
                ? [Rendered(relativePath, "updated file body")]
                : [];
            return new ActiveTransitionScenario(
                SquadDeploymentPlan.CreateUpdate(
                    root,
                    SquadDeploymentScope.Project,
                    Lock("1.2.4"),
                    rendered,
                    receipt,
                    [],
                    replaceManaged: false,
                    new FixedTimeProvider(InstalledAt.AddDays(1))),
                relativePath);
        }

        if (scenario is "link-write" or "link-delete")
        {
            const string installed = "installed link target";
            const string targetRelativePath = "canonical/conductor.toml";
            string canonicalPath = Path.Combine(root, "canonical", "conductor.toml");
            Write(root, targetRelativePath, installed);
            string fullLinkPath = ToPlatformPath(root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullLinkPath)!);
            File.CreateSymbolicLink(
                fullLinkPath,
                Path.GetRelativePath(Path.GetDirectoryName(fullLinkPath)!, canonicalPath));
            SquadReceipt receipt = Receipt(new SquadOwnedFile(
                relativePath,
                Digest(installed),
                "codex",
                false));
            WriteState(root, Lock(), receipt);
            SquadDeploymentFile[] rendered = scenario == "link-write"
                ? [Rendered(relativePath, "updated link body")]
                : [];
            return new ActiveTransitionScenario(
                SquadDeploymentPlan.CreateUpdate(
                    root,
                    SquadDeploymentScope.Project,
                    Lock("1.2.4"),
                    rendered,
                    receipt,
                    [],
                    replaceManaged: false,
                    new FixedTimeProvider(InstalledAt.AddDays(1))),
                relativePath);
        }

        SquadReceipt emptyReceipt = Receipt();
        WriteState(root, Lock(), emptyReceipt);
        if (scenario is "lock-write" or "receipt-write")
        {
            return new ActiveTransitionScenario(
                SquadDeploymentPlan.CreateUpdate(
                    root,
                    SquadDeploymentScope.Project,
                    Lock("1.2.4"),
                    [],
                    emptyReceipt,
                    [new SquadDegradation("warp", "conductor", "role-skill-fallback")],
                    replaceManaged: false,
                    new FixedTimeProvider(InstalledAt.AddDays(1))),
                scenario.StartsWith("lock", StringComparison.Ordinal) ? "lock" : "receipt");
        }

        if (scenario is "lock-delete" or "receipt-delete")
        {
            return new ActiveTransitionScenario(
                SquadDeploymentPlan.CreateUninstall(
                    root,
                    SquadDeploymentScope.Project,
                    emptyReceipt),
                scenario.StartsWith("lock", StringComparison.Ordinal) ? "lock" : "receipt");
        }

        throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
    }

    private static bool TryEnableWindowsDirectoryCaseSensitivity(
        string directory,
        out string diagnostic)
    {
        if (!OperatingSystem.IsWindows())
        {
            diagnostic = "host is not Windows";
            return false;
        }

        try
        {
            ProcessStartInfo startInfo = new ProcessStartInfo("fsutil.exe")
            {
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add("file");
            startInfo.ArgumentList.Add("setCaseSensitiveInfo");
            startInfo.ArgumentList.Add(directory);
            startInfo.ArgumentList.Add("enable");
            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                diagnostic = "fsutil did not start";
                return false;
            }

            string standardOutput = process.StandardOutput.ReadToEnd();
            string standardError = process.StandardError.ReadToEnd();
            process.WaitForExit();
            diagnostic = string.Join(
                " ",
                new[] { standardOutput, standardError }
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value.Trim()));
            return process.ExitCode == 0;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            diagnostic = exception.Message;
            return false;
        }
    }

    private static SquadLock ReplaceRequiredLockValue(
        SquadLock value,
        string field,
        string replacement) =>
        field switch
        {
            "schema" => value with { Schema = replacement },
            "squad-version" => value with { SquadVersion = replacement },
            "cli-version" => value with { CliVersion = replacement },
            "mcp-version" => value with { McpVersion = replacement },
            "bundle" => value with { Bundle = replacement },
            "translation" => value with { Translation = replacement },
            "apm-version" => value with { Apm = value.Apm with { Version = replacement } },
            "apm-tag-commit" => value with { Apm = value.Apm with { TagCommit = replacement } },
            "target" => value with { Targets = [replacement] },
            "exclusion" => value with { Exclusions = [replacement] },
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, null)
        };

    private static IEnumerable<(string Property, string Value, int Occurrence)>
        EnumerateAuthorityEnumTokens(JsonNode root)
    {
        HashSet<string> enumProperties = new HashSet<string>(StringComparer.Ordinal)
        {
            "phase", "originalKind", "afterKind", "lockKind", "lockAfterKind",
            "receiptKind", "receiptAfterKind", "area", "role", "nodeKind",
            "lifecycleState", "allowedState"
        };
        Dictionary<string, int> occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        return Enumerate(root);

        IEnumerable<(string Property, string Value, int Occurrence)> Enumerate(JsonNode node)
        {
            if (node is JsonObject jsonObject)
            {
                foreach ((string? property, JsonNode? child) in jsonObject)
                {
                    if (child is JsonValue value &&
                        enumProperties.Contains(property) &&
                        value.GetValueKind() == JsonValueKind.String)
                    {
                        string token = value.GetValue<string>();
                        string key = $"{property}\0{token}";
                        occurrences.TryGetValue(key, out int occurrence);
                        occurrences[key] = occurrence + 1;
                        yield return (property, token, occurrence);
                    }

                    if (child is not null)
                    {
                        foreach ((string Property, string Value, int Occurrence) nested in Enumerate(child))
                            yield return nested;
                    }
                }
            }
            else if (node is JsonArray jsonArray)
            {
                foreach (JsonNode? child in jsonArray)
                {
                    if (child is null)
                        continue;
                    foreach ((string Property, string Value, int Occurrence) nested in Enumerate(child))
                        yield return nested;
                }
            }
        }
    }

    private static bool ReplaceAuthorityEnumOccurrence(
        JsonNode root,
        string propertyName,
        string canonicalValue,
        int targetOccurrence,
        string replacement)
    {
        int occurrence = 0;
        return Replace(root);

        bool Replace(JsonNode node)
        {
            if (node is JsonObject jsonObject)
            {
                foreach ((string? property, JsonNode? child) in jsonObject.ToArray())
                {
                    if (string.Equals(property, propertyName, StringComparison.Ordinal) &&
                        child is JsonValue value &&
                        value.GetValueKind() == JsonValueKind.String &&
                        string.Equals(
                            value.GetValue<string>(),
                            canonicalValue,
                            StringComparison.Ordinal))
                    {
                        if (occurrence == targetOccurrence)
                        {
                            jsonObject[property] = replacement;
                            return true;
                        }

                        occurrence++;
                    }

                    if (child is not null && Replace(child))
                        return true;
                }
            }
            else if (node is JsonArray jsonArray)
            {
                foreach (JsonNode? child in jsonArray)
                {
                    if (child is not null && Replace(child))
                        return true;
                }
            }

            return false;
        }
    }

    private static IReadOnlyList<string> NoncanonicalCaseVariants(string canonical) =>
        new[]
            {
                canonical.ToLowerInvariant(),
                canonical.ToUpperInvariant(),
                char.ToUpperInvariant(canonical[0]) + canonical[1..],
                ToggleAsciiCase(canonical)
            }
            .Where(value => !string.Equals(value, canonical, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static JsonObject ReadJsonObject(string path) =>
        Assert.IsType<JsonObject>(JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)));

    private static void WriteJsonObject(string path, JsonObject document) =>
        File.WriteAllText(
            path,
            document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n",
            new UTF8Encoding(false));

    private static void AssertPreparedArtifactAuthority(JsonArray artifacts)
    {
        foreach (JsonNode? node in artifacts)
        {
            JsonObject artifact = Assert.IsType<JsonObject>(node);
            Assert.False(string.IsNullOrWhiteSpace(
                Assert.IsAssignableFrom<JsonValue>(artifact["transactionId"]).GetValue<string>()));
            Assert.False(string.IsNullOrWhiteSpace(
                Assert.IsAssignableFrom<JsonValue>(artifact["area"]).GetValue<string>()));
            Assert.False(string.IsNullOrWhiteSpace(
                Assert.IsAssignableFrom<JsonValue>(artifact["role"]).GetValue<string>()));
            Assert.False(string.IsNullOrWhiteSpace(
                Assert.IsAssignableFrom<JsonValue>(artifact["nodeKind"]).GetValue<string>()));
            Assert.False(string.IsNullOrWhiteSpace(
                Assert.IsAssignableFrom<JsonValue>(artifact["lifecycleState"]).GetValue<string>()));
        }
    }

    private static void CorruptPreparedAuthority(
        JsonObject intent,
        string transactionDirectory,
        string corruption)
    {
        JsonArray artifacts = Assert.IsType<JsonArray>(intent["artifacts"]);
        JsonObject first = Assert.IsType<JsonObject>(artifacts[0]);
        switch (corruption)
        {
            case "missing-required":
                artifacts.RemoveAt(0);
                break;
            case "undeclared-file":
                Write(transactionDirectory, "undeclared.bin", "undeclared file");
                break;
            case "undeclared-link":
                File.CreateSymbolicLink(
                    Path.Combine(transactionDirectory, "undeclared-link"),
                    "missing-link-target");
                break;
            case "undeclared-directory":
                Directory.CreateDirectory(Path.Combine(transactionDirectory, "undeclared-directory"));
                break;
            case "duplicate-identity":
                artifacts.Add(first.DeepClone());
                break;
            case "swapped-area":
                first["area"] = first["area"]?.GetValue<string>() == "journal"
                    ? "work"
                    : "journal";
                break;
            case "swapped-role":
                first["role"] = "state-stage";
                break;
            case "swapped-lifecycle":
                first["lifecycleState"] = "active-transition";
                break;
            case "invalid-transition":
                first["lifecycleState"] = "post-operation";
                intent["activeTransition"] = new JsonObject
                {
                    ["artifactIdentity"] = "not-the-mutated-artifact",
                    ["allowedState"] = "pre-operation"
                };
                break;
            case "well-digested-unexpected":
                const string unexpectedPath = "unexpected-authority.bin";
                byte[] unexpectedBytes = "well-digested but undeclared"u8.ToArray();
                File.WriteAllBytes(Path.Combine(transactionDirectory, unexpectedPath), unexpectedBytes);
                JsonObject unexpected = Assert.IsType<JsonObject>(first.DeepClone());
                unexpected["area"] = "journal";
                unexpected["path"] = unexpectedPath;
                unexpected["byteLength"] = unexpectedBytes.LongLength;
                unexpected["sha256"] = Digest(unexpectedBytes);
                artifacts.Add(unexpected);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(corruption), corruption, null);
        }
    }

    private static void AssertCreatedDirectoryAuthorityIsCanonical(JsonArray createdDirectories)
    {
        int previousDepth = int.MaxValue;
        HashSet<string> identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonNode? node in createdDirectories)
        {
            JsonObject authority = Assert.IsType<JsonObject>(node);
            string area = Assert.IsAssignableFrom<JsonValue>(authority["area"]).GetValue<string>();
            string path = Assert.IsAssignableFrom<JsonValue>(authority["path"]).GetValue<string>();
            Assert.False(string.IsNullOrWhiteSpace(area));
            Assert.False(Path.IsPathRooted(path));
            Assert.DoesNotContain("..", path.Split('/'), StringComparer.Ordinal);
            Assert.True(identities.Add($"{area}:{path}"));
            int depth = path.Count(character => character == '/');
            Assert.True(
                depth <= previousDepth,
                "Created-directory authority must be serialized deepest first.");
            previousDepth = depth;
        }
    }

    private static void CorruptCreatedDirectoryAuthority(
        JsonObject intent,
        JsonArray createdDirectories,
        string fixtureRoot,
        string corruption)
    {
        JsonObject first = Assert.IsType<JsonObject>(createdDirectories[0]);
        switch (corruption)
        {
            case "wrong-area":
                first["area"] = "outside";
                break;
            case "non-ancestor":
                first["path"] = "unrelated/not-an-ancestor";
                break;
            case "duplicate":
                createdDirectories.Add(first.DeepClone());
                break;
            case "unordered":
                Assert.True(createdDirectories.Count > 1);
                JsonObject last = Assert.IsType<JsonObject>(createdDirectories[^1]);
                createdDirectories.RemoveAt(createdDirectories.Count - 1);
                createdDirectories.Insert(0, last);
                break;
            case "absolute":
                first["path"] = Path.Combine(fixtureRoot, "absolute");
                break;
            case "traversal":
                first["path"] = "../escape";
                break;
            case "sibling":
                first["path"] = "beside-target";
                break;
            case "outside-root":
                first["path"] = Path.GetFullPath(Path.Combine(fixtureRoot, "..", "outside"));
                break;
            case "legacy-count":
                intent["missingJournalDirectoryCount"] = 1;
                break;
            case "negative-count":
                intent["missingJournalDirectoryCount"] = -1;
                break;
            case "maximum-count":
                intent["missingJournalDirectoryCount"] = int.MaxValue;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(corruption), corruption, null);
        }
    }

    private static string ReplaceJsonStringPropertyWithNumber(
        string json,
        string propertyName,
        int numericValue,
        out int replacementCount)
    {
        JsonNode root = JsonNode.Parse(json)
            ?? throw new Xunit.Sdk.XunitException("Expected a JSON authority document.");
        replacementCount = ReplaceJsonStringPropertyWithNumber(
            root,
            propertyName,
            numericValue);
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    private static int ReplaceJsonStringPropertyWithNumber(
        JsonNode node,
        string propertyName,
        int numericValue)
    {
        int replacements = 0;
        if (node is JsonObject jsonObject)
        {
            foreach ((string? name, JsonNode? value) in jsonObject.ToArray())
            {
                if (string.Equals(name, propertyName, StringComparison.Ordinal) &&
                    value is JsonValue &&
                    value.GetValueKind() == JsonValueKind.String)
                {
                    jsonObject[name] = numericValue;
                    replacements++;
                    continue;
                }

                if (value is not null)
                {
                    replacements += ReplaceJsonStringPropertyWithNumber(
                        value,
                        propertyName,
                        numericValue);
                }
            }
        }
        else if (node is JsonArray jsonArray)
        {
            foreach (JsonNode? child in jsonArray)
            {
                if (child is not null)
                {
                    replacements += ReplaceJsonStringPropertyWithNumber(
                        child,
                        propertyName,
                        numericValue);
                }
            }
        }

        return replacements;
    }

    private static void AssertJsonEnumTokensAreStrings(JsonNode node)
    {
        HashSet<string> enumProperties = new HashSet<string>(StringComparer.Ordinal)
        {
            "phase", "originalKind", "afterKind", "lockKind", "lockAfterKind",
            "receiptKind", "receiptAfterKind", "area", "role", "nodeKind",
            "lifecycleState"
        };
        AssertJsonEnumTokensAreStrings(node, enumProperties);
    }

    private static void AssertJsonEnumTokensAreStrings(
        JsonNode node,
        IReadOnlySet<string> enumProperties)
    {
        if (node is JsonObject jsonObject)
        {
            foreach ((string? name, JsonNode? value) in jsonObject)
            {
                if (enumProperties.Contains(name))
                {
                    JsonValue token = Assert.IsAssignableFrom<JsonValue>(value);
                    Assert.Equal(JsonValueKind.String, token.GetValueKind());
                }

                if (value is not null)
                    AssertJsonEnumTokensAreStrings(value, enumProperties);
            }
        }
        else if (node is JsonArray jsonArray)
        {
            foreach (JsonNode? child in jsonArray)
            {
                if (child is not null)
                    AssertJsonEnumTokensAreStrings(child, enumProperties);
            }
        }
    }

    private static bool TreesEqual(
        IReadOnlyDictionary<string, TreeEntry> expected,
        IReadOnlyDictionary<string, TreeEntry> actual)
    {
        if (!expected.Keys.SequenceEqual(actual.Keys, StringComparer.Ordinal))
            return false;

        foreach ((string? path, TreeEntry? expectedEntry) in expected)
        {
            TreeEntry actualEntry = actual[path];
            if (expectedEntry.Kind != actualEntry.Kind ||
                !string.Equals(expectedEntry.LinkTarget, actualEntry.LinkTarget, StringComparison.Ordinal))
            {
                return false;
            }

            if ((expectedEntry.Content is null) != (actualEntry.Content is null))
                return false;
            if (expectedEntry.Content is not null &&
                !expectedEntry.Content.SequenceEqual(Assert.IsType<byte[]>(actualEntry.Content)))
            {
                return false;
            }
        }

        return true;
    }

    private static void AssertTreeEntryEqual(TreeEntry expected, TreeEntry actual)
    {
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.LinkTarget, actual.LinkTarget);
        if (expected.Content is null)
        {
            Assert.Null(actual.Content);
            return;
        }

        Assert.NotNull(actual.Content);
        Assert.True(
            expected.Content.SequenceEqual(actual.Content),
            "The raced leaf bytes changed during claim/publication.");
    }

    public static IEnumerable<object[]> PortableInvalidPaths()
    {
        foreach (char forbidden in "<>:\"|?*")
            yield return [$".codex/agents/bad{forbidden}name.md"];

        yield return [".codex//agents/repeated-separator.md"];
        yield return [".codex\\agents\\noncanonical-separators.md"];
        yield return [".codex/agents/trailing-dot."];
        yield return [".codex/agents/trailing-space "];
        yield return [".codex/agents/e\u0301.md"];

        for (int value = 0; value <= 0x1f; value++)
            yield return [$".codex/agents/control{(char)value}name.md"];
        for (int value = 0x7f; value <= 0x9f; value++)
            yield return [$".codex/agents/control{(char)value}name.md"];

        string[] deviceNames =
        [
            "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
            "COM\u00b9", "COM\u00b2", "COM\u00b3", "LPT\u00b9", "LPT\u00b2", "LPT\u00b3"
        ];
        foreach (string deviceName in deviceNames)
        {
            yield return [$".codex/agents/{deviceName}/SKILL.md"];
            yield return [$".codex/agents/{deviceName.ToLowerInvariant()}.txt"];
            yield return [$".codex/agents/{ToggleAsciiCase(deviceName)}.agent.md"];
        }
    }

    private static string ToggleAsciiCase(string value) =>
        new(value.Select(character =>
            char.IsAsciiLetterUpper(character)
                ? char.ToLowerInvariant(character)
                : char.IsAsciiLetterLower(character)
                    ? char.ToUpperInvariant(character)
                    : character).ToArray());

    private static SortedDictionary<string, TreeEntry> InterruptTransactionAfter(
        TransactionFixture fixture,
        SquadTransactionStepKind checkpoint)
    {
        LifecycleStepFailingObserver observer = new LifecycleStepFailingObserver(fixture.Path, checkpoint);

        Assert.Throws<InjectedSquadTransactionFailure>(() =>
            Transaction(fixture.Path, observer).Execute(fixture.CreateUpdatePlan()));

        return Assert.IsType<SortedDictionary<string, TreeEntry>>(
            observer.InterruptedTreeSnapshot);
    }

    private static SortedDictionary<string, TreeEntry> InterruptTransactionAfterCheckpoint(
        TransactionFixture fixture,
        SquadTransactionCheckpointKind checkpoint)
    {
        CheckpointFailingObserver observer = new CheckpointFailingObserver(fixture.Path, checkpoint);

        Assert.Throws<InjectedSquadTransactionFailure>(() =>
            Transaction(fixture.Path, observer).Execute(fixture.CreateUpdatePlan()));

        return Assert.IsType<SortedDictionary<string, TreeEntry>>(
            observer.InterruptedTreeSnapshot);
    }

    private static void CorruptPreparedArtifact(
        string root,
        string relativePath,
        string corruption)
    {
        string transactionDirectory = Path.Combine(root, ".kyber-weave", ".squad-transaction");
        string intentPath = Path.Combine(transactionDirectory, "intent.json");
        string backupPath = ToPlatformPath(
            Path.Combine(transactionDirectory, "backups"),
            relativePath);
        switch (corruption)
        {
            case "truncated-journal":
                byte[] journal = File.ReadAllBytes(intentPath);
                File.WriteAllBytes(intentPath, journal[..Math.Max(1, journal.Length / 2)]);
                break;
            case "duplicate-journal-field":
                File.WriteAllText(
                    intentPath,
                    CorruptJournalDocument(
                        File.ReadAllText(intentPath, Encoding.UTF8),
                        "duplicate"),
                    new UTF8Encoding(false));
                break;
            case "partial-backup":
                byte[] backup = File.ReadAllBytes(backupPath);
                File.WriteAllBytes(backupPath, backup[..Math.Max(1, backup.Length / 2)]);
                break;
            case "wrong-backup-digest":
                byte[] wrong = File.ReadAllBytes(backupPath);
                wrong[0] ^= 0xff;
                File.WriteAllBytes(backupPath, wrong);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(corruption), corruption, null);
        }
    }

    private static void ReplacePublishedArtifactWithPartialTemporaryFile(
        string root,
        string relativePath,
        string artifact)
    {
        string transactionDirectory = Path.Combine(root, ".kyber-weave", ".squad-transaction");
        string publishedPath = artifact switch
        {
            "journal" => Path.Combine(transactionDirectory, "intent.json"),
            "staging" => ToPlatformPath(
                Path.Combine(transactionDirectory, "staging"),
                relativePath),
            "backup" => ToPlatformPath(
                Path.Combine(transactionDirectory, "backups"),
                relativePath),
            _ => throw new ArgumentOutOfRangeException(nameof(artifact), artifact, null)
        };
        byte[] bytes = File.ReadAllBytes(publishedPath);
        string temporaryPath = publishedPath + ".tmp";
        File.Move(publishedPath, temporaryPath);
        File.WriteAllBytes(temporaryPath, bytes[..Math.Max(1, bytes.Length / 2)]);
    }

    private static SquadDeploymentPlan CreateGlobalInstallPlan(string targetRoot) =>
        SquadDeploymentPlan.CreateInstall(
            targetRoot,
            SquadDeploymentScope.Global,
            Lock(),
            [Rendered(".codex/agents/conductor.toml", "global conductor")],
            [],
            adopt: false,
            new FixedTimeProvider(InstalledAt));

    private static int ProbeGlobalInstallStepCount()
    {
        using TempDirectory fixture = new TempDirectory();
        string targetRoot = Path.Combine(fixture.Path, "target");
        Directory.CreateDirectory(targetRoot);
        RecordingObserver observer = new RecordingObserver(targetRoot);
        new SquadTransaction(
            Store(Path.Combine(fixture.Path, "application-data")),
            observer).Execute(CreateGlobalInstallPlan(targetRoot));
        return observer.Steps.Count;
    }

    private static void AssertActiveLeaseConflict(Exception? exception)
    {
        InvalidOperationException conflict = Assert.IsAssignableFrom<InvalidOperationException>(exception);
        Assert.Contains("active", conflict.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static string CorruptLockDocument(string yaml, string corruption) =>
        corruption switch
        {
            "unknown" => yaml + "unknown-field: true\n",
            "duplicate" => yaml + "schema: kyber-squad.lock/v1\n",
            "missing" => yaml.Replace(
                "schema: kyber-squad.lock/v1\n",
                string.Empty,
                StringComparison.Ordinal),
            "null" => yaml.Replace(
                "schema: kyber-squad.lock/v1",
                "schema: null",
                StringComparison.Ordinal),
            "misspelled" => yaml.Replace("schema:", "schemma:", StringComparison.Ordinal),
            "wrong-case" => yaml.Replace("schema:", "Schema:", StringComparison.Ordinal),
            "wrong-type" => yaml.Replace(
                "targets:\n  - codex\n  - warp",
                "targets: 42",
                StringComparison.Ordinal),
            "yaml-alias" => yaml.Replace(
                "schema: kyber-squad.lock/v1",
                "schema: &schema kyber-squad.lock/v1",
                StringComparison.Ordinal),
            "yaml-merge" => "defaults: &defaults\n  schema: kyber-squad.lock/v1\n<<: *defaults\n" +
                yaml.Replace(
                    "schema: kyber-squad.lock/v1\n",
                    string.Empty,
                    StringComparison.Ordinal),
            "yaml-tag" => yaml.Replace(
                "schema: kyber-squad.lock/v1",
                "schema: !!str kyber-squad.lock/v1",
                StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(corruption), corruption, null)
        };

    private static string CorruptReceiptDocument(string json, string corruption)
    {
        return corruption switch
        {
            "unknown" => InsertBeforeRootClose(json, "  \"unknownField\": true"),
            "duplicate" => InsertBeforeRootClose(
                json,
                "  \"schema\": \"kyber-squad.receipt/v1\""),
            "missing" => json
                .Replace("  \"schema\": \"kyber-squad.receipt/v1\",\r\n", string.Empty, StringComparison.Ordinal)
                .Replace("  \"schema\": \"kyber-squad.receipt/v1\",\n", string.Empty, StringComparison.Ordinal),
            "null" => json.Replace(
                "\"schema\": \"kyber-squad.receipt/v1\"",
                "\"schema\": null",
                StringComparison.Ordinal),
            "misspelled" => json.Replace("\"schema\":", "\"schemma\":", StringComparison.Ordinal),
            "wrong-case" => json.Replace("\"schema\":", "\"Schema\":", StringComparison.Ordinal),
            "wrong-type" => json.Replace("\"files\": [", "\"files\": 42, \"ignoredFiles\": [", StringComparison.Ordinal),
            "wrong-enum" => json.Replace("\"scope\": \"project\"", "\"scope\": \"workspace\"", StringComparison.Ordinal),
            "invalid-timestamp" => json.Replace(
                "2026-08-14T12:34:56.0000000Z",
                "not-a-timestamp",
                StringComparison.Ordinal),
            "invalid-digest" => json.Replace(Digest("conductor"), "not-a-digest", StringComparison.Ordinal),
            "portable-duplicate" => InsertDuplicateReceiptFile(json),
            _ => throw new ArgumentOutOfRangeException(nameof(corruption), corruption, null)
        };
    }

    private static string CorruptJournalDocument(string json, string corruption)
    {
        return corruption switch
        {
            "unknown" => InsertBeforeRootClose(json, "  \"unknownField\": true"),
            "duplicate" => InsertBeforeRootClose(
                json,
                "  \"schema\": \"kyber-squad.transaction/v1\""),
            "missing" => json
                .Replace("  \"schema\": \"kyber-squad.transaction/v1\",\r\n", string.Empty, StringComparison.Ordinal)
                .Replace("  \"schema\": \"kyber-squad.transaction/v1\",\n", string.Empty, StringComparison.Ordinal),
            "null" => json.Replace(
                "\"schema\": \"kyber-squad.transaction/v1\"",
                "\"schema\": null",
                StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(corruption), corruption, null)
        };
    }

    private static string InsertBeforeRootClose(string json, string property)
    {
        int rootClose = json.LastIndexOf('}');
        Assert.True(rootClose >= 0);
        string newline = json.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        return json.Insert(rootClose, $",{newline}{property}{newline}");
    }

    private static string InsertDuplicateReceiptFile(string json)
    {
        int insertion = json.LastIndexOf("  ]", StringComparison.Ordinal);
        Assert.True(insertion >= 0);
        string newline = json.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string duplicate = $",{newline}    {{{newline}      \"relativePath\": \".codex/agents/Conductor.toml\",{newline}      \"sha256\": \"{Digest("conductor")}\",{newline}      \"target\": \"codex\",{newline}      \"adopted\": false{newline}    }}";
        return json.Insert(insertion + 3, duplicate);
    }

    private static SquadStateStore Store(string applicationData) =>
        new(new FakeSquadUserPaths(applicationData));

    private static SquadTransaction Transaction(
        string applicationData,
        ISquadTransactionObserver? observer = null) =>
        new(Store(applicationData), observer);

    private static bool IsHeldWithExclusiveFileShare(string path)
    {
        try
        {
            using FileStream stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    private static bool HasDirectoryNamed(string root, string name) =>
        Directory.Exists(root) && Directory.EnumerateDirectories(
                root,
                "*",
                SearchOption.AllDirectories)
            .Any(path => string.Equals(
                Path.GetFileName(path),
                name,
                StringComparison.Ordinal));

    private static SquadLock Lock(string version = "1.2.3") =>
        new(
            "kyber-squad.lock/v1",
            version,
            version,
            version,
            "full",
            ["codex", "warp"],
            ["cursor"],
            "best-effort",
            Digest("bundle"),
            Digest("asset"),
            new SquadApmIdentity(version, "0123456789abcdef", Digest("apm")));

    private static SquadReceipt Receipt(params SquadOwnedFile[] files) =>
        new(
            "kyber-squad.receipt/v1",
            SquadDeploymentScope.Project,
            ".",
            InstalledAt,
            [],
            files);

    private static SquadDeploymentFile Rendered(
        string relativePath,
        string content,
        string target = "codex") =>
        new(relativePath, Encoding.UTF8.GetBytes(content), target);

    private static string Digest(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static void Write(string root, string relativePath, string content)
    {
        string path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    private static string Read(string root, string relativePath) =>
        File.ReadAllText(Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static void WriteState(string root, SquadLock squadLock, SquadReceipt receipt)
    {
        string stateDirectory = Path.Combine(root, ".kyber-weave");
        Directory.CreateDirectory(stateDirectory);
        SquadStateStore store = Store(root);
        File.WriteAllText(
            Path.Combine(stateDirectory, "squad.lock.yml"),
            store.SerializeLock(squadLock),
            new UTF8Encoding(false));
        File.WriteAllText(
            Path.Combine(stateDirectory, "squad.receipt.json"),
            store.SerializeReceipt(receipt),
            new UTF8Encoding(false));
    }

    private static SortedDictionary<string, byte[]> Snapshot(string root)
    {
        SortedDictionary<string, byte[]> snapshot = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(root, path)
                .Replace(Path.DirectorySeparatorChar, '/');
            snapshot.Add(relativePath, ReadAllBytesAllowingWrites(path));
        }

        return snapshot;
    }

    private static SortedDictionary<string, TreeEntry> CaptureTree(string root)
    {
        SortedDictionary<string, TreeEntry> snapshot = new SortedDictionary<string, TreeEntry>(StringComparer.Ordinal);
        CaptureTreeDirectory(new DirectoryInfo(root), root, snapshot);
        return snapshot;
    }

    private static void CaptureTreeDirectory(
        DirectoryInfo directory,
        string root,
        IDictionary<string, TreeEntry> snapshot)
    {
        foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos()
                     .OrderBy(value => value.Name, StringComparer.Ordinal))
        {
            string relativePath = Path.GetRelativePath(root, entry.FullName)
                .Replace(Path.DirectorySeparatorChar, '/');
            if (entry.LinkTarget is not null)
            {
                bool isDirectoryLink = OperatingSystem.IsWindows()
                    ? (entry.Attributes & FileAttributes.Directory) != 0
                    : Directory.Exists(entry.FullName);
                snapshot.Add(
                    relativePath,
                    new TreeEntry(
                        isDirectoryLink
                            ? TreeEntryKind.DirectorySymbolicLink
                            : TreeEntryKind.FileSymbolicLink,
                        null,
                        entry.LinkTarget));
                continue;
            }

            if (entry is DirectoryInfo childDirectory)
            {
                snapshot.Add(relativePath, new TreeEntry(TreeEntryKind.Directory, null, null));
                CaptureTreeDirectory(childDirectory, root, snapshot);
                continue;
            }

            snapshot.Add(
                relativePath,
                new TreeEntry(
                    TreeEntryKind.File,
                    ReadAllBytesAllowingWrites(entry.FullName),
                    null));
        }
    }

    private static byte[] ReadAllBytesAllowingWrites(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using MemoryStream buffer = new();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static void AssertSnapshotsEqual(
        IReadOnlyDictionary<string, byte[]> expected,
        IReadOnlyDictionary<string, byte[]> actual)
    {
        Assert.Equal(expected.Keys, actual.Keys);
        foreach ((string? path, byte[]? expectedBytes) in expected)
        {
            Assert.True(
                actual[path].SequenceEqual(expectedBytes),
                $"File '{path}' changed. Expected {Digest(expectedBytes)}, actual {Digest(actual[path])}.");
        }
    }

    private static void AssertTreesEqual(
        IReadOnlyDictionary<string, TreeEntry> expected,
        IReadOnlyDictionary<string, TreeEntry> actual)
    {
        Assert.Equal(expected.Keys, actual.Keys);
        foreach ((string? path, TreeEntry? expectedEntry) in expected)
        {
            TreeEntry actualEntry = actual[path];
            Assert.Equal(expectedEntry.Kind, actualEntry.Kind);
            Assert.Equal(expectedEntry.LinkTarget, actualEntry.LinkTarget);
            if (expectedEntry.Content is null)
            {
                Assert.Null(actualEntry.Content);
                continue;
            }

            Assert.NotNull(actualEntry.Content);
            Assert.True(
                expectedEntry.Content.SequenceEqual(actualEntry.Content),
                $"File '{path}' changed. Expected {Digest(expectedEntry.Content)}, actual {Digest(actualEntry.Content)}.");
        }
    }

    private static void RestoreTree(
        string root,
        IReadOnlyDictionary<string, TreeEntry> snapshot)
    {
        DeleteTreeContents(new DirectoryInfo(root));

        foreach ((string? relativePath, TreeEntry _) in snapshot
                     .Where(pair => pair.Value.Kind == TreeEntryKind.Directory)
                     .OrderBy(pair => PathDepth(pair.Key)))
        {
            Directory.CreateDirectory(ToPlatformPath(root, relativePath));
        }

        foreach ((string? relativePath, TreeEntry? entry) in snapshot
                     .Where(pair => pair.Value.Kind == TreeEntryKind.File))
        {
            string path = ToPlatformPath(root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, Assert.IsType<byte[]>(entry.Content));
        }

        foreach ((string? relativePath, TreeEntry? entry) in snapshot.Where(pair =>
                     pair.Value.Kind is TreeEntryKind.FileSymbolicLink or
                         TreeEntryKind.DirectorySymbolicLink))
        {
            string path = ToPlatformPath(root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string linkTarget = Assert.IsType<string>(entry.LinkTarget);
            if (entry.Kind == TreeEntryKind.DirectorySymbolicLink)
                Directory.CreateSymbolicLink(path, linkTarget);
            else
                File.CreateSymbolicLink(path, linkTarget);
        }
    }

    private static void DeleteTreeContents(DirectoryInfo directory)
    {
        foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos().ToArray())
        {
            if (entry.LinkTarget is not null)
            {
                if (entry is DirectoryInfo)
                    Directory.Delete(entry.FullName);
                else
                    File.Delete(entry.FullName);
                continue;
            }

            if (entry is DirectoryInfo childDirectory)
            {
                DeleteTreeContents(childDirectory);
                childDirectory.Delete();
            }
            else
            {
                entry.Delete();
            }
        }
    }

    private static string ToPlatformPath(string root, string relativePath) =>
        Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static int PathDepth(string relativePath) =>
        relativePath.Count(character => character == '/');

    private static void RestoreSnapshot(
        string root,
        IReadOnlyDictionary<string, byte[]> snapshot)
    {
        foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            File.Delete(path);
        }

        foreach (string directory in Directory
                     .EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
        {
            Directory.Delete(directory, recursive: false);
        }

        foreach ((string? relativePath, byte[]? content) in snapshot)
        {
            string path = Path.Combine(
                root,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
        }
    }

    private static string Digest(byte[] content) =>
        Convert.ToHexStringLower(SHA256.HashData(content));

    private static void AssertLockEqual(SquadLock expected, SquadLock actual)
    {
        Assert.Equal(expected.Schema, actual.Schema);
        Assert.Equal(expected.SquadVersion, actual.SquadVersion);
        Assert.Equal(expected.CliVersion, actual.CliVersion);
        Assert.Equal(expected.McpVersion, actual.McpVersion);
        Assert.Equal(expected.Bundle, actual.Bundle);
        Assert.Equal(expected.Targets, actual.Targets);
        Assert.Equal(expected.Exclusions, actual.Exclusions);
        Assert.Equal(expected.Translation, actual.Translation);
        Assert.Equal(expected.BundleDigest, actual.BundleDigest);
        Assert.Equal(expected.AssetDigest, actual.AssetDigest);
        Assert.Equal(expected.Apm, actual.Apm);
    }

    private static void AssertReceiptEqual(SquadReceipt expected, SquadReceipt actual)
    {
        Assert.Equal(expected.Schema, actual.Schema);
        Assert.Equal(expected.Scope, actual.Scope);
        Assert.Equal(expected.TargetRoot, actual.TargetRoot);
        Assert.Equal(expected.InstalledAtUtc, actual.InstalledAtUtc);
        Assert.Equal(expected.Degradations, actual.Degradations);
        Assert.Equal(expected.Files, actual.Files);
    }

    private enum TreeEntryKind
    {
        File,
        Directory,
        FileSymbolicLink,
        DirectorySymbolicLink
    }

    private sealed record TreeEntry(
        TreeEntryKind Kind,
        byte[]? Content,
        string? LinkTarget);

    private sealed record ActiveTransitionScenario(
        SquadDeploymentPlan Plan,
        string Subject);

    private sealed record ClaimMutationScenario(
        SquadDeploymentPlan Plan,
        string Subject,
        string LiveRelativePath);

    private sealed record AfterImageRestoreScenario(
        SquadDeploymentPlan Plan,
        string Subject);

    private sealed record RestoreArtifactScenario(
        SquadDeploymentPlan Plan,
        SquadTransactionStepKind MutationBoundary,
        string ArtifactPath,
        string IntentPath,
        string LiveRelativePath,
        TreeEntry ExpectedLiveAfterImage);

    private sealed class FakeSquadUserPaths(string applicationDataDirectory) : ISquadUserPaths
    {
        public string ApplicationDataDirectory { get; } = applicationDataDirectory;
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class GlobalTransactionLocationObserver(
        string targetRoot,
        string stateDirectory) : ISquadTransactionObserver
    {
        public bool SawDurableIntentInState { get; private set; }

        public bool SawStagingOnTargetFilesystem { get; private set; }

        public bool SawBackupOnTargetFilesystem { get; private set; }

        public bool SawStagingOrBackupInState { get; private set; }

        public void AfterStep(SquadTransactionStep step)
        {
            SawDurableIntentInState |= Directory.Exists(stateDirectory) &&
                Directory.EnumerateFiles(
                    stateDirectory,
                    "intent.json",
                    SearchOption.AllDirectories).Any();
            if (step.Kind == SquadTransactionStepKind.FileStaged)
            {
                SawStagingOnTargetFilesystem |= HasDirectoryNamed(targetRoot, "staging");
                SawStagingOrBackupInState |= HasDirectoryNamed(stateDirectory, "staging");
            }

            if (step.Kind == SquadTransactionStepKind.FileBackedUp)
            {
                SawBackupOnTargetFilesystem |= HasDirectoryNamed(targetRoot, "backups");
                SawStagingOrBackupInState |= HasDirectoryNamed(stateDirectory, "backups");
            }
        }
    }

    private sealed class BlockingObserver(SquadTransactionStepKind blockedStep) :
        ISquadTransactionObserver,
        IDisposable
    {
        public ManualResetEventSlim Reached { get; } = new(initialState: false);

        public ManualResetEventSlim Release { get; } = new(initialState: false);

        public void AfterStep(SquadTransactionStep step)
        {
            if (step.Kind != blockedStep)
                return;

            Reached.Set();
            Release.Wait();
        }

        public void Dispose()
        {
            Reached.Dispose();
            Release.Dispose();
        }
    }

    private sealed class RecordingObserver(string root) : ISquadTransactionObserver
    {
        private readonly string _transactionDirectory = Path.Combine(
            root,
            ".kyber-weave",
            ".squad-transaction");

        public List<SquadTransactionStep> Steps { get; } = [];

        public bool SawIntentJournal { get; private set; }

        public bool SawStagedFile { get; private set; }

        public bool SawBackupFile { get; private set; }

        public void AfterStep(SquadTransactionStep step)
        {
            Steps.Add(step);
            SawIntentJournal |= File.Exists(Path.Combine(_transactionDirectory, "intent.json"));
            SawStagedFile |= Directory.Exists(Path.Combine(_transactionDirectory, "staging")) &&
                Directory.EnumerateFiles(
                    Path.Combine(_transactionDirectory, "staging"),
                    "*",
                    SearchOption.AllDirectories).Any();
            SawBackupFile |= Directory.Exists(Path.Combine(_transactionDirectory, "backups")) &&
                Directory.EnumerateFiles(
                    Path.Combine(_transactionDirectory, "backups"),
                    "*",
                    SearchOption.AllDirectories).Any();
        }
    }

    private sealed class CheckpointRecordingObserver : ISquadTransactionCheckpointObserver
    {
        public List<SquadTransactionStep> Steps { get; } = [];

        public List<SquadTransactionCheckpoint> Checkpoints { get; } = [];

        public void AfterStep(SquadTransactionStep step) => Steps.Add(step);

        public void AfterCheckpoint(SquadTransactionCheckpoint checkpoint) =>
            Checkpoints.Add(checkpoint);
    }

    private sealed class FailingObserver(string root, int failureSequence) : ISquadTransactionObserver
    {
        public IReadOnlyDictionary<string, byte[]>? InterruptedSnapshot { get; private set; }

        public IReadOnlyDictionary<string, TreeEntry>? InterruptedTreeSnapshot { get; private set; }

        public void AfterStep(SquadTransactionStep step)
        {
            if (step.Sequence == failureSequence)
            {
                InterruptedSnapshot = Snapshot(root);
                InterruptedTreeSnapshot = CaptureTree(root);
                throw new InjectedSquadTransactionFailure(step.Sequence);
            }
        }
    }

    private sealed class LifecycleStepFailingObserver(
        string root,
        SquadTransactionStepKind checkpoint) : ISquadTransactionObserver
    {
        public IReadOnlyDictionary<string, TreeEntry>? InterruptedTreeSnapshot { get; private set; }

        public void AfterStep(SquadTransactionStep step)
        {
            if (step.Kind != checkpoint)
                return;

            InterruptedTreeSnapshot = CaptureTree(root);
            throw new InjectedSquadTransactionFailure(step.Sequence);
        }
    }

    private sealed class RestoreArtifactTamperingObserver(
        string artifactPath,
        SquadTransactionStepKind mutationBoundary) : ISquadTransactionObserver
    {
        public byte[] CorruptBytes { get; } = Encoding.UTF8.GetBytes(
            $"corrupted restore artifact after {mutationBoundary}");

        public bool MutatedArtifact { get; private set; }

        public void AfterStep(SquadTransactionStep step)
        {
            if (step.Kind != mutationBoundary)
                return;

            Assert.True(
                File.Exists(artifactPath),
                $"Prepared restore artifact '{artifactPath}' was not published.");
            File.WriteAllBytes(artifactPath, CorruptBytes);
            MutatedArtifact = true;
            throw new InjectedSquadTransactionFailure(step.Sequence);
        }
    }

    private sealed class CheckpointFailingObserver(
        string root,
        SquadTransactionCheckpointKind checkpoint) : ISquadTransactionCheckpointObserver
    {
        public IReadOnlyDictionary<string, TreeEntry>? InterruptedTreeSnapshot { get; private set; }

        public void AfterStep(SquadTransactionStep step)
        {
        }

        public void AfterCheckpoint(SquadTransactionCheckpoint value)
        {
            if (value.Kind != checkpoint)
                return;

            InterruptedTreeSnapshot = CaptureTree(root);
            throw new InjectedSquadTransactionFailure(value.Sequence);
        }
    }

    private sealed class PostIntentReplacementObserver(
        string root,
        SquadStateStore store,
        SquadDeploymentScope scope,
        string relativePath,
        SquadTransactionStepKind checkpoint) : ISquadTransactionObserver
    {
        public byte[] ExternalBytes { get; } = Encoding.UTF8.GetBytes(
            $"external replacement after {checkpoint}");

        public string ReplacedPath => checkpoint switch
        {
            SquadTransactionStepKind.FileApplied => ToPlatformPath(root, relativePath),
            SquadTransactionStepKind.LockApplied => store.ResolveLockPath(root, scope),
            SquadTransactionStepKind.ReceiptApplied => store.ResolveReceiptPath(root, scope),
            _ => throw new ArgumentOutOfRangeException(nameof(checkpoint), checkpoint, null)
        };

        public void AfterStep(SquadTransactionStep step)
        {
            if (step.Kind != checkpoint)
                return;

            Directory.CreateDirectory(Path.GetDirectoryName(ReplacedPath)!);
            File.WriteAllBytes(ReplacedPath, ExternalBytes);
            throw new InjectedSquadTransactionFailure(step.Sequence);
        }
    }

    private sealed class ExternalChildObserver(
        string root,
        string generatedPath,
        string externalPath) : ISquadTransactionObserver
    {
        public void AfterStep(SquadTransactionStep step)
        {
            if (step.Kind != SquadTransactionStepKind.FileApplied ||
                !string.Equals(step.RelativePath, generatedPath, StringComparison.Ordinal))
            {
                return;
            }

            Write(root, externalPath, "external operator note");
            throw new InjectedSquadTransactionFailure(step.Sequence);
        }
    }

    private sealed class PreparedArtifactsObserver(
        string root,
        string targetRelativePath) : ISquadTransactionCheckpointObserver
    {
        public bool ObservedPreparedCheckpoint { get; private set; }

        public void AfterStep(SquadTransactionStep step)
        {
        }

        public void AfterCheckpoint(SquadTransactionCheckpoint checkpoint)
        {
            if (checkpoint.Kind != SquadTransactionCheckpointKind.Prepared)
                return;

            Assert.Equal("installed body", Read(root, targetRelativePath));
            string transactionDirectory = Path.Combine(root, ".kyber-weave", ".squad-transaction");
            string[] temporaryArtifacts = Directory.EnumerateFileSystemEntries(
                    transactionDirectory,
                    "*",
                    SearchOption.AllDirectories)
                .Where(path => Path.GetFileName(path).Contains("tmp", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            Assert.Empty(temporaryArtifacts);

            string intentPath = Path.Combine(transactionDirectory, "intent.json");
            using JsonDocument journal = JsonDocument.Parse(ReadAllBytesAllowingWrites(intentPath));
            Assert.Equal("prepared", journal.RootElement.GetProperty("phase").GetString());
            JsonElement[] artifacts = journal.RootElement.GetProperty("artifacts").EnumerateArray().ToArray();
            Assert.NotEmpty(artifacts);
            foreach (JsonElement artifact in artifacts)
            {
                string? relativeArtifactPath = artifact.GetProperty("path").GetString();
                Assert.False(string.IsNullOrWhiteSpace(relativeArtifactPath));
                string artifactPath = ToPlatformPath(
                    transactionDirectory,
                    Assert.IsType<string>(relativeArtifactPath));
                byte[] bytes = ReadAllBytesAllowingWrites(artifactPath);
                Assert.Equal(bytes.LongLength, artifact.GetProperty("byteLength").GetInt64());
                Assert.Equal(Digest(bytes), artifact.GetProperty("sha256").GetString());
            }

            ObservedPreparedCheckpoint = true;
        }
    }

    private sealed class StagedArtifactTamperingObserver(
        string root,
        string relativePath,
        string artifact) : ISquadTransactionCheckpointObserver
    {
        private bool _tampered;

        public bool SawAffectedApplyCheckpoint { get; private set; }

        public void AfterStep(SquadTransactionStep step)
        {
            SawAffectedApplyCheckpoint |= artifact switch
            {
                "target-file" => step.Kind == SquadTransactionStepKind.FileApplied,
                "lock-state" => step.Kind == SquadTransactionStepKind.LockApplied,
                "receipt-state" => step.Kind == SquadTransactionStepKind.ReceiptApplied,
                _ => throw new InvalidOperationException(
                    $"Unknown staged artifact contract '{artifact}'.")
            };
        }

        public void AfterCheckpoint(SquadTransactionCheckpoint checkpoint)
        {
            if (!_tampered && checkpoint.Kind == SquadTransactionCheckpointKind.Prepared)
            {
                string transactionDirectory = Path.Combine(
                    root,
                    ".kyber-weave",
                    ".squad-transaction");
                string stagePath = artifact switch
                {
                    "target-file" => ToPlatformPath(
                        Path.Combine(transactionDirectory, "staging"),
                        relativePath),
                    "lock-state" => Path.Combine(
                        transactionDirectory,
                        "state-staging",
                        "lock"),
                    "receipt-state" => Path.Combine(
                        transactionDirectory,
                        "state-staging",
                        "receipt"),
                    _ => throw new InvalidOperationException(
                        $"Unknown staged artifact contract '{artifact}'.")
                };
                Assert.True(File.Exists(stagePath), $"Prepared stage '{stagePath}' was not published.");
                File.WriteAllText(
                    stagePath,
                    $"changed prepared {artifact}",
                    new UTF8Encoding(false));
                _tampered = true;
            }
        }
    }

    private sealed class LeafRaceObserver(
        string root,
        string relativePath,
        string racedNodeKind) : ISquadTransactionCheckpointObserver
    {
        private bool _raced;

        public TreeEntry? RacedEntry { get; private set; }

        public string ExternalCanaryPath { get; private set; } = string.Empty;

        public void AfterStep(SquadTransactionStep step)
        {
        }

        public void AfterCheckpoint(SquadTransactionCheckpoint checkpoint)
        {
            if (_raced || checkpoint.Kind != SquadTransactionCheckpointKind.Prepared)
                return;

            string targetPath = ToPlatformPath(root, relativePath);
            DeleteLeaf(targetPath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            switch (racedNodeKind)
            {
                case "file":
                    File.WriteAllText(
                        targetPath,
                        "external raced file",
                        new UTF8Encoding(false));
                    ExternalCanaryPath = targetPath;
                    break;
                case "directory":
                    Directory.CreateDirectory(targetPath);
                    ExternalCanaryPath = Path.Combine(targetPath, "external-child.txt");
                    File.WriteAllText(
                        ExternalCanaryPath,
                        "external raced directory child",
                        new UTF8Encoding(false));
                    break;
                case "link":
                    ExternalCanaryPath = Path.Combine(root, "external-link-target.txt");
                    File.WriteAllText(
                        ExternalCanaryPath,
                        "external raced link target",
                        new UTF8Encoding(false));
                    File.CreateSymbolicLink(targetPath, ExternalCanaryPath);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unknown raced node kind '{racedNodeKind}'.");
            }

            RacedEntry = CaptureTree(root)[relativePath];
            _raced = true;
        }

        private static void DeleteLeaf(string path)
        {
            FileInfo entry = new FileInfo(path);
            if (entry.LinkTarget is not null)
            {
                File.Delete(path);
                return;
            }

            DirectoryInfo directory = new DirectoryInfo(path);
            if (directory.LinkTarget is not null)
            {
                Directory.Delete(path);
                return;
            }

            if (File.Exists(path))
                File.Delete(path);
            else if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
    }

    private sealed class PreparedBoundaryCorruptionObserver(
        string root,
        string relativePath,
        string corruption) : ISquadTransactionCheckpointObserver
    {
        public bool CorruptedPreparedArtifacts { get; private set; }

        public bool SawLeafOrStateMutation { get; private set; }

        public void AfterStep(SquadTransactionStep step)
        {
            if (!CorruptedPreparedArtifacts)
                return;

            SawLeafOrStateMutation |= step.Kind is
                SquadTransactionStepKind.FileApplied or
                SquadTransactionStepKind.LockApplied or
                SquadTransactionStepKind.ReceiptApplied;
        }

        public void AfterCheckpoint(SquadTransactionCheckpoint checkpoint)
        {
            if (!CorruptedPreparedArtifacts &&
                checkpoint.Kind == SquadTransactionCheckpointKind.Prepared)
            {
                string transactionDirectory = Path.Combine(
                    root,
                    ".kyber-weave",
                    ".squad-transaction");
                string stagePath = ToPlatformPath(
                    Path.Combine(transactionDirectory, "staging"),
                    relativePath);
                switch (corruption)
                {
                    case "missing":
                        File.Delete(stagePath);
                        break;
                    case "mutated":
                        File.WriteAllText(
                            stagePath,
                            "mutated after prepared publication",
                            new UTF8Encoding(false));
                        break;
                    case "extra":
                        Write(
                            transactionDirectory,
                            "unexpected/closed-set-extra.bin",
                            "undeclared prepared artifact");
                        break;
                    default:
                        throw new InvalidOperationException(
                            $"Unknown prepared-boundary corruption '{corruption}'.");
                }

                CorruptedPreparedArtifacts = true;
                return;
            }

            if (!CorruptedPreparedArtifacts)
                return;

            SawLeafOrStateMutation |= checkpoint.Kind is
                SquadTransactionCheckpointKind.ActiveTransitionWritten or
                SquadTransactionCheckpointKind.OriginalClaimed or
                SquadTransactionCheckpointKind.AfterImagePublished;
        }
    }

    private sealed class ActiveTransitionCrashObserver(
        string root,
        SquadTransactionCheckpointKind checkpoint,
        string subject) : ISquadTransactionCheckpointObserver
    {
        public IReadOnlyDictionary<string, TreeEntry>? InterruptedTreeSnapshot { get; private set; }

        public void AfterStep(SquadTransactionStep step)
        {
        }

        public void AfterCheckpoint(SquadTransactionCheckpoint value)
        {
            if (value.Kind != checkpoint ||
                !string.Equals(value.RelativePath, subject, StringComparison.Ordinal))
            {
                return;
            }

            InterruptedTreeSnapshot = CaptureTree(root);
            throw new InjectedSquadTransactionFailure(value.Sequence);
        }
    }

    private sealed class OriginalClaimStageTamperingObserver(
        string root,
        string targetRelativePath,
        string subject,
        string mutation) : ISquadTransactionCheckpointObserver
    {
        private readonly string _checkpointSubject = string.Equals(
            subject,
            "target",
            StringComparison.Ordinal)
            ? targetRelativePath
            : subject;

        public bool SawOriginalClaimed { get; private set; }

        public bool SawAffectedAfterImage { get; private set; }

        public bool SawAffectedPublicApply { get; private set; }

        public TreeEntry? MutatedStageEntry { get; private set; }

        public string IntentPath => Path.Combine(
            root,
            ".kyber-weave",
            ".squad-transaction",
            "intent.json");

        public string StageRelativePath => subject switch
        {
            "target" => $".kyber-weave/.squad-transaction/staging/{targetRelativePath}",
            "lock" => ".kyber-weave/.squad-transaction/state-staging/lock",
            "receipt" => ".kyber-weave/.squad-transaction/state-staging/receipt",
            _ => throw new ArgumentOutOfRangeException(nameof(subject), subject, null)
        };

        public void AfterStep(SquadTransactionStep step)
        {
            SawAffectedPublicApply |= subject switch
            {
                "target" => step.Kind == SquadTransactionStepKind.FileApplied &&
                    string.Equals(
                        step.RelativePath,
                        targetRelativePath,
                        StringComparison.Ordinal),
                "lock" => step.Kind == SquadTransactionStepKind.LockApplied,
                "receipt" => step.Kind == SquadTransactionStepKind.ReceiptApplied,
                _ => throw new InvalidOperationException(
                    $"Unknown claim-stage subject '{subject}'.")
            };
        }

        public void AfterCheckpoint(SquadTransactionCheckpoint checkpoint)
        {
            if (!string.Equals(
                    checkpoint.RelativePath,
                    _checkpointSubject,
                    StringComparison.Ordinal))
            {
                return;
            }

            SawAffectedAfterImage |=
                checkpoint.Kind == SquadTransactionCheckpointKind.AfterImagePublished;
            if (SawOriginalClaimed ||
                checkpoint.Kind != SquadTransactionCheckpointKind.OriginalClaimed)
            {
                return;
            }

            SawOriginalClaimed = true;
            if (string.Equals(mutation, "unchanged", StringComparison.Ordinal))
                return;

            string stagePath = ToPlatformPath(root, StageRelativePath);
            Assert.True(
                File.Exists(stagePath),
                $"Prepared stage '{stagePath}' must exist at OriginalClaimed.");
            switch (mutation)
            {
                case "edit-bytes":
                    File.WriteAllText(
                        stagePath,
                        $"changed {subject} stage after claim",
                        new UTF8Encoding(false));
                    break;
                case "replacement-link":
                    File.Delete(stagePath);
                    File.CreateSymbolicLink(stagePath, "replacement-stage-target");
                    break;
                case "replacement-directory":
                    File.Delete(stagePath);
                    Directory.CreateDirectory(stagePath);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unknown claim-stage mutation '{mutation}'.");
            }

            MutatedStageEntry = CaptureTree(root)[StageRelativePath];
        }
    }

    private sealed class OriginalClaimTamperingObserver(
        string root,
        string subject,
        string mutation) : ISquadTransactionCheckpointObserver
    {
        public bool SawOriginalClaimed { get; private set; }

        public bool SawAffectedAfterImage { get; private set; }

        public bool SawAffectedPublicApply { get; private set; }

        public string ClaimRelativePath { get; private set; } = string.Empty;

        public TreeEntry? MutatedClaimEntry { get; private set; }

        public IReadOnlyDictionary<string, TreeEntry>? InterruptedTreeSnapshot { get; private set; }

        public string IntentPath => Path.Combine(
            root,
            ".kyber-weave",
            ".squad-transaction",
            "intent.json");

        public void AfterStep(SquadTransactionStep step)
        {
            SawAffectedPublicApply |= subject switch
            {
                "lock" => step.Kind == SquadTransactionStepKind.LockApplied,
                "receipt" => step.Kind == SquadTransactionStepKind.ReceiptApplied,
                _ => step.Kind == SquadTransactionStepKind.FileApplied &&
                    string.Equals(step.RelativePath, subject, StringComparison.Ordinal)
            };
        }

        public void AfterCheckpoint(SquadTransactionCheckpoint checkpoint)
        {
            if (!string.Equals(checkpoint.RelativePath, subject, StringComparison.Ordinal))
                return;

            SawAffectedAfterImage |=
                checkpoint.Kind == SquadTransactionCheckpointKind.AfterImagePublished;
            if (checkpoint.Kind != SquadTransactionCheckpointKind.OriginalClaimed)
                return;

            SawOriginalClaimed = true;
            using JsonDocument intent = JsonDocument.Parse(ReadAllBytesAllowingWrites(IntentPath));
            JsonElement activeClaim = intent.RootElement
                .GetProperty("artifacts")
                .EnumerateArray()
                .Single(artifact =>
                    string.Equals(
                        artifact.GetProperty("role").GetString(),
                        "claimed-original",
                        StringComparison.Ordinal) &&
                    string.Equals(
                        artifact.GetProperty("lifecycleState").GetString(),
                        "active-transition",
                        StringComparison.Ordinal));
            string artifactRelativePath = Assert.IsType<string>(
                activeClaim.GetProperty("path").GetString());
            string claimPath = ToPlatformPath(
                Path.Combine(root, ".kyber-weave", ".squad-transaction"),
                artifactRelativePath);
            ClaimRelativePath = Path.GetRelativePath(root, claimPath)
                .Replace(Path.DirectorySeparatorChar, '/');
            switch (mutation)
            {
                case "edit-bytes":
                    File.WriteAllText(
                        claimPath,
                        $"changed {subject} claim bytes",
                        new UTF8Encoding(false));
                    break;
                case "retarget-file-link":
                    File.Delete(claimPath);
                    File.CreateSymbolicLink(claimPath, "retargeted-file-claim");
                    break;
                case "retarget-directory-link":
                    DeleteLeafEntry(claimPath);
                    Directory.CreateSymbolicLink(claimPath, "retargeted-directory-claim");
                    break;
                case "replace-directory":
                    File.Delete(claimPath);
                    Directory.CreateDirectory(claimPath);
                    break;
                case "replace-file":
                    Directory.Delete(claimPath, recursive: true);
                    File.WriteAllText(
                        claimPath,
                        $"replacement {subject} claim file",
                        new UTF8Encoding(false));
                    break;
                case "unchanged":
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unknown claim mutation '{mutation}'.");
            }

            MutatedClaimEntry = CaptureTree(root)[ClaimRelativePath];
            InterruptedTreeSnapshot = CaptureTree(root);
            throw new InjectedSquadTransactionFailure(checkpoint.Sequence);
        }
    }

    private sealed class AfterImageCrashObserver(
        string root,
        string subject) : ISquadTransactionCheckpointObserver
    {
        public IReadOnlyDictionary<string, TreeEntry>? InterruptedTreeSnapshot { get; private set; }

        public void AfterStep(SquadTransactionStep step)
        {
        }

        public void AfterCheckpoint(SquadTransactionCheckpoint checkpoint)
        {
            if (checkpoint.Kind != SquadTransactionCheckpointKind.AfterImagePublished ||
                !string.Equals(checkpoint.RelativePath, subject, StringComparison.Ordinal))
            {
                return;
            }

            InterruptedTreeSnapshot = CaptureTree(root);
            throw new InjectedSquadTransactionFailure(checkpoint.Sequence);
        }
    }

    [SuppressMessage("ReSharper", "UnusedMember.Local", Justification = "Standard exception constructors required by CA1032.")]
    private sealed class InjectedSquadTransactionFailure : Exception
    {
        public InjectedSquadTransactionFailure()
        {
        }

        public InjectedSquadTransactionFailure(string message)
            : base(message)
        {
        }

        public InjectedSquadTransactionFailure(string message, Exception innerException)
            : base(message, innerException)
        {
        }

        public InjectedSquadTransactionFailure(int sequence)
            : base($"Injected failure after transaction step {sequence}.")
        {
            Sequence = sequence;
        }

        public int Sequence { get; }
    }

    private sealed class RichTransactionFixture : IDisposable
    {
        private readonly TempDirectory _temp = new();

        private RichTransactionFixture()
        {
            const string installedBody = "installed conductor";
            const string obsoleteBody = "installed obsolete";
            Directory.CreateDirectory(System.IO.Path.Combine(Path, ".codex", "agents"));
            Directory.CreateDirectory(System.IO.Path.Combine(Path, ".codex", "preserved-empty"));
            string canonicalPath = System.IO.Path.Combine(Path, "canonical", "conductor.toml");
            Write(Path, "canonical/conductor.toml", installedBody);
            string agentDirectory = System.IO.Path.Combine(Path, ".codex", "agents");
            File.CreateSymbolicLink(
                System.IO.Path.Combine(agentDirectory, "conductor.toml"),
                System.IO.Path.GetRelativePath(agentDirectory, canonicalPath));
            Write(Path, ".cursor/agents/obsolete.md", obsoleteBody);
            Receipt = SquadDeploymentStateTests.Receipt(
                new SquadOwnedFile(
                    ".codex/agents/conductor.toml",
                    Digest(installedBody),
                    "codex",
                    false),
                new SquadOwnedFile(
                    ".cursor/agents/obsolete.md",
                    Digest(obsoleteBody),
                    "cursor",
                    false));
            WriteState(Path, Lock(), Receipt);
        }

        public string Path => _temp.Path;

        public SquadReceipt Receipt { get; }

        public static RichTransactionFixture Create() => new();

        public SquadDeploymentPlan CreateUpdatePlan() =>
            SquadDeploymentPlan.CreateUpdate(
                Path,
                SquadDeploymentScope.Project,
                Lock("1.2.4"),
                [
                    Rendered(".codex/agents/conductor.toml", "updated conductor"),
                    Rendered(".warp/roles/reviewer.md", "new reviewer", "warp")
                ],
                Receipt,
                [],
                replaceManaged: false,
                new FixedTimeProvider(InstalledAt.AddDays(1)));

        public void Dispose() => _temp.Dispose();
    }

    private sealed class TransactionFixture : IDisposable
    {
        private readonly TempDirectory _temp = new();

        private TransactionFixture()
        {
            RelativePath = ".codex/agents/conductor.toml";
            Write(Path, RelativePath, "installed body");
            Receipt = SquadDeploymentStateTests.Receipt(
                new SquadOwnedFile(
                    RelativePath,
                    Digest("installed body"),
                    "codex",
                    false));
            WriteState(Path, Lock(), Receipt);
        }

        public string Path => _temp.Path;

        public string RelativePath { get; }

        public SquadReceipt Receipt { get; }

        public static TransactionFixture Create() => new();

        public SquadDeploymentPlan CreateUpdatePlan() =>
            SquadDeploymentPlan.CreateUpdate(
                Path,
                SquadDeploymentScope.Project,
                Lock("1.2.4"),
                [Rendered(RelativePath, "updated body")],
                Receipt,
                [new SquadDegradation("codex", "conductor", "model-profile-narrowed")],
                replaceManaged: false,
                new FixedTimeProvider(InstalledAt.AddDays(1)));

        public void Dispose() => _temp.Dispose();
    }
}
