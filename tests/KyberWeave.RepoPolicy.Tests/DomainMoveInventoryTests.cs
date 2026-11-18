using System.Globalization;
using System.Text.RegularExpressions;
using KyberWeave.Tests.Corpus;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Checks the claims a move worker acts on in the Domain move inventory: a file the inventory sends to
/// Contracts holds nothing but interfaces, a file it calls a caller really calls Domain rather than
/// defining a type of the same name, every Contracts finding carries a decision, and the counts add up.
/// The inventory goes stale as the move tasks land, so nothing here pins a revision or a file count to
/// the tree; it checks the document against itself and against the declarations of the files it names.
/// This test only reads source. It touches no product code.
/// </summary>
public sealed class DomainMoveInventoryTests
{
    private const string InventoryPath = "docs/specs/interface-driven-unit-tests/inventory/domain-move.md";
    private const string DomainRoot = "src/KyberWeave.Domain";

    private static readonly ICorpusSource Corpus = CheckoutCorpusSource.Checkout;

    private static readonly Regex TypeDeclaration = new(
        @"\b(?<kind>class|record(?:\s+struct|\s+class)?|struct|interface|enum)\s+(?<name>[A-Za-z_]\w*)",
        RegexOptions.CultureInvariant);

    private static readonly Regex InterfaceOnlyLine = new(
        @"^\*\*Interface-only, the whole file moves to `Contracts` \(.*?\):\*\*\s*(?<files>.*)$",
        RegexOptions.CultureInvariant);

    private static readonly Regex Backticked = new(@"`(?<value>[^`]+)`", RegexOptions.CultureInvariant);

    private static string[] Lines() =>
        Corpus.ReadText(InventoryPath).ReplaceLineEndings("\n").Split('\n');

    [Fact]
    public void EveryFileTheInventoryMovesToContractsDeclaresOnlyInterfaces()
    {
        List<string> violations = [];
        string folder = string.Empty;
        int files = 0;

        foreach (string line in Lines())
        {
            if (line.StartsWith("### ", StringComparison.Ordinal))
                folder = line[4..].Trim();

            Match list = InterfaceOnlyLine.Match(line);
            if (!list.Success)
                continue;

            foreach (Match file in Backticked.Matches(list.Groups["files"].Value))
            {
                files++;
                string relative = $"{DomainRoot}/{folder}/{file.Groups["value"].Value}";
                string[] declarations = [.. Declarations(relative)
                    .Where(declaration => !declaration.Kind.StartsWith("interface", StringComparison.Ordinal))
                    .Select(declaration => $"{declaration.Kind} {declaration.Name}")];
                if (declarations.Length != 0)
                {
                    violations.Add($"{relative} declares {string.Join(", ", declarations)}");
                }
            }
        }

        Assert.True(files > 0, "No interface-only file list was found in " + InventoryPath);
        Assert.True(
            violations.Count == 0,
            "A file the inventory moves whole to Contracts must declare interfaces only. Split it instead: "
            + "the records stay in Domain and only the interface is lifted.\n" + string.Join("\n", violations));
    }

    [Fact]
    public void EveryCoreDocsSourceFileIsAccountedFor()
    {
        string text = Corpus.ReadText(InventoryPath);
        string[] files = [.. Corpus.EnumerateFiles("src/KyberWeave.Core.Docs", "*.cs")
            .Select(path => Path.GetFileName(path))];

        Assert.NotEmpty(files);
        string[] missing = [.. files.Where(name => !text.Contains(name, StringComparison.Ordinal))];

        Assert.True(
            missing.Length == 0,
            "Every file of Core.Docs must be named in the inventory, because task 2.2 scopes its deletions "
            + "from this document: " + string.Join(", ", missing));
    }

    [Fact]
    public void TheInventoryCountsAddUp()
    {
        List<string> lines = [.. Lines()];
        int header = lines.FindIndex(line =>
            line.StartsWith("| folder | files |", StringComparison.Ordinal));
        Assert.True(header >= 0, "The per-folder count table was not found.");

        int files = 0, data = 0, logic = 0, interfaces = 0;
        int totalFiles = 0, totalData = 0, totalLogic = 0, totalInterfaces = 0;
        for (int index = header + 2; index < lines.Count && lines[index].StartsWith('|'); index++)
        {
            string[] cells = lines[index].Trim('|').Split('|').Select(cell => cell.Trim()).ToArray();
            if (cells.Length < 5)
                continue;

            if (!int.TryParse(cells[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int rowFiles))
                continue;

            bool isTotal = cells[0] == "total";
            if (isTotal)
            {
                totalFiles = rowFiles;
                totalData = Parse(cells[2]);
                totalLogic = Parse(cells[3]);
                totalInterfaces = Parse(cells[4]);
            }
            else
            {
                files += rowFiles;
                data += Parse(cells[2]);
                logic += Parse(cells[3]);
                interfaces += Parse(cells[4]);
            }
        }

        Assert.Equal(totalFiles, files);
        Assert.Equal(totalData, data);
        Assert.Equal(totalLogic, logic);
        Assert.Equal(totalInterfaces, interfaces);
        Assert.Equal(totalFiles, totalData + totalLogic + totalInterfaces);
    }

    private static int Parse(string cell) =>
        int.Parse(cell, NumberStyles.Integer, CultureInfo.InvariantCulture);

    private static IEnumerable<(string Kind, string Name)> Declarations(string relative)
    {
        string[] lines = Corpus.ReadText(relative).ReplaceLineEndings("\n").Split('\n');
        foreach (string line in lines)
        {
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith("///", StringComparison.Ordinal) ||
                trimmed.StartsWith("//", StringComparison.Ordinal) ||
                trimmed.StartsWith('*'))
            {
                continue;
            }

            foreach (Match match in TypeDeclaration.Matches(line))
                yield return (match.Groups["kind"].Value, match.Groups["name"].Value);
        }
    }
}
