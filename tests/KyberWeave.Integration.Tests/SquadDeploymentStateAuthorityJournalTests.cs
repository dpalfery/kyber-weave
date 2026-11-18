using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json.Nodes;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Integration.Tests;

/// <summary>
/// The strict deployment-authority reader over a real interrupted transaction: integer and mis-cased enum tokens in a
/// receipt and a journal are refused before recovery touches the disk, and every supported journal shape round-trips.
/// Each test builds a real project tree, runs a real transaction part-way and recovers it from disk.
/// </summary>
public sealed class SquadDeploymentStateAuthorityJournalTests : SquadDeploymentStateTestBase
{
    [Fact]
    public void DeserializeAuthorityIntegerEnumTokensAreRejected()
    {
        using TransactionFixture fixture = TransactionFixture.Create();
        SquadStateStore store = Store(fixture.Path);
        List<string> failures = new();
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
        Dictionary<string, IReadOnlyList<int>> journalCases = new(StringComparer.Ordinal)
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

        Assert.True(
            failures.Count == 0,
            $"Integer enum tokens were accepted or reached filesystem recovery: {string.Join(", ", failures)}");
    }

    [Theory]
    [InlineData("preparing")]
    [InlineData("prepared")]
    [SuppressMessage(
        "Globalization",
        "CA1308:Normalize strings to uppercase",
        Justification = "The serializer emits scope tokens in lowercase; asserting an "
                        + "uppercased token would assert a format it never writes.")]
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
            // The fixture's file is dot-prefixed (it doubles as Project-scope fixture data
            // elsewhere), so forcing Scope to Global here must also carry a Layout that agrees
            // with that path's own classification (#91's read-side cross-check) — otherwise this
            // round-trip would hand the v2 writer a self-contradicting receipt of the test's own
            // making, not the serializer's.
            SquadReceiptLayout globalLayout = SquadDeploymentPlanRules.ClassifyGlobalLayout(fixture.Receipt.Files);
            SquadReceipt[] receipts =
            [
                fixture.Receipt with
                {
                    Scope = scope,
                    Layout = scope == SquadDeploymentScope.Global ? globalLayout : fixture.Receipt.Layout
                },
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
                Assert.Contains($"\"scope\": \"{scope.ToString().ToLowerInvariant()}\"", serialized,
                    StringComparison.Ordinal);

                // A Global receipt always serializes on the v2 schema (A3); Project stays on
                // whatever schema the fixture already carries.
                SquadReceipt expected = scope == SquadDeploymentScope.Global
                    ? receipt with { Schema = SquadStateSchemas.ReceiptSchemaV2 }
                    : receipt;
                AssertReceiptEqual(expected, store.DeserializeReceipt(serialized));
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

    [Fact]
    [SuppressMessage(
        "Globalization",
        "CA1308:Normalize strings to uppercase",
        Justification = "Derives the canonical lowercase token the serializer emits, which "
                        + "is the baseline the non-canonical variants are generated from.")]
    public void DeserializeAuthorityNoncanonicalEnumCaseIsRejected()
    {
        using RichTransactionFixture fixture = RichTransactionFixture.Create();
        string fixturePath = fixture.Path;
        SquadStateStore store = Store(fixturePath);
        List<string> failures = new();
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

        CheckpointFailingObserver observer = new(
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
        (string Property, string Value, int Occurrence)[] enumTokens =
            EnumerateAuthorityEnumTokens(canonicalIntent).ToArray();
        Assert.NotEmpty(enumTokens);
        foreach ((string Property, string Value, int Occurrence) token in enumTokens)
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

        Assert.True(
            failures.Count == 0,
            "Noncanonical authority enum spellings were accepted or reached filesystem " +
            $"recovery: {string.Join(", ", failures)}");
    }
}
