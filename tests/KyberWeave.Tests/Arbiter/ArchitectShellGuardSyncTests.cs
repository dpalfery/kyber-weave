using System.Text.Json;
using System.Text.RegularExpressions;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Core.Configuration;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Keeps the architect's documented command set and the shell guard from drifting apart. The
/// backticked spans in the architect agent file's command-set clause must equal the keys of
/// <see cref="ArchitectShellGuard.DocumentedForms"/>, and the guard must give each documented sample
/// the verdict the table records. Without this the document and the enforcement can disagree silently.
/// RED: <see cref="ArchitectShellGuard"/> does not exist yet.
/// </summary>
public sealed class ArchitectShellGuardSyncTests
{
    private const string Worktree = "/w";
    private const string CommandSetMarker = "Your shell is limited to this command set:";
    private const string CommandSetEnd = "before you edit it.";

    private static readonly string ArchitectFile = Path.Combine(
        KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad", "agents", "architect.md");

    private static readonly Regex BacktickSpan = new("`([^`]+)`", RegexOptions.CultureInvariant);

    private static readonly Lazy<IReadOnlyList<string>> PlanningDirectories = new(() =>
        ReadGuard.ProtectedDirectories(KyberWeaveConfigLoader.Load(KyberWeaveTestPaths.ToolRoot)));

    public static IEnumerable<object[]> DocumentedKeys() =>
        ArchitectShellGuard.DocumentedForms.Keys.Select(key => new object[] { key });

    [Fact]
    public void CommandSetClause_InTheArchitectFile_ListsExactlyTheDocumentedForms()
    {
        string text = File.ReadAllText(ArchitectFile);
        int markerStart = text.IndexOf(CommandSetMarker, StringComparison.Ordinal);
        Assert.True(markerStart >= 0, $"'{ArchitectFile}' has no '{CommandSetMarker}' clause.");

        int clauseStart = markerStart + CommandSetMarker.Length;
        int clauseEnd = text.IndexOf(CommandSetEnd, clauseStart, StringComparison.Ordinal);
        Assert.True(clauseEnd >= 0, $"The command-set clause in '{ArchitectFile}' is not closed by '{CommandSetEnd}'.");

        string clause = text[clauseStart..clauseEnd];
        HashSet<string> documented = BacktickSpan.Matches(clause)
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
        HashSet<string> forms = ArchitectShellGuard.DocumentedForms.Keys.ToHashSet(StringComparer.Ordinal);

        // A clause that parsed to nothing would make the set comparison below pass vacuously.
        Assert.NotEmpty(documented);
        Assert.Equal(27, documented.Count);

        string[] extra = documented.Except(forms, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        string[] missing = forms.Except(documented, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

        Assert.True(
            extra.Length == 0 && missing.Length == 0,
            $"The architect's command-set clause and ArchitectShellGuard.DocumentedForms disagree. " +
            $"Extra in architect.md (not in the guard): [{string.Join(", ", extra)}]. " +
            $"Missing from architect.md (in the guard, not documented): [{string.Join(", ", missing)}].");
    }

    [Fact]
    public void DocumentedKeys_IsNotEmptyAndHoldsTheTwentySevenForms()
    {
        // The member data the per-key theory runs over must be the whole table, not an empty or partial set.
        Assert.NotEmpty(DocumentedKeys());
        Assert.Equal(27, DocumentedKeys().Count());
        Assert.Equal(ArchitectShellGuard.DocumentedForms.Count, DocumentedKeys().Count());
    }

    [Theory]
    [MemberData(nameof(DocumentedKeys))]
    public void DocumentedSample_ReceivesTheDocumentedVerdict(string key)
    {
        DocumentedForm form = ArchitectShellGuard.DocumentedForms[key];

        ShellGuardResult result = ArchitectShellGuard.Check(
            "Bash",
            CommandInput(form.SampleCommand),
            Worktree,
            PlanningDirectories.Value);

        Assert.True(
            form.Allowed == result.Allowed,
            $"The documented form '{key}' (sample '{form.SampleCommand}') is documented as " +
            $"{(form.Allowed ? "allowed" : "denied")}, but the guard {(result.Allowed ? "allowed" : "denied")} it. " +
            $"Reason: {result.Reason}");
    }

    private static JsonElement CommandInput(string command) =>
        JsonSerializer.SerializeToElement(new { command });
}
